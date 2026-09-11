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

		/// <summary>
		///     The structured send boundary: a handler builds an <see cref="Rpc" /> and hands it here instead of a
		///     hand-framed packet buffer.
		/// </summary>
		/// <remarks>
		///     <see cref="NetworkClient" /> overrides this to serialize once and trace the send directly from the Rpc
		///     model, with no re-parse. This default serves only lightweight <see cref="INetworkClient" /> test doubles
		///     that do not derive from <see cref="NetworkClient" />; it serializes - byte-identical, locked by
		///     <c>PacketFrameRoundTripTests</c> - and routes through the byte[] send.
		/// </remarks>
		Task SendAsync(Rpc rpc, CancellationToken cancellationToken = default)
		{
			return SendAsync(rpc.Serialize(), cancellationToken);
		}

		/// <summary>
		///     The chat tier's send boundary: a handler builds a <see cref="ChatServerMessage" /> and hands it here. Its
		///     frame is 4 bytes plus the body, so it serializes straight through rather than earning a transport path of
		///     its own.
		/// </summary>
		Task SendAsync(ChatServerMessage message, CancellationToken cancellationToken = default)
		{
			return SendAsync(message.Serialize(), cancellationToken);
		}

		Task SendAsync(byte[] value, CancellationToken cancellationToken = default);

		void Start();
		void Stop();
	}

	/// <summary>
	///     The concrete socket client: owns the socket, the send lock, the message framer and the receive loop -
	///     everything except how a completed frame is turned into something the listener dispatches on.
	/// </summary>
	/// <remarks>
	///     That one step is <see cref="DispatchFrame" />, which the two subclasses supply: a
	///     <see cref="ChatServerMessage" /> for the chat tier or the structured <see cref="Rpc" /> for the Conan
	///     protocol listeners, both parsed straight from the frame with no per-frame packet object.
	/// </remarks>
	public abstract class NetworkClient : INetworkClient
	{
		protected readonly ILogger Logger;
		protected readonly Socket Socket;
		private readonly IMessageFramer _messageFramer;
		private readonly IPacketTracer _tracer;
		private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

		private readonly byte[] _tcpBuffer = new byte[0xFFFF];

		private CancellationTokenSource? _cancellationTokenSource;
		private Task? _receiveTask;
		private int _stopped;

		// A short protocol name for the receive-loop error logs.
		protected abstract string ProtocolName { get; }

		public Func<INetworkClient, Task>? Disconnected { get; init; }

		/// <summary>
		///     Fired once after every TCP recv whose bytes produced at least one complete packet, when that recv's
		///     packets have all been dispatched - the "batch drained" boundary.
		/// </summary>
		/// <remarks>
		///     Lets a listener react to the end of a client's callback burst deterministically rather than by counting
		///     or debouncing packets.
		/// </remarks>
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

		/// <summary>
		///     The byte[] send path: trace the frame, which the tracer decodes, then put it on the wire. Used by the
		///     chat server, whose framing has no Rpc model, and by the <c>SendAsync(Rpc)</c> default for test doubles.
		/// </summary>
		public Task SendAsync(byte[] value, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(value);
			_tracer.Transmitted(value);
			return SendRawAsync(value, cancellationToken);
		}

		/// <summary>
		///     The structured send path: serialize once, trace directly from the Rpc model with no re-parse, then put
		///     it on the wire.
		/// </summary>
		public virtual Task SendAsync(Rpc rpc, CancellationToken cancellationToken = default)
		{
			var bytes = rpc.Serialize();
			// Stamp the wire size the serializer just determined, so the trace reads the whole line off the message, as
			// it does for an inbound frame that Rpc.Parse stamped.
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
			// Disconnect cleanup can be async (persistence, despawn broadcast). Stop() is a sync void called from sync
			// paths too, so the returned Task is fire-and-forget: Server's Disconnected wrapper awaits
			// ClientDisconnected internally and logs any fault.
			_ = Disconnected?.Invoke(this);
		}

		/// <summary>Turns one completed wire frame into the listener's dispatch.</summary>
		protected abstract Task DispatchFrame(byte[] frame);

		private async Task DataReceivedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		{
			// Frame the recv window in place (no copy) before dispatching: the span cannot outlive the call, and the loop
			// below awaits. Indexed, so a frame batch costs no enumerator either.
			var frames = _messageFramer.PushBytes(data.Span);

			for (var i = 0; i < frames.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();

				await DispatchFrame(frames[i]);
			}

			// The batch this recv carried is fully dispatched: signal the "batch drained" boundary.
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
				// A frame the wire format says cannot exist (a hostile or garbage length prefix). Unlike a socket error
				// this says something about the peer, so it is logged loudly rather than at Debug.
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
				// Socket.SendAsync can complete having written fewer bytes than requested when the send buffer is full.
				// Discarding the count would truncate a large frame mid-stream, and the client's framer would then
				// consume the following packets as the truncated frame's remainder. Loop until fully sent.
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

	/// <summary>
	///     The chat-protocol client: frames the chat wire format and parses each frame into the structured
	///     <see cref="ChatServerMessage" /> for the listener. That frame
	///     (<c>[u16 opcode][u16 bodyLen][body]</c>) has no Rpc sender/receiver/CRC model, so it keeps its own message
	///     type and framer.
	/// </summary>
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

	/// <summary>
	///     The Rpc-dispatching client: parses each frame straight into the structured <see cref="Rpc" /> via
	///     <c>Rpc.Parse</c> and hands it to the listener. Used by every Conan protocol listener, so the framer is fixed
	///     at <see cref="ConanMessageFramer" /> - every tier speaks the same wire framing.
	/// </summary>
	public sealed class RpcNetworkClient : NetworkClient
	{
		public Func<INetworkClient, Rpc, Task>? ReceivedRpc { get; init; }

		protected override string ProtocolName => "Rpc";

		public RpcNetworkClient(Socket socket, ILogger logger, IPacketTracer tracer) : base(socket, logger, new ConanMessageFramer(), tracer)
		{
		}

		protected override async Task DispatchFrame(byte[] frame)
		{
			// Rpc.Parse is the sole inbound parse and stamps Rpc.FrameLength with the total wire size. A corrupt frame
			// surfaces when a handler reads a field off it, which the dispatch try/catch turns into a client.Stop().
			if (ReceivedRpc != null)
				await ReceivedRpc(this, Rpc.Parse(frame));
		}
	}

	public static class ClientEndpointExtensions
	{
		/// <summary>
		///     The client's server-to-client interface endpoint for <paramref name="endpointType" />, addressed to the
		///     logged-in character - the receiver every Rpc send frames against.
		/// </summary>
		/// <param name="instance">
		///     Overrides the character id. The create flow has no committed Character yet, so it passes
		///     <c>Account.ClientInstance</c>, the temporary create-editor instance.
		/// </param>
		public static ProtoEndpoint Endpoint(this INetworkClient client, uint endpointType, uint? instance = null)
		{
			return Endpoints.Endpoint(endpointType, instance ?? client.Session.Character.Id);
		}
	}
}
