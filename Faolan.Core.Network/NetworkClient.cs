using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
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
		///     True once the connection has stopped. A loop or a delayed action that outlives the packet that started it
		///     (the energy mirror, the trance meter, a channel's ticks) checks this to end with the connection: a send
		///     to a stopped connection is dropped rather than thrown, so a send failing is not how such a loop learns
		///     the player has gone.
		/// </summary>
		bool IsStopped { get; }

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
	///     The concrete socket client: owns the socket, the outbound queue, the message framer and the receive loop -
	///     everything except how a completed frame is turned into something the listener dispatches on.
	/// </summary>
	/// <remarks>
	///     That one step is <see cref="DispatchFrame" />, which the two subclasses supply: a
	///     <see cref="ChatServerMessage" /> for the chat tier or the structured <see cref="Rpc" /> for the Conan
	///     protocol listeners, both parsed straight from the frame with no per-frame packet object.
	///     <para>
	///         A send queues the frame and returns; one loop per client writes the queue to the socket in order. So a
	///         sender - a zone's loop above all - never waits on this client's socket: a client that stops reading
	///         (its TCP window full, the socket still open) used to park whoever was sending to it, the world tick
	///         included. Each frame is still its own socket write, so the per-segment framing the world-entry burst
	///         needs (see <c>ServerBase</c>'s NoDelay) is unchanged.
	///     </para>
	/// </remarks>
	public abstract class NetworkClient : INetworkClient
	{
		// Frames queued for the socket. A client that has not drained this many is not reading at all, and is
		// stopped rather than left to grow the queue without bound.
		private const int OutboundCapacity = 8192;

		protected readonly ILogger Logger;
		protected readonly Socket Socket;
		private readonly IMessageFramer _messageFramer;
		private readonly IPacketTracer _tracer;

		private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundCapacity)
		{
			SingleReader = true,
			FullMode = BoundedChannelFullMode.Wait
		});

		private readonly byte[] _tcpBuffer = new byte[0xFFFF];

		private CancellationTokenSource? _cancellationTokenSource;
		// The receive and send loops together; each handles its own end, so this is only the "started" mark.
		private Task? _loops;
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

		public bool IsStopped => Volatile.Read(ref _stopped) == 1;

		protected NetworkClient(Socket socket, ILogger logger, IMessageFramer messageFramer, IPacketTracer tracer)
		{
			Socket = socket;
			Logger = logger;
			_messageFramer = messageFramer;
			_tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
		}

		/// <summary>
		///     The byte[] send path: trace the frame, which the tracer decodes, then queue it for the socket. Used by the
		///     chat server, whose framing has no Rpc model, and by the <c>SendAsync(Rpc)</c> default for test doubles.
		/// </summary>
		/// <remarks>Completes once the frame is queued, not once it is on the wire (see the class remarks).</remarks>
		public Task SendAsync(byte[] value, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(value);
			_tracer.Transmitted(value);
			Enqueue(value);
			return Task.CompletedTask;
		}

		/// <summary>
		///     The structured send path: serialize once, trace directly from the Rpc model with no re-parse, then queue
		///     it for the socket.
		/// </summary>
		/// <remarks>Completes once the frame is queued, not once it is on the wire (see the class remarks).</remarks>
		public virtual Task SendAsync(Rpc rpc, CancellationToken cancellationToken = default)
		{
			var bytes = rpc.Serialize();
			// Stamp the wire size the serializer just determined, so the trace reads the whole line off the message, as
			// it does for an inbound frame that Rpc.Parse stamped.
			_tracer.Transmitted(rpc with
			{
				FrameLength = bytes.Length
			});
			Enqueue(bytes);
			return Task.CompletedTask;
		}

		public void Start()
		{
			if (_loops != null)
				return;

			_cancellationTokenSource = new CancellationTokenSource();
			_loops = Task.WhenAll(ReceiveLoopAsync(_cancellationTokenSource.Token), SendLoopAsync(_cancellationTokenSource.Token));
		}

		public void Stop()
		{
			if (Interlocked.Exchange(ref _stopped, 1) == 1)
				return;

			// Whatever is still queued is dropped with the connection, like any send made after this point.
			_outbound.Writer.TryComplete();

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

		/// <remarks>
		///     A send to a connection that has gone is dropped rather than thrown. Fan-outs (a despawn, the tick's regen,
		///     a chat line) reach every viewer the world still lists, and one whose socket closed a moment ago would
		///     otherwise throw into the sender: the rest of the fan-out would be skipped, and on a disconnect so would
		///     the leaver's own teardown. The gone connection's own disconnect cleanup runs either way.
		/// </remarks>
		private void Enqueue(byte[] frame)
		{
			if (IsStopped)
				return;

			if (!_outbound.Writer.TryWrite(frame) && !IsStopped)
			{
				Logger.LogWarning("Dropping {PacketType} client: {Count} frames queued and none drained.", ProtocolName, OutboundCapacity);
				Stop();
			}
		}

		// The one writer: frames go out in the order they were queued, each as its own socket write, and a short write
		// is continued rather than dropped - discarding the count would truncate a large frame mid-stream, and the
		// client's framer would then read the following packets as its remainder.
		private async Task SendLoopAsync(CancellationToken cancellationToken)
		{
			try
			{
				await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken))
				{
					var sent = 0;
					while (sent < frame.Length)
					{
						var n = await Socket.SendAsync(frame.AsMemory(sent), SocketFlags.None, cancellationToken);
						if (n <= 0)
							throw new SocketException((int)SocketError.ConnectionReset);

						sent += n;
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// Normal shutdown.
			}
			catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
			{
				// The peer went away mid-send: stop the connection, which runs its disconnect cleanup.
				Logger.LogDebug(ex, "Send failed for {PacketType}; closing client.", ProtocolName);
				Stop();
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
