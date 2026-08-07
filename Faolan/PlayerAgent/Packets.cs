using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.PlayerAgent;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.PlayerAgent
{
	partial class PlayerAgentListener
	{
		private static readonly ProtoEndpoint Sender = Endpoints.Endpoint(RpcInterfaces.PlayerAgentType, 0x02);
		private static readonly ProtoEndpoint ServiceConnectionSender = Endpoints.Endpoint(RpcInterfaces.PlayerAgentServiceConnectionType, 0x08);

		// `full_routing_context` (RpcHeader field 3) for the four login handoffs: they are addressed to the
		// ServiceConnection, so they restate the PlayerInterface object the method actually fires on. Constant across
		// all 472 captured frames - instance 1, never the character or account id (#610).
		private static readonly ProtoEndpoint PlayerInterfaceRoutingContext = Endpoints.Endpoint(RpcInterfaces.PlayerInterfaceType, 0x01);

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

			await client.SendAsync(new Rpc(Sender, receiver, PlayerInterfaceMethods.UpdateClientPlayerData, body.ToArray()));
		}

		private async Task SendChatServer(INetworkClient client, ProtoEndpoint receiver)
		{
			var realm = await ResolveActiveRealm(client);
			await client.SendAsync(new Rpc(ServiceConnectionSender, receiver, PlayerInterfaceMethods.ChatServerConnectReady,
				b => b.WriteUInt32(realm.IpAddressLong).WriteUInt16(realm.ChatServerPort).WriteUInt32(client.Session.Account.Id).WriteUInt32(EntityClass.Character).WriteUInt32(client.Session.Character.Id))
			{
				RoutingContext = PlayerInterfaceRoutingContext
			});
		}

		private async Task SendCsPlayerAgent(INetworkClient client, ProtoEndpoint receiver)
		{
			var realm = await ResolveActiveRealm(client);
			await client.SendAsync(new Rpc(ServiceConnectionSender, receiver, PlayerInterfaceMethods.CSServerConnectReady,
				b => b.WriteUInt32(realm.IpAddressLong).WriteUInt16(realm.CsPlayerAgentPort).WriteUInt32(client.Session.Account.Id).WriteUInt32(EntityClass.Character).WriteUInt32(client.Session.Character.Id))
			{
				RoutingContext = PlayerInterfaceRoutingContext
			});
		}

		private async Task SendGameServer(INetworkClient client, ProtoEndpoint receiver)
		{
			var realm = await ResolveActiveRealm(client);
			await client.SendAsync(new Rpc(ServiceConnectionSender, receiver, PlayerInterfaceMethods.LoginConnectReady,
				b => b
					.WriteString($"{realm.IpAddress}:{realm.Port}")
					.WriteUInt32(client.Session.Account.Id)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(client.Session.Character.Id)
					.WriteArray(0x62, 0x00, 0x00, 0xC7, 0x9C, 0x00, 0x00, 0x0F, 0xAA, 0x00, 0x00, 0x00, 0x00)
					.WriteUInt32(client.Session.Character.Id)
					.WriteArray(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x9C, 0x50, 0x01, 0xC9, 0x43, 0xC1, 0x00))
			{
				RoutingContext = PlayerInterfaceRoutingContext
			});
		}

		private async Task SendRealmList(INetworkClient client, ProtoEndpoint receiver)
		{
			var realms = await Database.GetRealms();

			await client.SendAsync(new Rpc(Sender, receiver, PlayerInterfaceMethods.SetDimensionList, b =>
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

		// The reply to GetStartupData: every character's stored select-screen preview, then the settings checksum map.
		// That is retail's order and content (Faolanx_mage-necromancer_login frames 8-9) - the previews come FIRST, one
		// packet each and only for the characters that have one, then one map covering all of them.
		//
		// ⚠️ The map's per-character string is the md5 of that character's stored SETTINGS archive (#611), which is how
		// the client decides whether its cached copy is current; it is EMPTY for a character that has never uploaded
		// any. It used to be the account's auth challenge - a 32-hex-char value of the right shape and the wrong
		// meaning, so it advertised a checksum for settings the server did not hold and no cached copy could ever match.
		private async Task SendSmallCharList(INetworkClient client, ProtoEndpoint receiver)
		{
			var characters = await Database.GetCharactersByAccount(client.Session.Account.Id);
			var stored = (await Database.GetCharacterClientSettingsByAccount(client.Session.Account.Id)).ToDictionary(s => s.CharacterId);

			foreach (var character in characters)
			{
				if (stored.TryGetValue(character.Id, out var row) && row.Preview is { Length: > 0 } preview)
					await client.SendAsync(new Rpc(Sender, receiver, PlayerInterfaceMethods.SendCharacterPreview, SendCharacterPreviewCodec.Build(new CharacterPreview(character.Id, ClientSettingsBufferCodec.Parse(preview)))));
			}

			await client.SendAsync(new Rpc(Sender, receiver, PlayerInterfaceMethods.SendCharacterSettingsCheckSumMap,
				SendCharacterSettingsCheckSumMapCodec.Build(new CharacterSettingsCheckSumMap([
					..characters.Select(c => new CharacterSettingsCheckSum(c.Id, CheckSumOf(stored.GetValueOrDefault(c.Id)?.Settings)))
				]))));
		}

		// The client sends its own md5 with each settings upload, and it is the value we hand back in the checksum map.
		// We store the payload rather than the string and recompute it, which only works because the two agree - so say
		// so out loud if they ever do not, instead of serving a checksum no cached client copy will match.
		private void WarnOnCheckSumMismatch(string method, ClientSettingsBuffer buffer, string declared)
		{
			if (buffer.CheckSum != declared)
				Logger.LogWarning("{Method}: the client declared md5 {Declared} but the payload hashes to {Computed} - the checksum map will advertise the computed one.", method, declared, buffer.CheckSum);
		}

		// The md5 the client compares its cache against, over the COMPRESSED archive - see ClientSettingsBuffer.CheckSum.
		private static string CheckSumOf(byte[]? settings)
		{
			return settings is { Length: > 0 } ? ClientSettingsBufferCodec.Parse(settings).CheckSum : "";
		}

		// The reply receiver echoes the request's caller endpoint (packet.Sender), passed through by the handler - it is
		// client-specific and can't be generated (see the UniverseAgent migration / ce487719).
		//
		// PlayerInterface s2c uniquely carries a 32-bit method id in the header after the opcode. Nothing here appends
		// it: `Rpc` derives it from (receiver class, opcode) like every other interface's header tail (#609), so the
		// PlayerRpc helper that used to thread it through is gone. Only OverrideGameTweaks still says its tail out
		// loud, because its tail is not a method id.
		//
		// The archive itself is whatever the client last uploaded for this character (#611), handed back verbatim so its
		// chat tabs, dock bars and per-character preferences come back with it. A character that has never uploaded one
		// gets the EMPTY buffer, which is the 12-byte body retail sends in all 118 captured sightings.
		private async Task SendCharacterSettings(INetworkClient client, ProtoEndpoint receiver)
		{
			var characterId = client.Session.Character.Id;
			var stored = (await Database.GetCharacterClientSettings(characterId))?.Settings;
			var buffer = stored is { Length: > 0 } ? ClientSettingsBufferCodec.Parse(stored) : ClientSettingsBuffer.Empty;

			await client.SendAsync(new Rpc(Sender, receiver, PlayerInterfaceMethods.SendCharacterSettings, SendCharacterSettingsCodec.Build(new CharacterSettings(characterId, buffer))));
		}
	}
}
