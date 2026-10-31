using System;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
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
		// and it is client-specific, so it cannot be generated.
		private Task SendPlayerAgentRealm(INetworkClient client, ProtoEndpoint receiver)
		{
			// DispatchAckAuthenticate(nAuthStatus, cPlayerID, cTerritoryManagerAddr, uCookie, eReason), the client's own
			// signature (docs/re/aoc-client-rpc-methods.md). cPlayerID is the account in its universe-scoped form, 0x2712
			// in the high word; its low word is the account id the FullChar snapshot carries as PlayerId (one value per
			// account across the corpus). uCookie is this login's cookie, different on every one of the 139 captured
			// logins, which the client presents to every later tier as its proof of this login (Account.LoginCookie).
			var account = client.Session.Account;
			var cookie = account.LoginCookie ?? throw new InvalidOperationException($"Account {account.Id:X8} has no login cookie to hand out; the login did not issue one.");

			return client.SendAsync(new Rpc(Sender, receiver, UniverseInterfaceMethods.DispatchAckAuthenticate, b => b
				.WriteUInt32(account.AuthStatus) // authstatus 0x01
				.WriteUInt64(0x0000271200000000u + account.Id)
				.WriteString($"{Configuration.ServerPublicAddress}:{Configuration.ServerPlayerAgentPort}")
				.WriteUInt32(cookie)
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
