using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private async Task Send0X201C(INetworkClient client)
		{
			var sender = LoginWorld201CSender;
			var receiver = LoginWorld201CReceiver(client);
			var ticketBase = 0x00160000u + ((uint)Environment.TickCount & 0xFFFF);

			// Retail (Strie login) sends five channel registrations before the spawn burst: four with the
			// 0x65 prefix and state ids {5, 6, 0x10, 0x12}, then a final 0x68 prefix with state 1. The old
			// three-entry set (with a duplicated state 6 and missing 5/0x12/the 0x68 entry) left the client
			// stuck before it ever started polling spawns (0x2020), so nothing rendered.
			await Send0X201CEntry(client, sender, receiver, 0x00000065, 0x00000005, ticketBase);
			await Send0X201CEntry(client, sender, receiver, 0x00000065, 0x00000006, ticketBase + 1);
			await Send0X201CEntry(client, sender, receiver, 0x00000065, 0x00000010, ticketBase + 2);
			await Send0X201CEntry(client, sender, receiver, 0x00000065, 0x00000012, ticketBase + 3);
			await Send0X201CEntry(client, sender, receiver, 0x00000068, 0x00000001, ticketBase + 4);
		}

		private static Task Send0X201CEntry(INetworkClient client, byte[] sender, byte[] receiver, uint prefix, uint state, uint ticket)
		{
			return new PacketStream()
				.WriteHeader(sender, receiver, GameCharInterfaceDispatchOpcodes.DispatchSetItemStat)
				.WriteUInt32(prefix)
				.WriteUInt32(client.Session.Account.ClientInstance)
				.WriteUInt32(state)
				.WriteUInt32(0x00000017)
				.WriteUInt32(ticket)
				.SendAsync(client);
		}
	}
}
