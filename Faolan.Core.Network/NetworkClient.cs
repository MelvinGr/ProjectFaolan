using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
	public interface INetworkClient
	{
		INetworkClientSession Session { get; }

		string IpAddress { get; }

		void Send(byte[] value);
		Task SendAsync(byte[] value, CancellationToken cancellationToken = default);

		void Start();
		void Stop();
	}

	public abstract class NetworkClient : INetworkClient
	{
		protected readonly ILogger Logger;
		protected readonly Socket Socket;
		public Action<INetworkClient>? Disconnected { get; init; }

		public Func<INetworkClient, Packet, Task>? ReceivedPacket { get; init; }

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

	public sealed class NetworkClient<TPacket, TMessageFramer, TPacketFactory> : NetworkClient where TPacket : Packet where TMessageFramer : IMessageFramer, new() where TPacketFactory : IPacketFactory<TPacket>, new()
	{
		private readonly byte[] _tcpBuffer = new byte[0xFFFF];
		private readonly IMessageFramer _messageFramer;
		private readonly IPacketFactory<TPacket> _packetFactory;
		private readonly IPacketLogger _outgoingPacketLogger;
		private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

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
			LogRawPacket("<=", value);

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
			LogRawPacket("<=", value);

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
			Disconnected?.Invoke(this);
		}

		private async Task DataReceivedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		{
			foreach (var frame in _messageFramer.PushBytes(data.ToArray(), data.Length))
			{
				cancellationToken.ThrowIfCancellationRequested();
				LogRawPacket("=>", frame);

				var packet = _packetFactory.Create(frame);

				if (packet?.IsValid != true)
					throw new InvalidOperationException($"{typeof(TPacket).Name} was invalid");

				if (ReceivedPacket != null)
					await ReceivedPacket(this, packet);
			}
		}

		private void LogRawPacket(string direction, byte[] bytes)
		{
			if (!Logger.IsEnabled(LogLevel.Debug))
				return;

			Logger.LogDebug("{Direction} {PacketType} raw ({Length} bytes): {Bytes}", direction, typeof(TPacket).Name, bytes.Length, bytes.ToHex());
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

					// zlib compression marker: 0x80000005
					if (buffer.Length >= 4 && buffer.Span[0] == 0x80 && buffer.Span[1] == 0x00 && buffer.Span[2] == 0x00 && buffer.Span[3] == 0x05)
					{
						// TODO: handle compressed stream.
						// buffer = Decompress(buffer[9..]);

						continue;
					}

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
}
