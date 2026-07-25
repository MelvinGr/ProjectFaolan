using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Packets.ChatServer;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.GameServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.ChatServer
{
	public partial class ChatServerListener : ChatServerBase, IChatServerProvider
	{
		private readonly ClaudeChannel _claudeChannel;
		private readonly IChatServerDispatcher _dispatcher;
		private readonly IGameServerDispatcher _gameServerDispatcher;

		// `port` is the hosting realm's ChatServerPort (from its Realm row - the single source of truth for the
		// realm-local service ports, #324), passed in at registration rather than read from global config.
		public ChatServerListener(ushort port, ILogger<ChatServerListener> logger, IConfiguration configuration, IDatabaseRepository database, IChatServerDispatcher dispatcher,
			IGameServerDispatcher gameServerDispatcher, ClaudeChannel claudeChannel) : base(port, logger, configuration, database, new ChatPacketTracer(logger))
		{
			_dispatcher = dispatcher;
			_gameServerDispatcher = gameServerDispatcher;
			_claudeChannel = claudeChannel;
		}

		public override Task StartAsync(CancellationToken cancellationToken)
		{
			_dispatcher.Register(this);
			return base.StartAsync(cancellationToken);
		}

		public override Task StopAsync(CancellationToken cancellationToken)
		{
			_dispatcher.Unregister(this);
			return base.StopAsync(cancellationToken);
		}

		protected override async Task ReceivedPacket(INetworkClient client, ChatServerMessage packet)
		{
			LogPacketReceived(in packet);

			var body = packet.BodyReader();
			switch ((ChatServerOpcodes)packet.Opcode)
			{
				case ChatServerOpcodes.Hello:
				{
					var unk0 = body.ReadUInt32();
					var clientInstance = body.ReadUInt32();
					var accountId = body.ReadUInt32();

					client.Session.Account = (await Database.GetAccount(accountId))!;
					if (client.Session.Account == null)
						throw new Exception($"Account not found: {accountId:X}");

					client.Session.Account.ClientInstance = clientInstance;

					client.Session.Character = (await Database.GetCharacterForAccount(accountId, clientInstance))!;
					if (client.Session.Character == null)
						throw new Exception($"Character not found for account: {accountId:X} and clientInstance: {clientInstance:X}");

					Logger.LogInformation("ChatServer hello decoded: unk0={Unk0:X}, clientInstance={ClientInstance:X8}, accountId={AccountId:X8}", unk0, clientInstance, accountId);

					// Retail sends the full chat-channel subscription list FIRST (one 0x003C per channel), then the
					// 0x0005 ready marker, the 0x0014 character info, and finally the welcome message. Verified from
					// the raw pcap. Channel ids/types are fixed per channel,
					// except ~Playfield whose id is the character's current map. ~Trial carries 0x00008244, the rest
					// 0x00008044 (a per-channel flag).
					await SendChannelSubscription(client, 0x04, 0x0000232A, "~Trial", 0x00008244);
					await SendChannelSubscription(client, 0x04, 0x00002328, "~Global", 0x00008044);
					await SendChannelSubscription(client, 0x04, 0x00002329, "~LFG", 0x00008044);
					await SendChannelSubscription(client, 0x11, 0x00002335, "~GlobalEnglish", 0x00008044);

					if (client.Session.Character?.MapId > 0)
						await SendChannelSubscription(client, 0x90, client.Session.Character.MapId, "~Playfield", 0x00008044);

					await SendChannelSubscription(client, 0x87, 0x001E8480, "~NewbieHelp", 0x00008044);

					// The server-defined Claude channel: posts on it are bridged to Claude via the MCP chat tools.
					// Only offered while the MCP server is hosted - without it nothing reads the log or can reply.
					if (Configuration.McpEnabled)
						await SendChannelSubscription(client, ClaudeChannel.ChannelType, ClaudeChannel.ChannelId, ClaudeChannel.ChannelName, ClaudeChannel.ChannelFlag);

					// An empty-bodied 0x0005 beat retail sends here; the method it names isn't recovered yet.
					await client.SendAsync(new ChatServerMessage(0x0005));

					var character = client.Session.Character!;
					await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.ChatSenderInfo, s => s
						.WriteUInt32(character.Id)
						.WriteUInt32(0x00000000)
						.WriteString(character.Name?.Length > 0
							? character.Name
							: $"Character{character.Id}")));

					await SendSystemMessage(client, Configuration.WelcomeString); //.Replace("$BUILDSTRING", Global.BuildString));
					await SendSystemMessage(client, Global.BuildString.Replace("\n", "<br />"));

					// Restore the stored friend list. ⚠️ The MESSAGES are the captured ones (the same pair an add
					// sends), but this TIMING is not: the captured account had no friends before it added them, so
					// login-with-friends confirms both the shape and the place: 0x0014 + 0x0028 per friend, in the same
					// burst as the channel subscriptions and the welcome message. It goes HERE rather than on the
					// client's chat-init (0x0064), which arrives at most once across two clients in the corpus and
					// never at all from ours - hooking it there meant the restore never ran.
					// Retail restores the list as 0x0014 + 0x0028 pairs with NO presence push - that belongs to an add.
					foreach (var friend in await Database.GetFriends(character.Id))
						await SendFriendEntry(client, friend, false);

					// ...and tell everyone who has US on their list that we are now online. Without this a friend's
					// status is frozen at whatever it happened to be when the WATCHER logged in: whoever connects first
					// sees the other as offline forever, because the restore above can only report who is already here.
					await AnnouncePresenceToWatchers(character.Id, true);

					break;
				}

				case ChatServerOpcodes.ChannelChat:
				{
					// The client posted a message to a chat channel: [byte type][u32 channelId][string][0000].
					// Relay it to every connected client as the sender's identity (0x0014) + the message (0x0041
					// with the sender id inserted). The channel (type+id) is preserved, so each client's own channel
					// subscriptions decide whether/where it shows - that is what makes the multiple channels work
					// (global channels reach everyone; ~Playfield's id is a map, so only same-map clients are
					// subscribed to it and the rest ignore it).
					var channelType = body.ReadByte();
					var channelId = body.ReadUInt32();
					var message = body.ReadString();

					Logger.LogInformation("ChatServer channel chat from {Character} on channel {Type:X2}/{Id:X8}: {Message}", client.Session.Character?.Name ?? "?", channelType, channelId, message);

					// A '.command' (not '..') from a GM runs as a GM command in ANY channel, just like in /say -
					// routed to the player's world session via the GameServer dispatcher. The command line is NOT
					// echoed to chat (and is kept out of the Claude bridge log below so Claude doesn't "reply" to it).
					if (await TryRunChatGmCommand(client, message))
						break;

					await BroadcastChannelChat(client, channelType, channelId, message);

					// #95: persist the channel post to chat history (write-behind, never breaks the live relay above).
					await PersistChat(client, channelType, channelId, null, false, message);

					// Posts on the Claude channel are additionally recorded for the MCP bridge (Claude polls the log
					// and replies via SendClaudeChannelMessage); the normal broadcast above still shows them in-game.
					if (Configuration.McpEnabled && channelId == ClaudeChannel.ChannelId)
					{
						var character = client.Session.Character!;
						var senderId = character.Id;
						var senderName = character.Name!;
						_claudeChannel.Append(senderId, senderName, message, false);
					}

					break;
				}

				case ChatServerOpcodes.Whisper:
				{
					// A tell: [u32 targetCharacterId][string text][00 01 00] - op 0x001E both directions (whisper
					// capture 2026-06-12, Hoxxling <-> Settuc). Deliver to the target as the sender's identity
					// (0x0014, like channel chat) + the same 0x001E shape with the SENDER's id in the peer slot.
					// The sender's client renders its own outgoing tell locally - retail echoes nothing back.
					var targetCharacterId = body.ReadUInt32();
					var message = body.ReadString();
					var senderId = client.Session.Character.Id;
					var senderName = client.Session.Character.Name!;

					Logger.LogInformation("ChatServer whisper {Sender} -> {Target:X8}: {Message}", senderName, targetCharacterId, message);

					// #95: persist the whisper to chat history (channelType/id 0 for a tell; recipient in TargetId).
					await PersistChat(client, 0, 0, targetCharacterId, true, message);

					// A tell to Claude's synthetic identity goes into the MCP bridge log as a private message
					// (the client learns Claude's id from the channel posts' 0x0014, so /tell Claude works). The
					// whisper MODE is carried structurally (viaWhisper) so the bridge can have Claude reply privately,
					// matching how it was addressed - NOT as a "(whisper) " text prefix the bridge would have to parse.
					if (Configuration.McpEnabled && targetCharacterId == ClaudeChannel.SenderId)
					{
						_claudeChannel.Append(senderId, senderName, message, false, true);
						break;
					}

					var target = ClientForCharacter(targetCharacterId);
					if (target == null)
					{
						// Offline/unknown target: not captured (both sides were online); a system message is the
						// graceful fallback so the tell doesn't vanish silently.
						await SendSystemMessage(client, "Player is not online.");
						break;
					}

					await target.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.ChatSenderInfo, s => s.WriteUInt32(senderId).WriteUInt32(0x00000000).WriteString(senderName)));

					await target.SendAsync(BuildWhisper(senderId, message));

					break;
				}

				// 0x0028 AddFriend([u32 characterId]): the friend list lives HERE, not on the game server, and is
				// ONE-SIDED - no accept step, so the add is a write plus the two replies retail sends, in this order:
				// the presence push (0x0025, addressed by NAME) then the friend's state (0x0028, ending in its CLASS).
				case ChatServerOpcodes.AddFriend:
				{
					var friendId = FriendListCodec.ParseCharacterId(body);
					var friend = await Database.GetCharacter(friendId);
					if (friend == null)
					{
						// Not captured (the capture only ever adds a character that resolves), so say so rather than
						// leaving the client's list silently short.
						await SendSystemMessage(client, "No such character.");
						break;
					}

					var added = await Database.AddFriend(client.Session.Character.Id, friendId);

					await SendFriendEntry(client, friend, true);
					Logger.LogInformation("Friends: {Character} added {Friend}{Already}.", client.Session.Character.Name, friend.Name, added
						? ""
						: " (already on the list)");

					break;
				}

				// 0x0029 RemoveFriend([u32 characterId]): retail echoes the id straight back.
				case ChatServerOpcodes.RemoveFriend:
				{
					var friendId = FriendListCodec.ParseCharacterId(body);
					var removed = await Database.RemoveFriend(client.Session.Character.Id, friendId);
					await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.FriendRemoved, s => s.WriteArray(FriendListCodec.BuildCharacterRef(friendId))));
					Logger.LogInformation("Friends: {Character} removed {Friend:X8}{Missing}.", client.Session.Character.Name, friendId, removed
						? ""
						: " (was not on the list)");

					break;
				}

				case ChatServerOpcodes.ChatReady:
				{
					// Small chat-init handshake (body 00 01 02); retail echoes it back verbatim.
					await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.ChatReadyAck, s => s.WriteArray(body.ReadRemainingArray())));

					break;
				}

				default:
				{
					LogUnknownPacket(in packet);
					break;
				}
			}
		}

		// #95: write-behind chat-history persistence. Records a channel post (isWhisper=false, target=null) or a tell
		// (isWhisper=true, target=recipient) to the ChatMessages table. Guarded: a DB hiccup must NEVER break the live
		// chat relay, so any failure is logged and swallowed. The sender identity matches the wire (ClientInstance ==
		// character id for a player; see player_handle_equals_character_id).
		// Re-sends this character's entry to every ONLINE watcher, so their list reflects a login or a logout.
		//
		// ⚠️ The MESSAGE is the captured one (the same state an add or a login restore sends), but the TRIGGER is
		// inferred: no capture covers a friend logging in or out while someone watches. It is hard to see how a friend
		// list could work without it - presence would never change - so this is the smallest step that makes the feature
		// coherent, rather than a new packet invented for it.
		private async Task AnnouncePresenceToWatchers(uint characterId, bool online)
		{
			var self = await Database.GetCharacter(characterId);
			if (self == null)
				return;

			foreach (var watcherId in await Database.GetFriendWatchers(characterId))
			{
				if (ClientForCharacter(watcherId) is { } watcher)
					await SendFriendEntry(watcher, self, false, online);
			}
		}

		// One friend's list entry:		// One friend's list entry: the id -> NAME binding (0x0014) then its state (0x0028), in retail's order. `presence`
		// prepends the 0x0025 push, which retail sends on an ADD but NOT when restoring the list at login.
		//
		// ⚠️ The 0x0014 is not optional decoration. Without it the client holds an id and a state with no name attached
		// and renders NOTHING - no entry and no error - which is exactly how this looked while the other two messages
		// were already byte-perfect. ONLINE is whether the character has a live chat connection here, the only thing
		// this tier can actually know.
		private async Task SendFriendEntry(INetworkClient client, Character friend, bool presence, bool? onlineOverride = null)
		{
			var name = friend.Name ?? "";
			if (presence)
				await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.FriendPresence, s => s.WriteArray(FriendListCodec.BuildPresence(name))));

			await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.ChatSenderInfo, s => s.WriteArray(FriendListCodec.BuildNameBinding(friend.Id, name))));

			// The stamp is a UNION on the online flag: an OFFLINE friend gets a real unix LAST-SEEN (the captured offline
			// friend's value is 2h06 before the capture was taken, which is what identified it), an online one gets the
			// captured constant, whose meaning is still open (Gitea #426 - it is not a time at all: 0x01EDA8B2 reads as
			// 1971). Same shape GuildService's member record already uses, and the same column.
			// The logout announce has to say "offline" while the socket is still up, so the caller can override.
			var online = onlineOverride ?? ClientForCharacter(friend.Id) != null;
			var stamp = online
				? FriendListCodec.OnlineStamp
				: (uint)friend.LastConnection!.Value.ToUnixTimeSeconds();

			await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.FriendAdded,
				s => s.WriteArray(FriendListCodec.BuildState(friend.Id, online, (byte)friend.Level, friend.Class, stamp))));
		}

		// A logout is a presence change for everyone who has this character on their friend list. The chat connection is
		// the one that knows: it is per-character and lives for the whole session.
		protected override async Task ClientDisconnected(INetworkClient client)
		{
			if (client.Session.Character is { } character)
				await AnnouncePresenceToWatchers(character.Id, false);

			await base.ClientDisconnected(client);
		}

		private async Task PersistChat(INetworkClient client, byte channelType, uint channelId, uint? target, bool isWhisper, string message)
		{
			try
			{
				var senderId = client.Session.Character.Id;
				var senderName = client.Session.Character.Name!;

				await Database.SaveChatMessage(new ChatMessage
				{
					SentAt = DateTimeOffset.UtcNow,
					SenderId = senderId,
					SenderName = senderName,
					ChannelType = channelType,
					ChannelId = channelId,
					TargetId = target,
					IsWhisper = isWhisper,
					Text = message.Length > 512
						? message[..512]
						: message
				});
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to persist chat message (history only; live relay unaffected).");
			}
		}

		/// <summary>
		///     If <paramref name="message" /> is a '.command' (single leading dot) from a GameMaster, run it as a GM
		///     command on the player's world session (via the GameServer dispatcher) and return true so the caller
		///     suppresses the chat broadcast/echo. Mirrors the '/say' GM gate in
		///     <see cref="GameServerListener" /> so commands work in every chat channel.
		/// </summary>
		private async Task<bool> TryRunChatGmCommand(INetworkClient client, string message)
		{
			var text = message?.TrimStart() ?? string.Empty;
			if (text.Length <= 2 || text[0] != '.' || text[1] == '.')
				return false;

			if (client.Session.Account is not { } account || account.Type < AccountType.GameMaster)
				return false;

			await _gameServerDispatcher.RunGmCommand(client.Session.Character.Id, text);
			return true;
		}
	}
}
