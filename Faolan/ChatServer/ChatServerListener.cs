using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
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
		private readonly IChatServerDispatcher _dispatcher;
		private readonly IGameServerDispatcher _gameServerDispatcher;

		// The port is the hosting realm's ChatServerPort (from its Realm row - the single source of truth for the
		// realm-local service ports, #324), taken by INJECTION rather than read from global config: `HostRealm` is a
		// singleton the pre-host bootstrap registers, which is what lets this be a plain AddHostedService<T>().
		public ChatServerListener(HostRealm realm, ILogger<ChatServerListener> logger, IConfiguration configuration, IDatabaseRepository database, IChatServerDispatcher dispatcher,
			IGameServerDispatcher gameServerDispatcher) : base(realm.ChatServerPort, logger, configuration, database, new ChatPacketTracer(logger))
		{
			_dispatcher = dispatcher;
			_gameServerDispatcher = gameServerDispatcher;
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
					// the raw pcap. Every channel it holds is declared in ChatChannels; the standing ones carry a
					// constant id, so they are just that list.
					foreach (var (channel, id) in ChatChannels.StandingAtLogin(Configuration.McpEnabled))
						await SendChannelSubscription(client, channel, id);

					// ...then the ones that follow something the character belongs to - their map and their guild -
					// through the SAME calls a mid-session change takes, so the login burst and a later move cannot
					// drift apart. Each is a no-op when there is nothing to belong to, hence no condition here.
					await SubscribeMembershipChannels(client);

					// The empty-bodied answer to Hello. Named HelloAck in #532 - from position, since an empty body
					// gives nothing else to go on, but the position is invariant across all 41 captures.
					await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.HelloAck));

					var character = client.Session.Character!;
					await client.SendAsync(new ChatServerMessage(ChatSenderInfoCodec.Opcode, ChatSenderInfoCodec.Build(character.Id, character.Name?.Length > 0
						? character.Name
						: $"Character{character.Id}")));

					if(!string.IsNullOrWhiteSpace(Configuration.ServerWelcomeString))
						await SendSystemMessage(client, Configuration.ServerWelcomeString.Replace("$BUILDSTRING", Global.BuildString));

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

					// ...and tell everyone who has US on their list that we are now online. Without this a friend's
					// status is frozen at whatever it happened to be when the WATCHER logged in: whoever connects first
					// sees the other as offline forever, because the restore above can only report who is already here.
					await AnnouncePresenceToWatchers(character.Id, SocialStatus.Online);

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

				// 0x002A SetSocialStatus([u32 SocialStatus]): the player's own presence - what a friend's list shows
				// beside their name (#892). The chat tier owns the friend list, so it is the tier that has to be told,
				// and the client tells it directly rather than through the game or CS tiers.
				//
				// It is the LFG toggle's second half: every captured occurrence fires ~50 microseconds after the CS
				// tier's SetLookingForTeamStatus, 2 for on and 1 for off. Nothing answers it on the wire - retail sends
				// no acknowledgement in any capture - so the work is entirely in the fan-out: everyone who has this
				// character on their friend list gets their row re-sent, which is how the icon changes.
				case ChatServerOpcodes.SetSocialStatus:
				{
					var status = SocialStatusCodec.Parse(body.ReadRemainingArray());
					_socialStatus[client.Session.Character.Id] = status;

					await AnnouncePresenceToWatchers(client.Session.Character.Id, status);

					Logger.LogInformation("Social: {Character} is now {Status}.", client.Session.Character.Name, status);
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
		private async Task AnnouncePresenceToWatchers(uint characterId, SocialStatus status)
		{
			var self = await Database.GetCharacter(characterId);
			if (self == null)
				return;

			foreach (var watcherId in await Database.GetFriendWatchers(characterId))
			{
				if (ClientForCharacter(watcherId) is { } watcher)
					await SendFriendEntry(watcher, self, false, status);
			}
		}

		// The live presence a character has SET (0x002A). Chat-tier live state, like the roster on the CS tier: it is
		// per-connection by definition, so it is not persisted and it goes when the connection does. A character with a
		// live chat connection who has never set one is simply Online, which is what every captured record carries.
		private readonly ConcurrentDictionary<uint, SocialStatus> _socialStatus = [];

		private SocialStatus StatusOf(uint characterId)
		{
			if (ClientForCharacter(characterId) == null)
				return SocialStatus.Offline;

			return _socialStatus.TryGetValue(characterId, out var status)
				? status
				: SocialStatus.Online;
		}

		// One friend's list entry: the id -> NAME binding (0x0014) then its state (0x0028), in retail's order. `presence`
		// prepends the 0x0025 push, which retail sends on an ADD but NOT when restoring the list at login.
		//
		// ⚠️ The 0x0014 is not optional decoration. Without it the client holds an id and a state with no name attached
		// and renders NOTHING - no entry and no error - which is exactly how this looked while the other two messages
		// were already byte-perfect. ONLINE is whether the character has a live chat connection here, the only thing
		// this tier can actually know.
		private async Task SendFriendEntry(INetworkClient client, Character friend, bool presence, SocialStatus? statusOverride = null)
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
			// The STATUS is what the row renders (online / looking for group / away); ONLINE is the question the stamp
			// union asks, and all three online statuses answer it the same way. The logout announce has to say offline
			// while the socket is still up, so the caller can override.
			var status = statusOverride ?? StatusOf(friend.Id);
			var online = status != SocialStatus.Offline;
			var stamp = online
				? _gameServerDispatcher.GetCharacterLiveState(friend.Id)?.PlayfieldToken ?? FriendStateCodec.OnlineStamp
				: (uint)friend.LastConnection!.Value.ToUnixTimeSeconds();

			await client.SendAsync(new ChatServerMessage(ChatServerInterfaceOpcodes.FriendAdded, FriendStateCodec.Build(friend.Id, status, (byte)friend.Level, friend.Class, stamp)));
		}

		// A logout is a presence change for everyone who has this character on their friend list. The chat connection is
		// the one that knows: it is per-character and lives for the whole session.
		protected override async Task ClientDisconnected(INetworkClient client)
		{
			if (client.Session.Character is { } character)
			{
				_socialStatus.TryRemove(character.Id, out _);
				await AnnouncePresenceToWatchers(character.Id, SocialStatus.Offline);
			}

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

			if (client.Session.Account is not { } account || !account.IsGm)
				return false;

			await _gameServerDispatcher.RunGmCommand(client.Session.Character.Id, text);
			return true;
		}
	}
}
