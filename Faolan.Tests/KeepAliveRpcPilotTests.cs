using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Verifies KeepAliveService: it builds an Rpc and hands it to client.SendAsync(Rpc). Drives the real
	// handlers through a capturing client and asserts the wire bytes match the expected frame - so the structured send
	// boundary is proven byte-preserving on a live handler, not just on the serializer in isolation.
	public class KeepAliveRpcPilotTests
	{
		private const uint Instance = 0x3BC035EC;

		[Fact]
		public async Task HandleLatencyCheck_FramesPongWithEchoedCounterAndFlag()
		{
			const uint counter = 0x00000007;
			var client = ClientAt(Instance);

			// Stimulus: an inbound LatencyCheck whose body starts with the counter the reply must echo. Parsed straight
			// into the Rpc the receive path now dispatches on.
			var incoming = Rpc.Parse(BuildFrame(RpcInterfaces.GameCharAgentType, 0x08, RpcInterfaces.GameCharInterfaceType, Instance, (ushort)GameCharInterfaceMethods.DispatchPong,
				[0x00, 0x00, 0x00, 0x07]));

			await KeepAliveService.HandleLatencyCheck(client, incoming, incoming.BodyReader());

			// The tick (3rd body word) is time-based, so assert structure + the deterministic fields, and that the
			// parser's independent crc recompute accepts the frame.
			var pong = Rpc.Parse(client.Sent!);
			Assert.True(Rpc.IsCrcValid(client.Sent!));
			Assert.Equal(RpcInterfaces.GameCharAgentType, pong.Sender.Type);
			Assert.Equal(RpcInterfaces.GameCharInterfaceType, pong.Receiver.Type);
			Assert.Equal(Instance, pong.Receiver.Instance);
			// The method is 0x82 = 130, so its varint needs a second group - that byte is part of the method field, not
			// a separate header tail.
			Assert.Equal((uint)GameCharInterfaceMethods.DispatchPong, pong.Method);
			var body = pong.BodyReader();
			Assert.Equal(counter, body.ReadUInt32());

			// The last two words are one 64-bit microsecond uptime counter, high word first (see KeepAliveService) -
			// so the high word is 0 until the process has been up 71.6 minutes, and the pair only ever moves forward.
			// The old assertion pinned the high word to a hardcoded 0x12, which is what let the pair run backwards.
			var high = body.ReadUInt32();
			var low = body.ReadUInt32();
			var first = ((ulong)high << 32) | low;
			Assert.Equal(0u, high);
			// Lower bound is 0, not 1: two reads of a monotonic clock inside one tick legitimately return the same
			// value, so a freshly started process can genuinely be up 0 microseconds. A backwards run is what this
			// gate is for, and that shows up as a huge unsigned value the upper bound catches.
			Assert.InRange(first, 0uL, (ulong)TimeSpan.FromHours(1).TotalMicroseconds);

			await KeepAliveService.HandleLatencyCheck(client, incoming, incoming.BodyReader());
			var second = Rpc.Parse(client.Sent!).BodyReader();
			second.ReadUInt32();
			var later = ((ulong)second.ReadUInt32() << 32) | second.ReadUInt32();
			Assert.True(later >= first, $"the uptime counter must advance monotonically: {first} then {later}");
		}

		[Fact]
		public async Task HandlePing_ReportsTheMeasuredTickRate()
		{
			var client = ClientAt(Instance);
			// A gauge fed a full window of 20 Hz ticks - so the frame rate on the wire has to be ~20, not the nominal
			// 10 and not a constant such as 0x42B32A07 (~89.6).
			var metrics = new TickMetrics(10.0);
			FillWindow(metrics, ticks: 20, overSeconds: 1.0);

			await new KeepAliveService(_ => metrics).HandlePing(client);

			// The expected frame: GameAgent -> GameInterface, GameServerStats, body [frameRate BE float][0][0]. The two
			// trailing words stay 0 - retail's are 0 in all 4243 captured GameServerStats.
			var stats = Rpc.Parse(client.Sent!);
			Assert.True(Rpc.IsCrcValid(client.Sent!));
			Assert.Equal(RpcInterfaces.GameAgentType, stats.Sender.Type);
			Assert.Equal(RpcInterfaces.GameInterfaceType, stats.Receiver.Type);
			Assert.Equal(Instance, stats.Receiver.Instance);
			Assert.Equal((uint)GameInterfaceMethods.DispatchGameServerStats, stats.Method);

			// The rate the gauge holds, exactly - the handler's contract is "report the measurement", so the assertion
			// must not restate the arithmetic (a rate asserted against a recomputed expectation would pass on a
			// handler that recomputed it wrongly the same way). Sleep-driven timing is why it is compared to the gauge
			// rather than to a literal; TickMetricsTests pins the arithmetic itself.
			var body = stats.BodyReader();
			var measured = metrics.Current.TicksPerSecond;
			Assert.True(measured > 0, "the gauge should have published a window"); // no target rate: sleep is a floor, not a schedule
			Assert.Equal((float)measured, body.ReadFloat());
			Assert.Equal(0u, body.ReadUInt32());
			Assert.Equal(0u, body.ReadUInt32());
		}

		[Fact]
		public async Task HandlePing_ReportsZeroWhenTheWorldLoopIsNotTicking()
		{
			var client = ClientAt(Instance);

			// A gauge that has never been fed: the tick loop has not started, or has died. That must read 0 rather than
			// a plausible-looking number - the whole point of the field is to make a stalled world visible in-client.
			await new KeepAliveService(_ => new TickMetrics(10.0)).HandlePing(client);

			Assert.Equal(0f, Rpc.Parse(client.Sent!).BodyReader().ReadFloat());
		}

		// Records `ticks` ticks spread across `overSeconds` of wall time so the gauge publishes a window. Real sleeping
		// is the only way to advance Stopwatch, so keep the window short.
		private static void FillWindow(TickMetrics metrics, int ticks, double overSeconds)
		{
			var perTick = TimeSpan.FromSeconds(overSeconds / ticks);
			for (var i = 0; i < ticks; i++)
			{
				Thread.Sleep(perTick);
				metrics.RecordTick(0.001);
			}

			// The window publishes on the tick that crosses it; one more guarantees a published snapshot even if the
			// sleeps ran slightly short.
			Thread.Sleep(perTick);
			metrics.RecordTick(0.001);
		}

		private static byte[] BuildFrame(uint senderType, uint senderInstance, uint receiverType, uint receiverInstance, uint method, byte[] body)
		{
			return new Rpc(Endpoints.Endpoint(senderType, senderInstance), Endpoints.Endpoint(receiverType, receiverInstance), method, (ReadOnlyMemory<byte>)body).Serialize();
		}

		private static CapturingClient ClientAt(uint characterId)
		{
			var client = new CapturingClient();
			client.Session.Character = new Character
			{
				Id = characterId
			};
			return client;
		}

		// Minimal INetworkClient that records the last-sent bytes. The default SendAsync(Rpc) on the interface
		// serializes and routes through SendAsync(byte[]), which we capture.
		private sealed class CapturingClient : INetworkClient
		{
			public INetworkClientSession Session { get; } = new NetworkClientSession();
			public byte[]? Sent { get; private set; }
			public string? IpAddress => "127.0.0.1";
			public bool IsStopped => false;

			public Task SendAsync(byte[] value, CancellationToken cancellationToken = default)
			{
				Sent = value;
				return Task.CompletedTask;
			}

			public void Start()
			{
			}

			public void Stop()
			{
			}
		}
	}
}
