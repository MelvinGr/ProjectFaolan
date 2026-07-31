using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;

namespace Faolan.GameServer.World
{
	// The one place a world IIR goes onto a client's world connection, as a DispatchPassBlob.
	//
	// It exists because there used to be two private `SendWorldIir` helpers - one on GameServerListener.Outbound, one on
	// WorldBroadcaster - with the SAME name and OPPOSITE contracts: the broadcaster's framed the record for you, the
	// listener's sent it verbatim. Nothing said which was which at a call site. Sending a prefix-less record through the
	// verbatim one puts the IIR TYPE where the client reads a length: AddToGroupIIR went out unframed, the client read
	// 0x0DF44B73 as a 234 MB payload, and BOTH members of the team lost every connection the instant the roster landed.
	//
	// So the two shapes keep existing - some codecs frame their own payload (ComboBookIirCodec, NewLevelIirCodec,
	// GroupMembershipCodec, WorldObjectCodec), most emit a bare record - but they are one definition each with a name
	// that says which is which, and a call site now reads unambiguously.
	public static class WorldIir
	{
		// The usual case: `record` is [u32 iirType][record] and the carrier's [u32 len] is written here.
		public static Task Send(INetworkClient client, params byte[] record)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(record)));
		}

		// For a codec that already emits the WHOLE carrier payload - [u32 len][u32 iirType][record] - so its output is
		// directly comparable to a captured packet. Framing it again would nest one payload inside another.
		public static Task SendFramed(INetworkClient client, params byte[] framedPayload)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, framedPayload));
		}
	}
}
