using System.Threading.Tasks;
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
	// #530 rule 2 settled which shape is right: the u32 length belongs to the SENDER, so a codec emits a bare record
	// and there is exactly ONE way to put it on the wire. The `SendFramed` twin - for codecs that prepended their own
	// length - lost its last caller when Combat converted in wave 4 and is gone.
	public static class WorldIir
	{
		// The usual case: `record` is [u32 iirType][record] and the carrier's [u32 len] is written here.
		public static Task Send(INetworkClient client, params byte[] record)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(record)));
		}
	}
}
