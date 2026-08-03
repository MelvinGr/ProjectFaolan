using System;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.PlayerAgent
{
	partial class PlayerAgentListener
	{
		private static readonly ProtoEndpoint Sender = Endpoints.Endpoint(Endpoints.PlayerAgentType, 0x02);
		private static readonly ProtoEndpoint ServiceConnectionSender = Endpoints.Endpoint(Endpoints.PlayerAgentServiceConnectionType, 0x08);

		// The active character's realm - the routing target for the world/chat/guild handoffs (#324). Every service
		// the client is about to connect to lives IN that realm's process, so all three handoffs advertise this realm's
		// address, not the process-global config. Resolved from the selected character (ClientInstance is set at EnterWorld).
		private async Task<Realm> ResolveActiveRealm(INetworkClient client)
		{
			var character = client.Session.Character
			                ?? throw new Exception("No character is selected, so there is no realm to route to.");
			return await Database.GetRealm(character.RealmId) ?? throw new Exception($"Character {character.Id:X8} has no realm to route to.");
		}

		// #573: re-establishes `Session.Character` when the client resumes the login sequence PAST character selection.
		// The handoff block (CsPlayerAgent/ChatServer/GameServer) is driven by `ReceivedCharacterSettings`, which
		// normally arrives after `LoginCharacter` has selected the character - but a client whose connection was
		// replaced mid-flow (the server restarted under it) re-sends the later step WITHOUT the earlier one. Every
		// sender in that block then dereferences a null `Session.Character`, which threw and dropped the connection,
		// so the client reconnected, resumed at the same step and was dropped again - a login loop, observed live.
		//
		// The account row remembers which character it was playing: `ClientInstance` is written and SAVED at
		// LoginCharacter, and is the pre-selection-safe field `SendCharacterList` already relies on. So the session is
		// rebuilt from it rather than the client being bounced. Returns false when there is genuinely nothing to
		// recover (a fresh account that has never entered a character), and the caller then skips the handoff block
		// instead of throwing - the client is simply out of sequence, which is not a server error.
		private async Task<bool> EnsureSessionCharacter(INetworkClient client)
		{
			if (client.Session.Character != null)
				return true;

			var lastPlayed = client.Session.Account?.ClientInstance ?? 0;
			if (lastPlayed == 0)
				return false;

			client.Session.Character = (await Database.GetCharacter(lastPlayed))!;
			if (client.Session.Character == null)
				return false;

			Logger.LogInformation("Recovered session character {CharacterId:X8} from the account's last-played id - the client resumed login past character selection (#573).", lastPlayed);
			return true;
		}

		private async Task SendCharacterList(INetworkClient client, ProtoEndpoint receiver)
		{
			var characters = await Database.GetCharactersByAccount(client.Session.Account.Id);

			using var body = new FaolanStream();
			// The account's current character instance (0 on a first login, the last-played id afterwards). This is the
			// character-SELECTION screen - no character is selected yet, so Session.Character is null here; ClientInstance
			// is the pre-selection-safe account field. (Do NOT "clean up" to Session.Character.Id - it NREs here.)
			body.WriteUInt32(client.Session.Account.ClientInstance);
			body.WriteUInt32(ListFrame.ForCount(characters.Length)); // count-framed character list

			foreach (var character in characters)
			{
				body.WriteUInt32(character.Id);
				body.WriteUInt32(client.Session.Account.ClientInstance); // PlayerInstance (account's current instance; pre-selection, no Session.Character)
				body.WriteUInt32(character.Id);
				body.WriteString(character.Name!);
				body.WriteUInt32(character.RealmId);
				body.WriteUInt32(character.Sex);
				body.WriteString((character.LastConnection ?? character.CreationDate).ToString("MM/dd/yyyy HH:mm:ss"));
				body.WriteUInt32(0x00000000);
				body.WriteUInt32(character.MapId);
				body.WriteUInt32(character.Level);
				body.WriteUInt32(character.Class);
				body.WriteUInt32(0x00000000);
				body.WriteUInt32(0x00000000);
				body.WriteUInt32(0x60355308);
				body.WriteUInt32(0x00000002);
				body.WriteUInt32(character.Race);
				body.WriteString(character.Language!); // enEN
				body.WriteUInt32(0x00000000);
				body.WriteUInt32(0x00000000);
				body.WriteString(character.AuthChallenge.ToString("N"));
				body.WriteUInt32(0x00000000);
				body.WriteUInt32(0x00000001);
			}

			body.WriteUInt32(0x00000bd3);
			body.WriteUInt32(0x00000001);
			body.WriteUInt32(0x00000001);
			body.WriteUInt32(0x00000002);
			body.WriteUInt32(0x00000014);
			body.WriteUInt32(client.Session.Account.ClientInstance); // (pre-selection, no Session.Character)
			body.WriteUInt32(0x000c0001);
			// Subscription state comes from config (Account:{Premium,PremiumCharSlots,FreeCharSlots}); it is a
			// per-deployment policy, not a per-account one - every account is advertised the same way.
			var premium = Configuration.AccountPremium;
			body.WriteUInt32(premium
				? 0x00018081
				: 0x00018181); // subscribed flag byte: 0x80 premium / 0x81 free
			body.WriteUInt32(0x00000015);
			body.WriteUInt32(0x00000020);
			body.WriteUInt32(premium
				? Configuration.AccountPremiumCharSlots
				: Configuration.AccountFreeCharSlots); // unlocked character slots
			body.WriteUInt32(0x00000000);
			body.WriteString("https://www.facebook.com/dialog/oauth?client_id=.....");
			body.WriteUInt32(0x00000000);
			body.WriteUInt32(0x00000001);

			await client.SendAsync(PlayerRpc(Sender, receiver, PlayerInterfaceOpcodes.UpdateClientPlayerData, body.ToArray()));
		}

		private async Task SendChatServer(INetworkClient client, ProtoEndpoint receiver)
		{
			var realm = await ResolveActiveRealm(client);
			await client.SendAsync(new Rpc(ServiceConnectionSender, receiver, PlayerInterfaceOpcodes.OverrideGameTweaks,
				(byte[])[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xFA, 0xE5, 0x98, 0x9D, 0x02],
				b => b.WriteUInt32(realm.IpAddressLong).WriteUInt16(realm.ChatServerPort).WriteUInt32(client.Session.Account.Id).WriteUInt32(EntityClass.Character).WriteUInt32(client.Session.Character.Id)));
		}

		private async Task SendCsPlayerAgent(INetworkClient client, ProtoEndpoint receiver)
		{
			var realm = await ResolveActiveRealm(client);
			await client.SendAsync(new Rpc(ServiceConnectionSender, receiver, PlayerInterfaceOpcodes.OverrideGameTweaks,
				(byte[])[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xE0, 0xD4, 0xB4, 0xD7, 0x05],
				b => b.WriteUInt32(realm.IpAddressLong).WriteUInt16(realm.CsPlayerAgentPort).WriteUInt32(client.Session.Account.Id).WriteUInt32(EntityClass.Character).WriteUInt32(client.Session.Character.Id)));
		}

		private async Task SendGameServer(INetworkClient client, ProtoEndpoint receiver)
		{
			var realm = await ResolveActiveRealm(client);
			await client.SendAsync(new Rpc(ServiceConnectionSender, receiver, PlayerInterfaceOpcodes.OverrideGameTweaks,
				(byte[])[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xF9, 0xD8, 0xD9, 0xC5, 0x0D],
				b => b
					.WriteString($"{realm.IpAddress}:{realm.Port}")
					.WriteUInt32(client.Session.Account.Id)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(client.Session.Character.Id)
					.WriteArray(0x62, 0x00, 0x00, 0xC7, 0x9C, 0x00, 0x00, 0x0F, 0xAA, 0x00, 0x00, 0x00, 0x00)
					.WriteUInt32(client.Session.Character.Id)
					.WriteArray(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x9C, 0x50, 0x01, 0xC9, 0x43, 0xC1, 0x00)));
		}

		private async Task SendRealmList(INetworkClient client, ProtoEndpoint receiver)
		{
			var realms = await Database.GetRealms();

			await client.SendAsync(PlayerRpc(Sender, receiver, PlayerInterfaceOpcodes.SetDimensionList, b =>
			{
				b.WriteUInt32(realms.Length);
				foreach (var realm in realms)
				{
					b.WriteUInt32(realm.Id);
					b.WriteUInt32(realm.OnlineStatus);
					b.WriteString(realm.Name);
					b.WriteUInt32(0x00000001); //u2
					b.WriteUInt32(0x00000000); //u3
					b.WriteUInt32(0x00000000); //u4
					b.WriteUInt32(realm.LoadStatus == RealmLoadStatus.Full);
					b.WriteUInt32(realm.LoadStatus);
					b.WriteUInt16(0x0000);
					b.WriteByte(0x00);
					b.WriteUInt16(realm.Type);
					b.WriteUInt32(realm.Country);
					b.WriteByte(0x00);
				}
			}));
		}

		// The small character list (id + auth challenge per named character). Sent in response to
		// RequestSmallCharList and again after a delete so the client refreshes. Entry layout is exactly
		// Id + AuthChallenge (length-prefixed string), verified against retail (Faolanx_mage-necromancer_login: all bytes
		// consumed, no per-entry trailer - an extra field shifts every following entry and the client loops
		// once there is more than one character).
		private async Task SendSmallCharList(INetworkClient client, ProtoEndpoint receiver)
		{
			var characters = await Database.GetCharactersByAccount(client.Session.Account.Id);

			await client.SendAsync(new Rpc(Sender, receiver, PlayerInterfaceOpcodes.SendCharacterSettingsCheckSumMap, PlayerAgentMethodIds.HeaderId((ushort)PlayerInterfaceOpcodes.SendCharacterSettingsCheckSumMap), b =>
			{
				b.WriteUInt32(ListFrame.ForCount(characters.Length)); // count-framed character list
				foreach (var character in characters)
				{
					b.WriteUInt32(character.Id);
					b.WriteString(character.AuthChallenge.ToString("N"));
				}
			}));
		}

		// The reply receiver echoes the request's caller endpoint (packet.Sender), passed through by the handler - it is
		// client-specific and can't be generated (see the UniverseAgent migration / ce487719).
		//
		// PlayerInterface s2c uniquely carries a 32-bit method id in the header after the opcode; PlayerRpc appends it
		// (PlayerAgentMethodIds) as the HeaderData so senders don't hand-roll it, mirroring the
		// WriteHeader(byte[], byte[], PlayerInterfaceOpcodes) overload it replaces. Sends that carry a NON-standard
		// header tail (SmallCharList, the OverrideGameTweaks handoffs) pass their literal HeaderData to Rpc directly.
		private static Rpc PlayerRpc(ProtoEndpoint sender, ProtoEndpoint receiver, PlayerInterfaceOpcodes opcode, ReadOnlyMemory<byte> body)
		{
			return new Rpc(sender, receiver, opcode, PlayerAgentMethodIds.HeaderId((ushort)opcode), body);
		}

		private static Rpc PlayerRpc(ProtoEndpoint sender, ProtoEndpoint receiver, PlayerInterfaceOpcodes opcode, Action<FaolanStream> body)
		{
			return new Rpc(sender, receiver, opcode, PlayerAgentMethodIds.HeaderId((ushort)opcode), body);
		}

		private static Task SendCharacterSettings(INetworkClient client, ProtoEndpoint receiver)
		{
			return client.SendAsync(PlayerRpc(Sender, receiver, PlayerInterfaceOpcodes.SendCharacterSettings, b => b.WriteUInt32(EntityClass.Character).WriteUInt32(client.Session.Character.Id).WriteUInt32(0)));
		}
	}
}
