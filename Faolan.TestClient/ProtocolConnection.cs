using System.Net.Sockets;
using Faolan.Core.Network.Conan;

namespace Faolan.TestClient
{
	// A thin TCP wrapper that speaks the Conan wire protocol: send finalized PacketStream bytes, and read replies
	// through the SAME ConanMessageFramer the server uses (length-prefixed, zlib-aware), yielding parsed ConanPackets.
	public sealed class ProtocolConnection : IDisposable
	{
		private readonly ConanMessageFramer _framer = new ConanMessageFramer();
		private readonly Queue<ConanPacket> _pending = new Queue<ConanPacket>();
		private readonly TcpClient _tcp = new TcpClient();
		private NetworkStream _stream = null!;

		public void Dispose()
		{
			_stream?.Dispose();
			_tcp.Dispose();
		}

		public async Task ConnectAsync(string host, int port)
		{
			await _tcp.ConnectAsync(host, port);
			_stream = _tcp.GetStream();
		}

		// Read the next complete packet, blocking until one frames (or the timeout / socket close).
		public async Task<ConanPacket> ReadAsync(TimeSpan timeout)
		{
			using var cts = new CancellationTokenSource(timeout);
			var buffer = new byte[16 * 1024];
			while (_pending.Count == 0)
			{
				var n = await _stream.ReadAsync(buffer, cts.Token);
				if (n == 0)
					throw new IOException("connection closed by peer");

				foreach (var framed in _framer.PushBytes(buffer, n))
					_pending.Enqueue(new ConanPacket(framed));
			}

			return _pending.Dequeue();
		}

		// Read until a packet with the given opcode arrives (logging the ones skipped along the way).
		public async Task<ConanPacket> ReadUntilAsync(ushort opcode, TimeSpan timeout, Action<ConanPacket>? onSkip = null)
		{
			var deadline = DateTime.UtcNow + timeout;
			while (true)
			{
				var remaining = deadline - DateTime.UtcNow;
				if (remaining <= TimeSpan.Zero)
					throw new TimeoutException($"no packet 0x{opcode:X4} within {timeout.TotalSeconds:F0}s");

				var p = await ReadAsync(remaining);
				if (p.Opcode == opcode)
					return p;

				onSkip?.Invoke(p);
			}
		}

		public Task SendAsync(byte[] packetBytes)
		{
			return _stream.WriteAsync(packetBytes).AsTask();
		}
	}
}
