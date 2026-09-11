using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Frames the 0x96B8DC59 StatUpdateIIR for a single addressed recipient - the host-side counterpart of
	///     <c>StatUpdateCodec</c>, which owns the pure IIR body - by adding the RPC header on the world conversation
	///     plus the length and hash.
	/// </summary>
	/// <remarks>
	///     Used by everything that pushes a stat or vitals change to one client: combat, the regen tick, the
	///     world-entry replay and the GM commands. A broadcaster fanning a stat out to nearby viewers calls the codec
	///     directly and frames it per recipient through <see cref="WorldIir.Send" />. It lives here rather than in the
	///     codec because it depends on the host framing types.
	/// </remarks>
	public static class VitalsPacket
	{
		/// <summary>
		///     The framed 0x96B8DC59 stat-update packet for an entity, as an <see cref="Rpc" /> the transport
		///     serializes and logs once, with no re-parse.
		/// </summary>
		public static Rpc BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(StatUpdateCodec.Build(entityHandle, stats)));
		}
	}
}
