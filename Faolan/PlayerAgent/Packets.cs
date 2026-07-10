using System;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;

namespace Faolan.PlayerAgent
{
	partial class PlayerAgentListener
	{
		private static readonly byte[] Sender = [0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02];
		private static readonly byte[] ServiceConnectionSender = [0x0D, 0x16, 0x91, 0x35, 0x1D, 0x10, 0x08];

		private Task SendAgentServer(INetworkClient client)
		{
			return new PacketStream()
				.WriteHeader(ServiceConnectionSender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.Ox1A07,
					[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xFA, 0xE5, 0x98, 0x9D, 0x02])
				.WriteUInt32(Configuration.ServerIpAddressLong)
				.WriteUInt16(Configuration.AgentServerPort)
				.WriteUInt32(client.Session.Account.Id)
				.WriteUInt32(0x0000c350)
				.WriteUInt32(client.Session.Account.ClientInstance)
				.SendAsync(client);
		}

		private async Task SendCharacterList(INetworkClient client)
		{
			var characters = await Database.GetCharactersByAccount(client.Session.Account.Id);

			var aBuffer = new PacketStream();
			aBuffer.WriteHeader(Sender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.SendCharacterList);
			aBuffer.WriteUInt32(client.Session.Account.ClientInstance);
			aBuffer.WriteUInt32((characters.Length + 1) * 1009); // number of characters

			foreach (var character in characters)
			{
				aBuffer.WriteUInt32(character.Id);
				aBuffer.WriteUInt32(client.Session.Account.ClientInstance); // PlayerInstance
				aBuffer.WriteUInt32(character.Id);
				aBuffer.WriteString(character.Name);
				aBuffer.WriteUInt32(character.RealmId);
				aBuffer.WriteUInt32(character.Sex);
				aBuffer.WriteString((character.LastConnection ?? character.CreationDate).ToString("MM/dd/yyyy HH:mm:ss"));
				aBuffer.WriteUInt32(0x00000000);
				aBuffer.WriteUInt32(character.MapId);
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
				aBuffer.WriteString(character.AuthChallenge); // 8f58a8a3cd66eb5c5e20c15d4ec6f978 for conanhimself
				aBuffer.WriteUInt32(0x00000000);
				aBuffer.WriteUInt32(0x00000001);
			}

			aBuffer.WriteUInt32(0x00000bd3);
			aBuffer.WriteUInt32(0x00000001);
			aBuffer.WriteUInt32(0x00000001);
			aBuffer.WriteUInt32(0x00000002);
			aBuffer.WriteUInt32(0x00000014);
			aBuffer.WriteUInt32(client.Session.Account.ClientInstance); // 0x3bc035ec
			aBuffer.WriteUInt32(0x000c0001);
			aBuffer.WriteUInt32(Global.Premium
				? 0x00018081
				: 0x00018181); // subscribed flag byte: 0x80 premium / 0x81 free
			aBuffer.WriteUInt32(0x00000015);
			aBuffer.WriteUInt32(0x00000020);
			aBuffer.WriteUInt32(Global.Premium
				? Global.PremiumCharSlots
				: Global.FreeCharSlots); // unlocked character slots
			aBuffer.WriteUInt32(0x00000000);
			aBuffer.WriteString("https://www.facebook.com/dialog/oauth?client_id=.....");
			aBuffer.WriteUInt32(0x00000000);
			aBuffer.WriteUInt32(0x00000001);

			await aBuffer.SendAsync(client);
		}

		private Task SendCsPlayerAgent(INetworkClient client)
		{
			return new PacketStream() // send CSPlayerAgent
				.WriteHeader(ServiceConnectionSender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.Ox1A07,
					[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xE0, 0xD4, 0xB4, 0xD7, 0x05])
				.WriteUInt32(Configuration.ServerIpAddressLong)
				.WriteUInt16(Configuration.CsPlayerAgentPort)
				.WriteUInt32(client.Session.Account.Id)
				.WriteUInt32(0x0000c350)
				.WriteUInt32(client.Session.Account.ClientInstance)
				.SendAsync(client);
		}

		private async Task SendGameServer(INetworkClient client)
		{
			var account = client.Session.Account;

			// Resolve the active character via the repository (the session no longer lazy-loads Account.Characters):
			// GetCharacterForAccount returns the direct id match, else the most-recently-created character.
			var activeCharacter = await Database.GetCharacterForAccount(account.Id, account.ClientInstance) ??
								  throw new Exception($"Unable to resolve active character for account {account.Id:X8} and client instance {account.ClientInstance:X8}");

			var realm = await Database.GetRealm(activeCharacter.RealmId);
			if (realm == null)
				throw new Exception($"Character {activeCharacter.Id:X8} has no realm to advertise the GameServer for.");

			await new PacketStream()
				.WriteHeader(ServiceConnectionSender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.Ox1A07,
					[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xF9, 0xD8, 0xD9, 0xC5, 0x0D])
				.WriteString($"{realm.IpAddress}:{realm.Port}")
				.WriteUInt32(account.Id)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(account.ClientInstance)
				.WriteArray((byte[])[0x62, 0x00, 0x00, 0xC7, 0x9C, 0x00, 0x00, 0x0F, 0xAA, 0x00, 0x00, 0x00, 0x00])
				.WriteUInt32(account.ClientInstance)
				.WriteArray((byte[])[0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x9C, 0x50, 0x01, 0xC9, 0x43, 0xC1, 0x00])
				.SendAsync(client);
		}

		private async Task SendRealmList(INetworkClient client)
		{
			var realms = await Database.GetRealms();

			var aBuffer = new PacketStream();
			aBuffer.WriteHeader(Sender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.SendRealmList);

			aBuffer.WriteUInt32(realms.Length);
			foreach (var realm in realms)
			{
				aBuffer.WriteUInt32(realm.Id);
				aBuffer.WriteUInt32(realm.OnlineStatus);
				aBuffer.WriteString(realm.Name);
				aBuffer.WriteUInt32(0x00000001); //u2
				aBuffer.WriteUInt32(0x00000000); //u3
				aBuffer.WriteUInt32(0x00000000); //u4
				aBuffer.WriteUInt32(realm.LoadStatus == RealmLoadStatus.Full);
				aBuffer.WriteUInt32(realm.LoadStatus);
				aBuffer.WriteUInt16(0x0000);
				aBuffer.WriteByte(0x00);
				aBuffer.WriteUInt16(realm.Type);
				aBuffer.WriteUInt32(realm.Country);
				aBuffer.WriteByte(0x00);
			}

			await aBuffer.SendAsync(client);
		}

		// The small character list (id + auth challenge per named character). Sent in response to
		// RequestSmallCharList and again after a delete so the client refreshes. Entry layout is exactly
		// Id + AuthChallenge (length-prefixed string), verified against retail (Faolanx_mage-necromancer_login: all bytes
		// consumed, no per-entry trailer - an extra field shifts every following entry and the client loops
		// once there is more than one character).
		private async Task SendSmallCharList(INetworkClient client)
		{
			var characters = await Database.GetCharactersByAccount(client.Session.Account.Id);

			var buffer = new PacketStream();
			buffer.WriteHeader(Sender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.SmallCharList, [0x94, 0xa7, 0x60]);
			buffer.WriteUInt32((characters.Length + 1) * 1009);

			foreach (var character in characters)
			{
				buffer.WriteUInt32(character.Id);
				buffer.WriteString(character.AuthChallenge);
			}

			await buffer.SendAsync(client);
		}

		private static Task SendOx20B9(INetworkClient client)
		{
			return new PacketStream()
				.WriteHeader(Sender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.Ox20B9)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(client.Session.Account.ClientInstance)
				.WriteUInt32(0)
				.SendAsync(client);
		}
	}
}
