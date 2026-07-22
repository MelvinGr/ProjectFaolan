using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
	public interface INetworkClient
	{
		INetworkClientSession Session { get; }

		string? IpAddress { get; }

		void Send(byte[] value);
		Task SendAsync(byte[] value, CancellationToken cancellationToken = default);

		// The structured send boundary (Gitea #406, phase 4): a handler builds an Rpc and hands it here instead of a
		// hand-framed PacketStream. Serializes to the wire frame (byte-identical to the PacketStream builder, locked by
		// PacketFrameRoundTripTests) and sends. Logging still runs off the serialized bytes for now; moving it onto the
		// Rpc model (no re-parse) is phase 6.
		Task SendAsync(Rpc rpc, CancellationToken cancellationToken = default)
		{
			return SendAsync(PacketSerializer.Serialize(rpc), cancellationToken);
		}

		void Start();
		void Stop();
	}

	public abstract class NetworkClient : INetworkClient
	{
		protected readonly ILogger Logger;
		protected readonly Socket Socket;

		public Func<INetworkClient, Packet, Task>? ReceivedPacket { get; init; }
		public Func<INetworkClient, Task>? Disconnected { get; init; }

		// Fired once after every TCP recv whose bytes produced at least one complete packet, when that recv's
		// packets have all been dispatched (the "batch drained" boundary). Lets a listener react to the END of a
		// client's callback burst deterministically instead of counting/debouncing packets (#197). Default: unused.
		public Func<INetworkClient, Task>? ReceivedBatchComplete { get; init; }

		public INetworkClientSession Session { get; } = new NetworkClientSession();

		public string? IpAddress => field ??= (Socket.RemoteEndPoint as IPEndPoint)?.Address.ToString();

		protected NetworkClient(Socket socket, ILogger logger)
		{
			Socket = socket;
			Logger = logger;
		}

		public abstract void Send(byte[] value);
		public abstract Task SendAsync(byte[] value, CancellationToken cancellationToken = default);

		public abstract void Start();
		public abstract void Stop();
	}

	public sealed class NetworkClient<TPacket, TMessageFramer, TPacketFactory> : NetworkClient
		where TPacket : Packet where TMessageFramer : IMessageFramer, new() where TPacketFactory : IPacketFactory<TPacket>, new()
	{
		private readonly byte[] _tcpBuffer = new byte[0xFFFF];
		private readonly IMessageFramer _messageFramer;
		private readonly IPacketFactory<TPacket> _packetFactory;
		private readonly IPacketLogger _outgoingPacketLogger;
		private readonly SemaphoreSlim _sendLock = new(1, 1);

		private CancellationTokenSource? _cancellationTokenSource;
		private int _stopped;
		private Task? _receiveTask;

		public NetworkClient(Socket socket, ILogger logger, IPacketLogger outgoingPacketLogger) : base(socket, logger)
		{
			_messageFramer = new TMessageFramer();
			_packetFactory = new TPacketFactory();
			_outgoingPacketLogger = outgoingPacketLogger ?? throw new ArgumentNullException(nameof(outgoingPacketLogger));
		}

		public override void Send(byte[] value)
		{
			ArgumentNullException.ThrowIfNull(value);

			_outgoingPacketLogger.Log(value);

			_sendLock.Wait();
			try
			{
				// Socket.Send can write FEWER bytes than requested when the send buffer is full; loop until the
				// whole frame is on the wire (see SendAsync for the bug this fixes).
				var sent = 0;
				while (sent < value.Length)
				{
					var n = Socket.Send(value, sent, value.Length - sent, SocketFlags.None);
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

		public override async Task SendAsync(byte[] value, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(value);

			_outgoingPacketLogger.Log(value);

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

		public override void Start()
		{
			if (_receiveTask != null)
				return;

			_cancellationTokenSource = new CancellationTokenSource();
			_receiveTask = ReceiveLoopAsync(_cancellationTokenSource.Token);
		}

		public override void Stop()
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

		private async Task DataReceivedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		{
			var dispatchedAny = false;
			foreach (var frame in _messageFramer.PushBytes(data.ToArray(), data.Length))
			{
				cancellationToken.ThrowIfCancellationRequested();

				var packet = _packetFactory.Create(frame);

				if (packet?.IsValid != true)
					throw new InvalidOperationException($"{typeof(TPacket).Name} was invalid");

				if (ReceivedPacket != null)
					await ReceivedPacket(this, packet);

				dispatchedAny = true;
			}

			// The batch this recv carried is fully dispatched - signal the "batch drained" boundary (#197).
			if (dispatchedAny && ReceivedBatchComplete != null)
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
				Logger.LogDebug(ex, "Socket receive failed for {PacketType}; closing client.", typeof(TPacket).Name);
				Stop();
			}
			catch (Exception ex)
			{
				Logger.LogDebug(ex, "Receive processing failed for {PacketType}; closing client.", typeof(TPacket).Name);
				Stop();
			}
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
