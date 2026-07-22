using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;

namespace Faolan.GameServer.World
{
	// Frames the 0x96B8DC59 StatUpdateIIR for a single addressed recipient - the host-side counterpart of
	// StatUpdateCodec (which owns the pure IIR body). Adds the RPC header on the world conversation (WorldFraming)
	// + length/hash. Used by everything that pushes a stat/vitals change to one client (combat, the regen tick, the
	// world-entry replay, GM commands); broadcasters that fan a stat out to nearby viewers call StatUpdateCodec.BuildBody
	// directly and frame it per recipient via SendWorldIir. Kept here (not in the codec) because it depends on the
	// host framing types (WorldFraming + INetworkClient).
	public static class VitalsPacket
	{
		// Builds a framed 0x96B8DC59 stat-update packet for an entity (defaults to the session's own client instance).
		public static byte[] BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return PacketSerializer.Serialize(new Rpc(WorldFraming.SenderEndpoint, client.EndpointOf(Endpoints.GameInterfaceType),
				GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(StatUpdateCodec.BuildBody(entityHandle, stats))));
		}
	}
}
