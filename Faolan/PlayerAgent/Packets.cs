using System;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Faolan.PlayerAgent
{
	partial class PlayerAgentListener
	{
		private static readonly byte[] Sender = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02 };
		private static readonly byte[] Receiver = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
		private static readonly byte[] Sender3 = { 0x0D, 0x16, 0x91, 0x35, 0x1D, 0x10, 0x46 };
		private static readonly byte[] Receiver3 = { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0xEC, 0xEB, 0x80, 0xDE, 0x03 };
		private static readonly byte[] Sender0 = { 0x0D, 0x16, 0x91, 0x35, 0x1D, 0x10, 0x4C };
		private static readonly byte[] Sender2 = { 0x0D, 0x16, 0x91, 0x35, 0x1D, 0x10, 0x64 };
		private static readonly byte[] Sender4 = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };

		private async Task SendCharacterList(INetworkClient client)
		{
			var characters = await Database.GetCharactersByAccount(client.Account.Id);

			var aBuffer = new PacketStream();
			aBuffer.WriteHeader(Sender, Receiver, new byte[] { 0x8b, 0xd3, 0xa0, 0x0c },
				PlayerAgentResponseOpcodes.SendCharacterList);
			aBuffer.WriteUInt32(client.Account.ClientInstance);
			aBuffer.WriteUInt32((characters.Length + 1) * 1009); // number of characters

			foreach (var character in characters)
			{
				aBuffer.WriteUInt32(character.Id);
				aBuffer.WriteUInt32(client.Account.ClientInstance); // PlayerInstance
				aBuffer.WriteUInt32(character.Id);
				aBuffer.WriteString(character.Name);
				aBuffer.WriteUInt32(character.RealmId ?? throw new Exception("character.RealmId == null"));
				aBuffer.WriteUInt32(character.Sex);
				aBuffer.WriteString((character.LastConnection ?? character.CreationDate).ToString("MM/dd/yyyy HH:mm:ss"));
				aBuffer.WriteUInt32(0x00000000);
				aBuffer.WriteUInt32(character.MapId ?? throw new Exception("client.MapId == null"));
				aBuffer.WriteUInt32(character.Level);
				aBuffer.WriteUInt32(character.Class);
				aBuffer.WriteUInt32(0x00000000);
				aBuffer.WriteUInt32(0x00000000);
				aBuffer.WriteUInt32(0x60355308);
				aBuffer.WriteUInt32(0x00000002);
				aBuffer.WriteUInt32(character.Race);
				aBuffer.WriteString(character.Language); // enEN
				aBuffer.WriteUInt32(0x00000000);
				aBuffer.WriteUInt32(0x00000000);
				aBuffer.WriteString("8f58a8a3cd66eb5c5e20c15d4ec6f978");
				aBuffer.WriteUInt32(0x00000000);
				aBuffer.WriteUInt32(0x00000001);
			}

			aBuffer.WriteUInt32(0x00000bd3);
			aBuffer.WriteUInt32(0x00000001);
			aBuffer.WriteUInt32(0x00000001);
			aBuffer.WriteUInt32(0x00000002);
			aBuffer.WriteUInt32(0x00000014);
			aBuffer.WriteUInt32(client.Account.ClientInstance); // 0x3bc035ec
			aBuffer.WriteUInt32(0x000c0001);
			aBuffer.WriteUInt32(0x00018181);
			aBuffer.WriteUInt32(0x00000015);
			aBuffer.WriteUInt32(0x00000020);
			aBuffer.WriteUInt32(0x00000004);
			aBuffer.WriteUInt32(0x00000000);
			aBuffer.WriteString("https://www.facebook.com/dialog/oauth?client_id=.....");
			aBuffer.WriteUInt32(0x00000000);
			aBuffer.WriteUInt32(0x00000001);
			aBuffer.Send(client);
		}

		private async Task SendRealmList(INetworkClient client)
		{
			var realms = await Database.Context.Realms.ToArrayAsync();

			var aBuffer = new PacketStream();
			aBuffer.WriteHeader(Sender, Receiver, new byte[] { 0xe2, 0xe6, 0xc4, 0x0f },
				PlayerAgentResponseOpcodes.SendRealmList);

			aBuffer.WriteUInt32(realms.Length);
			foreach (var realm in realms)
			{
				aBuffer.WriteUInt32(realm.Id);
				aBuffer.WriteUInt32(realm.OnlineStatus);
				aBuffer.WriteString(realm.Name);
				aBuffer.WriteUInt32(0x00000001); //u2
				aBuffer.WriteUInt32(0x00000000); //u3
				aBuffer.WriteUInt32(0x00000000); //u4
				aBuffer.WriteUInt32(realm.FullStatus);
				aBuffer.WriteUInt32(realm.LoadStatus);
				aBuffer.WriteUInt16(0x0000);
				aBuffer.WriteByte(0x00);
				aBuffer.WriteUInt16(realm.Type);
				aBuffer.WriteUInt32(realm.Country);
				aBuffer.WriteByte(0x00);
			}

			aBuffer.Send(client);
		}

		private void SendCsPlayerAgent(INetworkClient client)
        {
            var sender638167354496472670 = new byte[] { 0x0D, 0x16, 0x91, 0x35, 0x1D, 0x10, 0x54 };
            var receiver638167354496472670 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0xEC, 0xEB, 0x80, 0xDE, 0x03 };

            var headerData0 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xE0, 0xD4, 0xB4, 0xD7, 0x05 };

			new PacketStream() // send CSPlayerAgent
				.WriteHeader(sender638167354496472670, receiver638167354496472670, headerData0, PlayerAgentResponseOpcodes.SendCsPlayerAgent)
				.WriteUInt32(Configuration.ServerIpAddressLong())
				.WriteUInt16(Configuration.CsPlayerAgentPort())
				.WriteUInt32(client.Account.Id)
				.WriteUInt32(0x0000c350)
				.WriteUInt32(client.Account.ClientInstance)
				.Send(client);

            /*var headerData638167354496472670 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xE0, 0xD4, 0xB4, 0xD7, 0x05 };
            var packetData638167354496472670 = new byte[]
            {
                0x25, 0x12, 0xC0, 0x2B, 
                0x1B, 0x65, // 7013
                0x02, 0x8C, 0x26, 0xAF,
                0x00, 0x00, 0xC3, 0x50,
                0x08, 0x02, 0xE5, 0xD4
            };
            new PacketStream()
                .WriteHeader(sender638167354496472670, receiver638167354496472670, headerData638167354496472670, 0x00001A07, true)
                .WriteArray(packetData638167354496472670)
                .Send(client);*/
        }

        private void SendAgentServer(INetworkClient client)
        {
            var sender638167354496472670 = new byte[] { 0x0D, 0x16, 0x91, 0x35, 0x1D, 0x10, 0x54 };
            var receiver638167354496472670 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0xEC, 0xEB, 0x80, 0xDE, 0x03 };

            var headerData0 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xFA, 0xE5, 0x98, 0x9D, 0x02 };

			new PacketStream() 
				.WriteHeader(sender638167354496472670, receiver638167354496472670, headerData0, PlayerAgentResponseOpcodes.SendAgentServer)
				.WriteUInt32(Configuration.ServerIpAddressLong())
				.WriteUInt16(Configuration.AgentServerPort())
				.WriteUInt32(client.Account.Id)
				.WriteUInt32(0x0000c350)
				.WriteUInt32(client.Account.ClientInstance)
				.Send(client);

            /*var headerData638167354496472670 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xFA, 0xE5, 0x98, 0x9D, 0x02 };
            var packetData638167354496472670 = new byte[]
            {
                0x25, 0x12, 0xC0, 0x2B, 
                0x1B, 0x5A, // 7002
                0x02, 0x8C, 0x26, 0xAF,
                0x00, 0x00, 0xC3, 0x50,
                0x08, 0x02, 0xE5, 0xD4
            };
            new PacketStream()
                .WriteHeader(sender638167354496472670, receiver638167354496472670, headerData638167354496472670, 0x00001A07, true)
                .WriteArray(packetData638167354496472670)
                .Send(client);*/
        }

        private void SendGameServer(INetworkClient client)
		{
			var headerData2 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xF9, 0xD8, 0xD9, 0xC5, 0x0D };
			var sender2 = new byte[] { 0x0D, 0x16, 0x91, 0x35, 0x1D, 0x10, 0x64 };
			var receiver2 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0xEC, 0xEB, 0x80, 0xDE, 0x03 };

			new PacketStream()
				.WriteHeader(sender2, receiver2, headerData2, 0x00001A07)
				.WriteString($"{client.Character.Realm.IpAddress}:{client.Character.Realm.Port}")
				.WriteUInt32(client.Account.Id) // 0x5679E2CF
				.WriteUInt32(0x0000C350)
				.WriteUInt32(client.Account.ClientInstance)
				.WriteArray(0x62,
					0x00, 0x00, 0xC7, 0x9C,
					0x00, 0x00, 0x07, 0xD0,
					0x00, 0x00, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x01,
					0x00, 0x00, 0x9C, 0x50,
					0x00, 0x34, 0x3C, 0xC4,
					0x00)
				.Send(client);
		}
	}
}
