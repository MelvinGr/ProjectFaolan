using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;

namespace Faolan.GameServer.World
{
	// Frames the 0x96B8DC59 StatUpdateIIR for a single addressed recipient - the host-side counterpart of
	// StatUpdateCodec (which owns the pure IIR body). Adds the RPC header on the world conversation (WorldSenderEndpoint)
	// + length/hash. Used by everything that pushes a stat/vitals change to one client (combat, the regen tick, the
	// world-entry replay, GM commands); broadcasters that fan a stat out to nearby viewers call StatUpdateCodec.Build
	// directly and frame it per recipient via WorldIir.Send. Kept here (not in the codec) because it depends on the
	// host framing types (WorldSenderEndpoint + INetworkClient).
	public static class VitalsPacket
	{
		// The framed 0x96B8DC59 stat-update packet for an entity (defaults to the session's own client instance), as an
		// Rpc the transport serializes + logs once (no re-parse).
		public static Rpc BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(StatUpdateCodec.Build(entityHandle, stats)));
		}
	}
}
