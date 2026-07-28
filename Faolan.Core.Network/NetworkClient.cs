using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Packets.ChatServer;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
	public interface INetworkClient
	{
		INetworkClientSession Session { get; }

		string? IpAddress { get; }

		// The structured send boundary (Gitea #406): a handler builds an Rpc and hands it here instead of a hand-framed
		// packet buffer. The real client (NetworkClient) implements this to serialize ONCE and trace the send directly
		// from the Rpc model - no re-parse (phase 6). This default only serves lightweight INetworkClient test doubles
		// that don't derive from NetworkClient; it serializes (byte-identical, locked by PacketFrameRoundTripTests) and
		// routes through the byte[] send.
		Task SendAsync(Rpc rpc, CancellationToken cancellationToken = default)
		{
			return SendAsync(rpc.Serialize(), cancellationToken);
		}

		// The chat tier's send boundary, the same shape one tier down: a handler builds a ChatServerMessage and hands it
		// here. Its frame is 4 bytes plus the body, so it serializes straight through rather than earning a transport
		// path of its own (the trace decodes those 4 bytes back, which costs nothing).
		Task SendAsync(ChatServerMessage message, CancellationToken cancellationToken = default)
		{
			return SendAsync(message.Serialize(), cancellationToken);
		}

		Task SendAsync(byte[] value, CancellationToken cancellationToken = default);

		void Start();
		void Stop();
	}

	// The concrete socket client: owns the socket, the send lock, the message framer and the receive loop - everything
	// EXCEPT how a completed frame is turned into something the listener dispatches on. That one step is DispatchFrame,
	// which the two subclasses supply: a ChatServerMessage (ChatServer) or the structured Rpc (the Conan protocol
	// listeners) - both parsed straight from the frame, with no per-frame packet object on either side.
	public abstract class NetworkClient : INetworkClient
	{
		protected readonly ILogger Logger;
		protected readonly Socket Socket;
		private readonly IMessageFramer _messageFramer;
		private readonly IPacketTracer _tracer;
		private readonly SemaphoreSlim _sendLock = new(1, 1);

		private readonly byte[] _tcpBuffer = new byte[0xFFFF];

		private CancellationTokenSource? _cancellationTokenSource;
		private Task? _receiveTask;
		private int _stopped;

		// A short protocol name for the receive-loop error logs (was typeof(TPacket).Name).
		protected abstract string ProtocolName { get; }

		public Func<INetworkClient, Task>? Disconnected { get; init; }

		// Fired once after every TCP recv whose bytes produced at least one complete packet, when that recv's
		// packets have all been dispatched (the "batch drained" boundary). Lets a listener react to the END of a
		// client's callback burst deterministically instead of counting/debouncing packets (#197). Default: unused.
		public Func<INetworkClient, Task>? ReceivedBatchComplete { get; init; }

		public INetworkClientSession Session { get; } = new NetworkClientSession();

		public string? IpAddress => field ??= (Socket.RemoteEndPoint as IPEndPoint)?.Address.ToString();

		protected NetworkClient(Socket socket, ILogger logger, IMessageFramer messageFramer, IPacketTracer tracer)
		{
			Socket = socket;
			Logger = logger;
			_messageFramer = messageFramer;
			_tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
		}

		// The byte[] send path: trace the frame (the tracer decodes it), then put it on the wire. Used by the chat
		// server (its own framing has no Rpc model) and by the SendAsync(Rpc) default for lightweight test doubles.
		public Task SendAsync(byte[] value, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(value);
			_tracer.Transmitted(value);
			return SendRawAsync(value, cancellationToken);
		}

		// The structured send path (Gitea #406 phase 6): serialize ONCE, trace directly from the Rpc model (no
		// re-parse), then put it on the wire.
		public virtual Task SendAsync(Rpc rpc, CancellationToken cancellationToken = default)
		{
			var bytes = rpc.Serialize();
			// Stamp the wire size the serializer just determined, so the trace reads the whole line off the message -
			// exactly as it does for an inbound frame, which Rpc.Parse stamped.
			_tracer.Transmitted(rpc with
			{
				FrameLength = bytes.Length
			});
			return SendRawAsync(bytes, cancellationToken);
		}

		public void Start()
		{
			if (_receiveTask != null)
				return;

			_cancellationTokenSource = new CancellationTokenSource();
			_receiveTask = ReceiveLoopAsync(_cancellationTokenSource.Token);
		}

		public void Stop()
		{
			if (Interlocked.Exchange(ref _stopped, 1) == 1)
				return;

			try
			{
				_cancellationTokenSource?.Cancel();
			}
			catch
			{
				// Ignore cancellation/disposing races.
			}

			try
			{
				Socket.Shutdown(SocketShutdown.Both);
			}
			catch
			{
				// Socket may already be closed/reset.
			}

			Socket.Close();
			// Disconnect cleanup can be async (persistence / despawn broadcast). Stop() is a sync void called from
			// sync paths too, so fire-and-forget the returned Task - Server's Disconnected wrapper awaits
			// ClientDisconnected internally and logs any fault.
			_ = Disconnected?.Invoke(this);
		}

		// Turn one completed wire frame into the listener's dispatch. Packet subclass: factory.Create + IsValid gate;
		// Rpc subclass: Rpc.Parse.
		protected abstract Task DispatchFrame(byte[] frame);

		private async Task DataReceivedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		{
			// Frame the recv window in place (no copy), THEN dispatch: the span can't outlive the call, and the loop
			// below awaits. Indexed, so a frame batch costs no enumerator either.
			var frames = _messageFramer.PushBytes(data.Span);

			for (var i = 0; i < frames.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();

				await DispatchFrame(frames[i]);
			}

			// The batch this recv carried is fully dispatched - signal the "batch drained" boundary (#197).
			if (frames.Count > 0 && ReceivedBatchComplete != null)
				await ReceivedBatchComplete(this);
		}

		private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
		{
			try
			{
				while (!cancellationToken.IsCancellationRequested)
				{
					var bytesRead = await Socket.ReceiveAsync(_tcpBuffer, SocketFlags.None, cancellationToken);
					if (bytesRead == 0)
					{
						Stop();
						return;
					}

					var buffer = _tcpBuffer.AsMemory(0, bytesRead);
					await DataReceivedAsync(buffer, cancellationToken);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// Normal shutdown.
			}
			catch (ObjectDisposedException)
			{
				// Normal shutdown.
			}
			catch (SocketException ex)
			{
				Logger.LogDebug(ex, "Socket receive failed for {PacketType}; closing client.", ProtocolName);
				Stop();
			}
			catch (InvalidDataException ex)
			{
				// A frame the wire format says cannot exist (a hostile/garbage length prefix). Unlike a socket error
				// this says something about the PEER, so it is logged loud rather than at Debug.
				Logger.LogWarning("Dropping {PacketType} client {IpAddress}: {Reason}", ProtocolName, IpAddress, ex.Message);
				Stop();
			}
			catch (Exception ex)
			{
				Logger.LogDebug(ex, "Receive processing failed for {PacketType}; closing client.", ProtocolName);
				Stop();
			}
		}

		private async Task SendRawAsync(byte[] value, CancellationToken cancellationToken)
		{
			await _sendLock.WaitAsync(cancellationToken);
			try
			{
				// Socket.SendAsync can complete having written FEWER bytes than requested (send buffer full); the
				// old code discarded the count, silently truncating large frames mid-stream. The client's framer
				// then consumed the FOLLOWING packets as the truncated frame's remainder and misparsed everything
				// after ("Wrong IIR_t version <garbage>" on the 10 KB post-load inventory snapshot, which goes out
				// right behind the multi-hundred-KB world burst - isolated 2026-06-12). Loop until fully sent.
				var sent = 0;
				while (sent < value.Length)
				{
					var n = await Socket.SendAsync(value.AsMemory(sent), SocketFlags.None, cancellationToken);
					if (n <= 0)
						throw new SocketException((int)SocketError.ConnectionReset);

					sent += n;
				}
			}
			finally
			{
				_sendLock.Release();
			}
		}
	}

	// The chat-protocol client: frames the chat wire format and parses each frame into the structured ChatServerMessage
	// for the listener. That frame ([u16 opcode][u16 bodyLen][body]) has no Rpc sender/receiver/CRC model, so it keeps
	// its own message type + framer.
	public sealed class ChatNetworkClient : NetworkClient
	{
		public Func<INetworkClient, ChatServerMessage, Task>? ReceivedPacket { get; init; }

		protected override string ProtocolName => nameof(ChatServerMessage);

		public ChatNetworkClient(Socket socket, ILogger logger, IPacketTracer tracer) : base(socket, logger, new ChatServerMessageFramer(), tracer)
		{
		}

		protected override async Task DispatchFrame(byte[] frame)
		{
			if (ReceivedPacket != null)
				await ReceivedPacket(this, ChatServerMessage.Parse(frame));
		}
	}

	// The Rpc-dispatching client (Gitea #406/#407): parses each frame straight into the structured Rpc via
	// Rpc.Parse - no per-frame packet object - and hands it to the listener. Used by every Conan protocol listener, so
	// the framer is fixed at ConanMessageFramer (every tier speaks the same wire framing).
	public sealed class RpcNetworkClient : NetworkClient
	{
		public Func<INetworkClient, Rpc, Task>? ReceivedRpc { get; init; }

		protected override string ProtocolName => "Rpc";

		public RpcNetworkClient(Socket socket, ILogger logger, IPacketTracer tracer) : base(socket, logger, new ConanMessageFramer(), tracer)
		{
		}

		protected override async Task DispatchFrame(byte[] frame)
		{
			// Rpc.Parse is the SOLE inbound parse (Gitea #406/#407) - straight to the structured Rpc, no
			// ConanPacket. It stamps Rpc.FrameLength with the total wire size. (The old ConanPacket path threw on a bad
			// CRC; a corrupt frame now surfaces when a handler reads a field off it, which the dispatch try/catch turns
			// into the same client.Stop().)
			if (ReceivedRpc != null)
				await ReceivedRpc(this, Rpc.Parse(frame));
		}
	}

	public static class ClientEndpointExtensions
	{
		// The client's server->client INTERFACE endpoint (ProtoEndpoint) for `endpointType`, addressed to the logged-in
		// character (e.g. client.Endpoint(Endpoints.GameCharInterfaceType)) — the receiver every Rpc send frames against.
		// Pass `instance` to override with a non-character id — the create flow has no committed Character yet, so it
		// passes Account.ClientInstance (the temporary create-editor instance).
		public static ProtoEndpoint Endpoint(this INetworkClient client, uint endpointType, uint? instance = null)
		{
			return Endpoints.Endpoint(endpointType, instance ?? client.Session.Character.Id);
		}
	}
}
