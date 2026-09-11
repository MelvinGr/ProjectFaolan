using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Session
{
	// The connection keep-alive / latency replies the client polls with in-world:
	//   LatencyCheck (c2s 0x2085) -> Pong (0x2082): echo the counter + the server's 64-bit uptime counter.
	//   Ping         (c2s 0x2005) -> GameServerStats (0x2009): a server-load readout float.
	// Both are fixed-shape replies with a little per-reply data (the echoed counter, the tick, the measured tick rate).
	// The only dependency is the simulation's TickMetrics gauge.
	//
	// Both build an Rpc and hand it to client.SendAsync(Rpc) rather than hand-framing a PacketStream; the wire output
	// is byte-identical, locked by KeepAliveRpcPilotTests.
	public sealed class KeepAliveService
	{
		// LatencyCheck rides the GameCharAgent conversation (sub-id 0x08); Ping rides GameAgent (sub-id 0x08).
		private static readonly ProtoEndpoint LatencySender = Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x08);
		private static readonly ProtoEndpoint PingSender = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08);

		// The simulation loop's live health readout - the source of the frame rate the Ping reply reports. Taken as the
		// narrow metric object rather than the WorldSimulation so this stays a keep-alive service that reads a gauge.
		private readonly TickMetrics _metrics;

		public KeepAliveService(TickMetrics metrics)
		{
			_metrics = metrics;
		}

		// LatencyCheck (0x2085): reply body = [counter echoed][uptime hi][uptime lo].
		//
		// The last two words are one 64-bit microsecond counter, high word first - not a constant and a tick. Over the
		// corpus, (hi << 32 | lo) advances at exactly wall-clock rate across every captured pong (182/186 and 23/23 in
		// the two NecroL80 sessions) and its absolute value is the retail server's uptime - 2.60 and 4.73 days in those
		// two. That is also why the high word looks like a per-session constant within one capture and takes 83
		// different values across the corpus: it ticks once per 71.6 minutes.
		//
		// This reply is the only thing that sets the client's `ServerTime_c`, and its value divided by 1000 is the
		// clock every absolute wire expiry is measured against, today the MagicBuffIIR apply's @46 stamp. That is why
		// the origin lives in `ServerUptimeClock` rather than a private field here: the two senders have to be the same
		// clock, and a second origin would desync every buff resume with nothing on the wire looking wrong.
		public static Task HandleLatencyCheck(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var counter = body.ReadUInt32();
			var uptime = ServerUptimeClock.Microseconds;

			return client.SendAsync(new Rpc(LatencySender, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchPong,
				b => b.WriteUInt32(counter)
					.WriteUInt32((uint)(uptime >> 32))
					.WriteUInt32((uint)uptime)));
		}

		// Ping (0x2005, empty): reply on GameAgent with GameServerStats. Client signature (docs/re/aoc-client-rpc-methods.md)
		// is DispatchGameServerStats(nGSFrameRate, uGSFrameCount, nGSCpuUsage) -> body = [float BE][u32][u32]; the
		// client renders the first field as "Server Framerate" in its developer overlay.
		//
		// The frame rate is measured - the simulation loop's achieved ticks per second, from TickMetrics - rather than
		// a constant, because this is the one field on the overlay that exists to show server health. The nominal
		// cadence here is 10 Hz, so a healthy reading is ~10.0, an order of magnitude under retail's ~99 because
		// retail's was a much finer server frame; what matters is the reading against its own nominal.
		//
		// The other two fields stay zero because retail's are: all 4243 s2c GameServerStats in the capture corpus carry
		// frame count 0 and CPU usage 0. Tick count and tick busy-fraction are exposed server-side
		// (TickMetrics.Current) rather than invented onto the wire.
		public Task HandlePing(INetworkClient client)
		{
			var frameRate = (float)_metrics.Current.TicksPerSecond;

			return client.SendAsync(new Rpc(PingSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchGameServerStats,
				b => b.WriteFloat(frameRate).WriteUInt32(0).WriteUInt32(0)));
		}
	}
}
