using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;

namespace Faolan.UniverseAgent
{
	partial class UniverseAgentListener
	{
		private static readonly byte[] Sender = [0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01];

		private Task SendPlayerAgentRealm(INetworkClient client)
		{
			return new PacketStream()
				.WriteHeader(Sender, client.Session.ClientConversation, UniverseInterfaceOpcodes.DispatchAckAuthenticate)
				.WriteUInt32(client.Session.Account.AuthStatus) // authstatus 0x01
				.WriteUInt64(client.Session.Account.LongId)
				.WriteString($"{Configuration.ServerIpAddress}:{Configuration.PlayerAgentPort}")
				.WriteUInt32(client.Session.Account.Id)
				.WriteUInt32(0x00000000)
				.SendAsync(client);
		}

		private static Task InitAuth(INetworkClient client)
		{
			return new PacketStream()
				.WriteHeader(Sender, [0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0x99, 0xA7, 0x42], UniverseInterfaceOpcodes.DispatchChallenge)
				.WriteString(client.Session.Account.AuthChallenge)
				.SendAsync(client);
		}

		private static Task SendInvalidLogin(INetworkClient client, InvalidLoginReasons reason)
		{
			return new PacketStream()
				.WriteHeader(Sender, client.Session.ClientConversation, UniverseInterfaceOpcodes.DispatchAckAuthenticate)
				.WriteArray(0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
				.WriteByte(reason)
				.SendAsync(client);
		}

		private static Task SetRegionState(INetworkClient client)
		{
			return new PacketStream()
				.WriteHeader(Sender, client.Session.ClientConversation, UniverseInterfaceOpcodes.DispatchSetRegionState)
				.WriteArray(0x01, 0x01, 0x01, 0x01, 0x3f, 0x80, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x01, 0x01, 0x00)
				.SendAsync(client);
		}
	}
}
