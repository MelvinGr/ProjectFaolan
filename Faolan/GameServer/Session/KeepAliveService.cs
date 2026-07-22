using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;

namespace Faolan.GameServer.Session
{
	// The connection keep-alive / latency replies the client polls with in-world:
	//   LatencyCheck (c2s 0x2085) -> Pong (0x2082): echo the counter + a server tick.
	//   Ping         (c2s 0x2005) -> GameServerStats (0x2009): a server-load readout float.
	// Both are fixed-shape replies with a tiny bit of per-reply data (the echoed counter / the tick). Extracted
	// out of the GameServerListener dispatch switch as a composed collaborator (#200); the two switch cases are
	// now thin one-liners delegating here. No deps - it just builds + sends on the client.
	//
	// PILOT for the structured send boundary (Gitea #406, phase 4): these build an Rpc and hand it to
	// client.SendAsync(Rpc) instead of a hand-framed PacketStream. The wire output is byte-identical (locked by
	// KeepAliveRpcPilotTests) - the sender/receiver/opcode are now the Rpc's fields, the body is built separately.
	public sealed class KeepAliveService
	{
		// LatencyCheck rides the GameCharAgent conversation (sub-id 0x08); Ping rides GameAgent (sub-id 0x08).
		private static readonly ProtoEndpoint LatencySender = Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x08);
		private static readonly ProtoEndpoint PingSender = Endpoints.Endpoint(Endpoints.GameAgentType, 0x08);

		// LatencyCheck (0x2085): reply body = [counter echoed][0x00000012][server tick]. The tick is a
		// monotonically increasing microsecond timer (gameplay dumps: ~5,000,000 per ~5s interval, ms precision).
		public static Task HandleLatencyCheck(INetworkClient client, ConanPacket packet)
		{
			var counter = packet.Body.ReadUInt32();
			var tick = unchecked((uint)(Environment.TickCount64 * 1000L));

			return client.SendAsync(new Rpc(LatencySender, Endpoints.Endpoint(Endpoints.GameCharInterfaceType, client.Session.Character.Id),
				(ushort)GameCharInterfaceOpcodes.DispatchPong, new byte[] { 0x01 },
				body => body.WriteUInt32(counter).WriteUInt32(0x00000012).WriteUInt32(tick)));
		}

		// Ping (0x2005, empty): reply on GameAgent with GameServerStats. Body = [float][0][0]; retail's leading
		// float is a varying server-load/fps metric (~80-83 in dumps) - we have no equivalent, so a fixed
		// plausible value (0x42B32A07 = ~89.6) is sent. The client just treats it as a readout.
		public static Task HandlePing(INetworkClient client)
		{
			return client.SendAsync(new Rpc(PingSender, Endpoints.Endpoint(Endpoints.GameInterfaceType, client.Session.Character.Id),
				(ushort)GameInterfaceOpcodes.DispatchGameServerStats,
				body => body.WriteUInt32(0x42B32A07).WriteUInt32(0).WriteUInt32(0)));
		}
	}
}
