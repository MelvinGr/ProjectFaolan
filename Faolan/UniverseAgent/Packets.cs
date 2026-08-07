using Faolan.Core.Constants;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;

namespace Faolan.UniverseAgent
{
	partial class UniverseAgentListener
	{
		private static readonly ProtoEndpoint AuthChallengeReceiver = Endpoints.EndpointFromBytes(0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0x99, 0xA7, 0x42);
		private static readonly ProtoEndpoint Sender = Endpoints.Endpoint(RpcInterfaces.UniverseAgentType, 0x01);

		// receiver = the request's caller endpoint (packet.Sender), passed through by the handler - the reply echoes it,
		// which is client-specific and can't be generated (see ce487719).
		private Task SendPlayerAgentRealm(INetworkClient client, ProtoEndpoint receiver)
		{
			// The account rides this reply TWICE: a 64-bit universe-scoped form (0x2712 in the high word) and the
			// RAW id. That raw u32 is the same number the FullChar snapshot later carries as its PlayerId stat -
			// verified equal in the 105 captures that contain both (tools/capture/scan_account_id_vs_playerid.py),
			// with NO base added. So the two must stay in step: if this ever sends something other than Account.Id,
			// SnapshotStatSheet's PlayerId has to follow (#478).
			return client.SendAsync(new Rpc(Sender, receiver, UniverseInterfaceMethods.DispatchAckAuthenticate, b => b
				.WriteUInt32(client.Session.Account.AuthStatus) // authstatus 0x01
				.WriteUInt64(0x0000271200000000u + client.Session.Account.Id)
				.WriteString($"{Configuration.ServerAddress}:{Configuration.PlayerAgentPort}")
				.WriteUInt32(client.Session.Account.Id)
				.WriteUInt32(0x00000000)));
		}

		private static Task InitAuth(INetworkClient client)
		{
			return client.SendAsync(new Rpc(Sender, AuthChallengeReceiver, UniverseInterfaceMethods.DispatchChallenge, b => b.WriteString(client.Session.Account.AuthChallenge.ToString("N"))));
		}

		private static Task SendInvalidLogin(INetworkClient client, ProtoEndpoint receiver, InvalidLoginReasons reason)
		{
			return client.SendAsync(new Rpc(Sender, receiver, UniverseInterfaceMethods.DispatchAckAuthenticate,
				b => b.WriteArray(0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00).WriteByte(reason)));
		}

		private static Task SetRegionState(INetworkClient client, ProtoEndpoint receiver)
		{
			return client.SendAsync(new Rpc(Sender, receiver, UniverseInterfaceMethods.DispatchSetRegionState,
				b => b.WriteArray(0x01, 0x01, 0x01, 0x01, 0x3f, 0x80, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x01, 0x01, 0x00)));
		}
	}
}
