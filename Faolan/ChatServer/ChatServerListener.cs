using System;
using System.Linq;
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
		// The ~Playfield channel: type 0x90, and its ID IS the map id - which is what makes it follow the player and
		// need re-subscribing on a zone change (#562). The flag is the per-channel word every non-~Trial channel uses.
		private const byte PlayfieldChannelType = 0x90;
		private const string PlayfieldChannelName = "~Playfield";
		private const uint PlayfieldChannelFlag = 0x00008044;

		// The level + class the virtual Claude friend entry reports (#100). See SendClaudeFriendEntry: both are fields
		// the client's tooltip shows and neither has a "none" encoding, so they are a choice, not a decode.
		private const byte ClaudeVirtualFriendLevel = 80;
		private const ConanClass ClaudeVirtualFriendClass = ConanClass.PriestOfMitra;

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
					// Body = [u32 version][u32 clientInstance][u32 cookie][string language]. The three-field head is the
					// same shape the client sends to the CSPlayerAgent as Authenticate(nCharacterInstance, nCookie,
					// communityVersion) - capture-confirmed: in every trace the chat cookie is byte-identical to the
					// CSPlayerAgent one, which is the `uCookie` the UniverseAgent handed out in DispatchAckAuthenticate.
					// ⚠️ We look the account up BY that cookie, which only works because SendPlayerAgentRealm issues
					// Account.Id AS the cookie (Faolan/UniverseAgent/Packets.cs) - if the cookie ever becomes a real
					// opaque token, this lookup has to go through a cookie->account map instead.
					// Version is 1 in all 5239 captured Hellos; language is the client's UI language ("English").
					var protocolVersion = body.ReadUInt32();
					var clientInstance = body.ReadUInt32();
					var accountId = body.ReadUInt32();
					var language = body.ReadString();

					client.Session.Account = (await Database.GetAccount(accountId))!;
					if (client.Session.Account == null)
						throw new Exception($"Account not found: {accountId:X}");

					client.Session.Account.ClientInstance = clientInstance;

					// No `!` here on purpose: GetCharacterForAccount returns null for a character this account does not own,
					// and forgiving that makes the check look like dead code someone will helpfully delete.
					client.Session.Character = await Database.GetCharacterForAccount(accountId, clientInstance)
						?? throw new Exception($"Character not found for account: {accountId:X} and clientInstance: {clientInstance:X}");

					Logger.LogInformation("ChatServer hello decoded: version={Version}, clientInstance={ClientInstance:X8}, cookie/accountId={AccountId:X8}, language={Language}", protocolVersion, clientInstance, accountId, language);

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
					{
						await SendChannelSubscription(client, PlayfieldChannelType, client.Session.Character.MapId, PlayfieldChannelName, PlayfieldChannelFlag);
						// Remember it, or the first RefreshPlayfieldChannel would not know which channel to drop.
						client.Session.PlayfieldChannelId = client.Session.Character.MapId;
					}

					await SendChannelSubscription(client, 0x87, 0x001E8480, "~NewbieHelp", 0x00008044);

					// The server-defined Claude channel: posts on it are bridged to Claude via the MCP chat tools.
					// Only offered while the MCP server is hosted - without it nothing reads the log or can reply.
					if (Configuration.McpEnabled)
						await SendChannelSubscription(client, ClaudeChannel.ChannelType, ClaudeChannel.ChannelId, ClaudeChannel.ChannelName, ClaudeChannel.ChannelFlag);

					// The empty-bodied answer to Hello. Named HelloAck in #532 - from position, since an empty body
					// gives nothing else to go on, but the position is invariant across all 41 captures.
					await client.SendAsync(new ChatServerMessage((ushort)ChatServerInterfaceOpcodes.HelloAck));

					var character = client.Session.Character!;
					await client.SendAsync(new ChatServerMessage(ChatSenderInfoCodec.Opcode, ChatSenderInfoCodec.Build(character.Id, character.Name?.Length > 0
						? character.Name
						: $"Character{character.Id}")));

					await SendSystemMessage(client, Configuration.WelcomeString); //.Replace("$BUILDSTRING", Global.BuildString));
					await SendSystemMessage(client, ChatMarkup.Lines([.. Global.BuildString.Split('\n').Select(ChatMarkup (l) => l)]));

					// Restore the stored friend list. ⚠️ The MESSAGES are the captured ones (the same pair an add
					// sends), but this TIMING is not: the captured account had no friends before it added them, so
					// login-with-friends confirms both the shape and the place: 0x0014 + 0x0028 per friend, in the same
					// burst as the channel subscriptions and the welcome message. It goes HERE rather than on the
					// client's chat-init (0x0064), which arrives at most once across two clients in the corpus and
					// never at all from ours - hooking it there meant the restore never ran.
					// Retail restores the list as 0x0014 + 0x0028 pairs with NO presence push - that belongs to an add.
					foreach (var friend in await Database.GetFriends(character.Id))
						await SendFriendEntry(client, friend, false);

					// ...plus Claude, the virtual friend (#100). Same gate as the channel: an entry nobody can answer
					// would be a lie.
					if (Configuration.McpEnabled)
						await SendClaudeFriendEntry(client);

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

					await target.SendAsync(new ChatServerMessage(ChatSenderInfoCodec.Opcode, ChatSenderInfoCodec.Build(senderId, senderName)));

					await target.SendAsync(BuildWhisper(senderId, message));

					break;
				}

				// 0x0028 AddFriend([u32 characterId]): the friend list lives HERE, not on the game server, and is
				// ONE-SIDED - no accept step, so the add is a write plus the two replies retail sends, in this order:
				// the presence push (0x0025, addressed by NAME) then the friend's state (0x0028, ending in its CLASS).
				case ChatServerOpcodes.AddFriend:
				{
					var friendId = FriendRefCodec.Parse(body.ReadRemainingArray()).CharacterId;

					// Re-adding Claude (#100): there is no character row behind the synthetic identity, so the lookup
					// below would answer "No such character" for a friend the client can see is online. Re-send the
					// entry instead - the list is regenerated at login either way, so this only has to satisfy the
					// client in the moment.
					if (Configuration.McpEnabled && friendId == ClaudeChannel.SenderId)
					{
						await SendClaudeFriendEntry(client);
						break;
					}

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
					var friendId = FriendRefCodec.Parse(body.ReadRemainingArray()).CharacterId;
					var removed = await Database.RemoveFriend(client.Session.Character.Id, friendId);
					await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.FriendRemoved, FriendRefCodec.Build(friendId)));
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

		// Claude as a VIRTUAL friend (#100): an always-online list entry with no character behind it. It works because
		// this list is ONE-SIDED and server-pushed - there is no accept step and no roster the client validates an entry
		// against, so an entry simply IS the two messages SendFriendEntry sends, and the client renders what it is told.
		// The payoff is presence plus one-click access: a tell addressed to this id already routes into the MCP bridge
		// (see the Whisper case), so the entry is the missing front door rather than a new channel.
		//
		// Deliberately NOT written to the Friends table: SenderId is not a character id, the FK would refuse it, and the
		// entry is regenerated every login anyway. So a player who removes it gets it back next login - RemoveFriend
		// echoes the id and the delete no-ops. Worth knowing before that reads as a bug.
		private async Task SendClaudeFriendEntry(INetworkClient client)
		{
			await client.SendAsync(new ChatServerMessage(ChatSenderInfoCodec.Opcode, ChatSenderInfoCodec.Build(ClaudeChannel.SenderId, ClaudeChannel.SenderName)));

			// Online, so the stamp takes the playfield-token branch - and Claude is in no playfield, which is exactly
			// the case FriendStateCodec.OnlineStamp is the documented fallback for. LEVEL and CLASS are the two fields
			// the client's hover tooltip shows and the wire encodes no "none" for either, so they are a choice rather
			// than a fact: the level cap, and the support class, for a friend who exists to answer questions.
			await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.FriendAdded,
				FriendStateCodec.Build(ClaudeChannel.SenderId, true, ClaudeVirtualFriendLevel, ClaudeVirtualFriendClass, FriendStateCodec.OnlineStamp)));
		}

		// One friend's list entry: the id -> NAME binding (0x0014) then its state (0x0028), in retail's order. `presence`
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
				await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.FriendPresence, FriendPresenceCodec.Build(name)));

			await client.SendAsync(new ChatServerMessage(ChatSenderInfoCodec.Opcode, ChatSenderInfoCodec.Build(friend.Id, name)));

			// The stamp is a UNION on the online flag: an OFFLINE friend gets a real unix LAST-SEEN (the captured offline
			// friend's value is 2h06 before the capture was taken, which is what identified it), and an ONLINE one gets
			// the PLAYFIELD INSTANCE TOKEN of the playfield they are in (#426, answered by #454 - the same 0x01EDA8B2
			// turned out to be the instance token inside `TeamMemberRecord`'s embedded PlayfieldProxy, which is why it
			// is not a time and reads as 1971). That is a LIVE value, so it comes from the game tier through the
			// dispatcher seam rather than from a column; the captured constant remains the fallback for a friend the
			// game host does not have in a zone. Same union GuildService's member record already uses.
			// The logout announce has to say "offline" while the socket is still up, so the caller can override.
			var online = onlineOverride ?? ClientForCharacter(friend.Id) != null;
			var stamp = online
				? _gameServerDispatcher.GetCharacterLiveState(friend.Id)?.PlayfieldToken ?? FriendStateCodec.OnlineStamp
				: (uint)friend.LastConnection!.Value.ToUnixTimeSeconds();

			await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.FriendAdded, FriendStateCodec.Build(friend.Id, online, (byte)friend.Level, friend.Class, stamp)));
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
