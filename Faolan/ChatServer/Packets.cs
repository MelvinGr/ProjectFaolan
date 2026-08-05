using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Packets.ChatServer;
using Faolan.Core.Network.Server.ChatServer;
using Microsoft.Extensions.Logging;

namespace Faolan.ChatServer
{
	partial class ChatServerListener
	{
		public async Task CharacterSay(uint characterId, ChatMarkup message, IReadOnlyCollection<uint>? recipientCharacterIds = null)
		{
			// "say" is proximity chat: the GameServer (which holds the live positions) passes the in-range character
			// ids in `recipientCharacterIds`; we deliver the CharacterChat (0x0022) only to those clients. A null list
			// means server-wide (kept for any non-proximity caller). The sender is included by the GameServer, so they
			// always see their own message.
			foreach (var client in NetworkClients)
			{
				var recipientId = client.Session.Character?.Id ?? 0;
				if (recipientCharacterIds != null && !recipientCharacterIds.Contains(recipientId))
					continue;

				await client.SendAsync(new ChatServerMessage(CharacterChatCodec.Opcode, CharacterChatCodec.Build(characterId, message.Value)));
			}
		}

		// An NPC bark: the same CharacterChat (0x0022) delivery as a say, with CharacterChatCodec composing the bark's
		// remoteformat (#539) instead of the world layer pre-rendering it.
		public async Task NpcBark(uint npcHandle, NpcChatterBark bark, IReadOnlyCollection<uint>? recipientCharacterIds = null)
		{
			foreach (var client in NetworkClients)
			{
				var recipientId = client.Session.Character?.Id ?? 0;
				if (recipientCharacterIds != null && !recipientCharacterIds.Contains(recipientId))
					continue;

				await client.SendAsync(new ChatServerMessage(CharacterChatCodec.Opcode, CharacterChatCodec.Build(npcHandle, bark)));
			}
		}

		public async Task<int> SendClaudeChannelMessage(ChatMarkup message)
		{
			// Claude's post on the Claude channel: the same 0x0014 sender-info + 0x0041 relay pair as a player post
			// (see BroadcastChannelChat), but under the synthetic Claude identity. Every client is subscribed to the
			// channel (Hello sends the subscription), so a plain broadcast reaches everyone.
			var delivered = 0;
			foreach (var client in NetworkClients)
			{
				await client.SendAsync(new ChatServerMessage(ChatSenderInfoCodec.Opcode, ChatSenderInfoCodec.Build(ClaudeChannel.SenderId, ClaudeChannel.SenderName)));

				await client.SendAsync(new ChatServerMessage(ChannelChatCodec.Opcode,
					ChannelChatCodec.Build(ChatChannels.Assistant.Type, ClaudeChannel.ChannelId, ClaudeChannel.SenderId, message.Value)));

				delivered++;
			}

			_claudeChannel.Append(ClaudeChannel.SenderId, ClaudeChannel.SenderName, message.Value, true);

			return delivered;
		}

		// Whispers a single character as the synthetic Claude identity: the 0x0014 sender info + the 0x001E tell
		// (the exact delivery shape of a player tell - see the whisper capture / WhisperTests). Logged on the
		// Claude channel log so the bridge conversation history stays complete.
		public async Task<bool> SendClaudeWhisper(uint targetCharacterId, ChatMarkup message)
		{
			var target = ClientForCharacter(targetCharacterId);
			if (target == null)
				return false;

			await target.SendAsync(new ChatServerMessage(ChatSenderInfoCodec.Opcode, ChatSenderInfoCodec.Build(ClaudeChannel.SenderId, ClaudeChannel.SenderName)));

			await target.SendAsync(BuildWhisper(ClaudeChannel.SenderId, message.Value));

			_claudeChannel.Append(ClaudeChannel.SenderId, ClaudeChannel.SenderName, $"(whisper to {targetCharacterId:X8}) {message.Value}", true, true);
			return true;
		}

		public async Task SendSystemMessage(uint characterId, ChatMarkup message)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
			{
				Logger.LogInformation("Skipping ChatServer system message for character {CharacterId:X8} because no ChatServer client is connected yet. Message: {Message}", characterId, message);
				return;
			}

			await SendSystemMessage(client, message);
		}

		public Task SystemBroadcast(ChatMarkup message)
		{
			return Task.WhenAll(NetworkClients.Select(c => SendSystemMessage(c, message)));
		}

		// Relays a channel chat message (0x0041) to every connected client, preceded by the sender's identity
		// (0x0014) so the client can show the speaker's name. The channel (type+id) from the original post is kept
		// verbatim - the client only displays it in a channel it is subscribed to, so the per-channel routing is the
		// client's subscription filter (global channels reach everyone; ~Playfield's id is a map id, so only same-map
		// clients are subscribed to it and the rest ignore it). The sender sees their own message too.
		private async Task BroadcastChannelChat(INetworkClient sender, byte channelType, uint channelId, string message)
		{
			var senderId = sender.Session.Character.Id;
			var senderName = sender.Session.Character.Name?.Length > 0
				? sender.Session.Character.Name
				: $"Character{senderId}";

			foreach (var client in NetworkClients)
			{
				await client.SendAsync(new ChatServerMessage(ChatSenderInfoCodec.Opcode, ChatSenderInfoCodec.Build(senderId, senderName)));

				await client.SendAsync(new ChatServerMessage(ChannelChatCodec.Opcode, ChannelChatCodec.Build(channelType, channelId, senderId, message)));
			}
		}

		// An incoming tell (0x001E): [u32 peerCharacterId][string][00 01 00] - the peer slot carries the SENDER's
		// id on delivery (and the target's id in the client's request, same shape both ways). Byte-exact against
		// the whisper capture (WhisperTests). The receiving client resolves the name from the preceding 0x0014.
		internal static ChatServerMessage BuildWhisper(uint peerCharacterId, string message)
		{
			return new ChatServerMessage(WhisperCodec.Opcode, WhisperCodec.Build(peerCharacterId, message));
		}

		// A chat-channel subscription (0x003C): [type][id][name][flag][0x0000]. The channel's (type, name, flag) comes
		// from its ChatChannels descriptor, so no caller spells one out. Byte layout verified from the raw login pcap.
		private static Task SendChannelSubscription(INetworkClient client, ChatChannel channel, uint id)
		{
			return client.SendAsync(new ChatServerMessage(ChannelSubscriptionCodec.Opcode, ChannelSubscriptionCodec.Build(channel.Type, id, channel.Name, channel.Flag)));
		}

		// The subscription's twin (0x003D): [type][id] - the channel this client is no longer on. It names no channel
		// NAME, which is exactly what identifies it as the subscription's leading pair (#532).
		private static Task SendChannelUnsubscribe(INetworkClient client, ChatChannel channel, uint id)
		{
			return client.SendAsync(new ChatServerMessage(ChannelUnsubscribeCodec.Opcode, ChannelUnsubscribeCodec.Build(channel.Type, id)));
		}

		// The channels that follow something the character belongs to, brought in line with what they belong to right
		// now. This IS the login burst's second half - it calls the same refreshes a mid-session change does, so there
		// is no second code path that can drift from them. Each is a no-op when there is nothing to belong to.
		//
		// ~Team is deliberately NOT here: retail's login burst carries ~Playfield and ~Guild, but every captured ~Team
		// subscription follows a team FORMING, never a login. A team is live CS-tier state that does not survive the
		// character's disconnect anyway, so there would be nothing to restore.
		private async Task SubscribeMembershipChannels(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			await RefreshPlayfieldChannel(character.Id, character.MapId);
			await RefreshGuildChannel(character.Id, character.GuildId);
		}

		// Moves the client's ~Playfield channel to the map they are now on. Retail does exactly this on a zone change:
		// 0x003D on the old channel, then 0x003C on the new one.
		//
		// IDEMPOTENT by design, so the world tier can call it after ANY map change without remembering what it last
		// sent - a map change funnels through one place (the MapChange packet) but a character's MapId is assigned from
		// several, and a "refresh" that has to be paired with the right previous value would be wrong the first time
		// somebody assigned it somewhere new. The session carries what was last subscribed, so a repeat is a no-op.
		public async Task RefreshPlayfieldChannel(uint characterId, uint mapId)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session == null || mapId == 0)
				return;

			var current = client.Session.PlayfieldChannelId;
			client.Session.PlayfieldChannelId = await MoveChannel(client, current, mapId, ChatChannels.Playfield);
			if (current != mapId)
				Logger.LogInformation("ChatServer ~Playfield for {Character:X8}: {From} -> {To}", characterId, current, mapId);
		}

		// Moves the client's ~Guild channel to the guild they are now in - the membership twin of
		// RefreshPlayfieldChannel, and idempotent for the same reason: a guild id is assigned from several places
		// (create, invite accept, kick, leave) and this is called from all of them.
		//
		// ⚠️ The SUBSCRIBE side is captured (create frame 369, and the login burst of a guilded character). The
		// UNSUBSCRIBE on leaving a guild is not: no capture in the corpus ever leaves one. It is the same 0x003D
		// (type, id) the corpus does show retiring a ~Team when the group dissolves and a ~Playfield on a zone change,
		// so it is that message applied to this channel, not a new one - but the guild case itself is inferred.
		// [V0: retail's 0x003D on a guild leave/kick]
		public async Task RefreshGuildChannel(uint characterId, uint? guildId)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session == null)
				return;

			var current = client.Session.GuildChannelId;
			client.Session.GuildChannelId = await MoveChannel(client, current, guildId ?? 0, ChatChannels.Guild);
			if (current != (guildId ?? 0))
				Logger.LogInformation("ChatServer ~Guild for {Character:X8}: {From:X} -> {To:X}", characterId, current, guildId ?? 0);
		}

		// Moves the client's ~Team channel to the team they are now in - the third instance of the same mechanism, and
		// the one the corpus pins on BOTH sides (#668): `login_2-clients_group-trade-friend` subscribes every member on
		// the accept and unsubscribes them all on the dissolve, three teams in a row.
		//
		// Like the guild channel, the id IS the team id - the one DispatchYouJoinedTeam carries as an ID32 of type
		// 0xDEA9 (accept frame 691 -> `0000dea9 0051dd9a` at 693 -> `82 0051dd9a "~Team"` at 701/702).
		// [V2: login_2-clients_group-trade-friend.tshark.json]
		public async Task RefreshTeamChannel(uint characterId, uint? teamId)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session == null)
				return;

			var current = client.Session.TeamChannelId;
			client.Session.TeamChannelId = await MoveChannel(client, current, teamId ?? 0, ChatChannels.Team);
			if (current != (teamId ?? 0))
				Logger.LogInformation("ChatServer ~Team for {Character:X8}: {From:X} -> {To:X}", characterId, current, teamId ?? 0);
		}

		// The move itself, shared by the three channels that follow something the player belongs to (their map, their
		// guild, their team): drop the old one, subscribe the new one, and return what is now subscribed so the caller
		// can stamp its session slot. Either side may be 0 for "none", which is what makes it total - a first
		// subscription, a move, and a plain drop are the same three lines.
		//
		// Comparing against the session slot rather than against the character's own state is what makes every caller
		// idempotent: the slot is what we last TOLD the client, which is a different fact from where the character is.
		private static async Task<uint> MoveChannel(INetworkClient client, uint current, uint wanted, ChatChannel channel)
		{
			if (current == wanted)
				return current;

			if (current != 0)
				await SendChannelUnsubscribe(client, channel, current);

			if (wanted != 0)
				await SendChannelSubscription(client, channel, wanted);

			return wanted;
		}

		private static Task SendSystemMessage(INetworkClient client, ChatMarkup message)
		{
			return client.SendAsync(new ChatServerMessage(ChatSystemMessageCodec.Opcode, ChatSystemMessageCodec.Build(message.Value)));
		}
	}
}
