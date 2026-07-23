using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Server.ChatServer;
using Microsoft.Extensions.Logging;

namespace Faolan.ChatServer
{
	partial class ChatServerListener
	{
		public async Task CharacterSay(uint characterId, string message, IReadOnlyCollection<uint>? recipientCharacterIds = null)
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

				await new FaolanStream()
					.WriteUInt16(ChatServerInterfaceOpcodes.CharacterChat) // opcode
					.WriteArrayUInt16Length(s => s
						.WriteUInt32(characterId)
						.WriteString(message)
						.WriteUInt16(0x0001) // type?
						.WriteByte(0))
					.SendAsync(client);
			}
		}

		public async Task<int> SendClaudeChannelMessage(string message)
		{
			// Claude's post on the Claude channel: the same 0x0014 sender-info + 0x0041 relay pair as a player post
			// (see BroadcastChannelChat), but under the synthetic Claude identity. Every client is subscribed to the
			// channel (Hello sends the subscription), so a plain broadcast reaches everyone.
			var delivered = 0;
			foreach (var client in NetworkClients)
			{
				await new FaolanStream()
					.WriteUInt16(ChatServerInterfaceOpcodes.ChatSenderInfo)
					.WriteArrayUInt16Length(s => s.WriteUInt32(ClaudeChannel.SenderId).WriteUInt32(0x00000000).WriteString(ClaudeChannel.SenderName))
					.SendAsync(client);

				await new FaolanStream()
					.WriteUInt16(ChatServerInterfaceOpcodes.ChannelChat)
					.WriteArrayUInt16Length(s =>
						s.WriteByte(ClaudeChannel.ChannelType).WriteUInt32(ClaudeChannel.ChannelId).WriteUInt32(ClaudeChannel.SenderId).WriteString(message).WriteUInt16(0x0000))
					.SendAsync(client);

				delivered++;
			}

			_claudeChannel.Append(ClaudeChannel.SenderId, ClaudeChannel.SenderName, message, true);

			return delivered;
		}

		// Whispers a single character as the synthetic Claude identity: the 0x0014 sender info + the 0x001E tell
		// (the exact delivery shape of a player tell - see the whisper capture / WhisperTests). Logged on the
		// Claude channel log so the bridge conversation history stays complete.
		public async Task<bool> SendClaudeWhisper(uint targetCharacterId, string message)
		{
			var target = ClientForCharacter(targetCharacterId);
			if (target == null)
				return false;

			await new FaolanStream()
				.WriteUInt16(ChatServerInterfaceOpcodes.ChatSenderInfo)
				.WriteArrayUInt16Length(s => s.WriteUInt32(ClaudeChannel.SenderId).WriteUInt32(0x00000000).WriteString(ClaudeChannel.SenderName))
				.SendAsync(target);

			await target.SendAsync(BuildWhisper(ClaudeChannel.SenderId, message));

			_claudeChannel.Append(ClaudeChannel.SenderId, ClaudeChannel.SenderName, $"(whisper to {targetCharacterId:X8}) {message}", true, true);
			return true;
		}

		public async Task SendSystemMessage(uint characterId, string message)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
			{
				Logger.LogInformation("Skipping ChatServer system message for character {CharacterId:X8} because no ChatServer client is connected yet. Message: {Message}", characterId, message);
				return;
			}

			await SendSystemMessage(client, message);
		}

		public Task SystemBroadcast(string message)
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
				await new FaolanStream()
					.WriteUInt16(ChatServerInterfaceOpcodes.ChatSenderInfo)
					.WriteArrayUInt16Length(s => s.WriteUInt32(senderId).WriteUInt32(0x00000000).WriteString(senderName))
					.SendAsync(client);

				await new FaolanStream()
					.WriteUInt16(ChatServerInterfaceOpcodes.ChannelChat)
					.WriteArrayUInt16Length(s => s.WriteByte(channelType).WriteUInt32(channelId).WriteUInt32(senderId).WriteString(message).WriteUInt16(0x0000))
					.SendAsync(client);
			}
		}

		// An incoming tell (0x001E): [u32 peerCharacterId][string][00 01 00] - the peer slot carries the SENDER's
		// id on delivery (and the target's id in the client's request, same shape both ways). Byte-exact against
		// the whisper capture (WhisperTests). The receiving client resolves the name from the preceding 0x0014.
		internal static byte[] BuildWhisper(uint peerCharacterId, string message)
		{
			using var stream = new FaolanStream();
			stream.WriteUInt16(ChatServerInterfaceOpcodes.Whisper)
				.WriteArrayUInt16Length(s => s.WriteUInt32(peerCharacterId).WriteString(message).WriteByte(0x00).WriteByte(0x01).WriteByte(0x00));
			return stream.ToArray();
		}

		// A chat-channel subscription (0x003C): [type][id][name][flag][0x0000]. Sent on ChatServer connect for each
		// channel the character joins (~Trial/~Global/~LFG/~GlobalEnglish/~Playfield/~NewbieHelp). Byte layout
		// verified from the raw login pcap.
		private static Task SendChannelSubscription(INetworkClient client, byte type, uint id, string name, uint flag)
		{
			return new FaolanStream()
				.WriteUInt16(ChatServerInterfaceOpcodes.ChannelSubscription)
				.WriteArrayUInt16Length(s => s.WriteByte(type).WriteUInt32(id).WriteString(name).WriteUInt32(flag).WriteUInt16(0x0000))
				.SendAsync(client);
		}

		private static Task SendSystemMessage(INetworkClient client, string message)
		{
			return new FaolanStream()
				.WriteUInt16(ChatServerInterfaceOpcodes.SystemMessage)
				.WriteArrayUInt16Length(s => s.WriteUInt16(0x0000).WriteString(message).WriteUInt16(0x0000))
				.SendAsync(client);
		}
	}
}
