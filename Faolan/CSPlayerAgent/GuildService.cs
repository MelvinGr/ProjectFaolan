using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// Guild RPC handlers + wire builders + flow state for the CsPlayerAgent connection (create / invite / accept /
	// decline / leave / MOTD / info). Formats decoded from the hoxxling guild_invite_accepted capture (see
	// tools/capture/decode_guild.py). Extracted from CsPlayerAgentListener as a composed collaborator: it owns the
	// pending-invite + per-client reply-endpoint state and routes guild events to other connected members. The
	// cross-connection client lookup (ClientForCharacter, on the Server base) is injected as a delegate seam; the
	// in-world guild-tag push rides the existing IGameServerDispatcher.
	public sealed class GuildService
	{
		// Retail addresses every CsPlayerAgent server->client packet FROM the guild-server object endpoint
		// {class = the class the client targeted, instance = a fixed server-object handle}. The client targets the
		// class with NO instance (its Authenticate/requests carry instance 0), and the captures show a nonzero server
		// instance on every reply (0x04 on the Hoxxling login, 0x52 on the DemoL80 create - so it is an arbitrary server
		// handle, not session data). We stamp a constant so our s2c headers match retail's shape instead of echoing the
		// client's instance-0 endpoint back as the sender.
		private const uint GuildServerInstance = 0x04;
		private static readonly ProtoEndpoint PushSender = Endpoints.Endpoint(Endpoints.CSPlayerAgentType, GuildServerInstance);
		private static readonly ProtoEndpoint PushReceiver = Endpoints.Endpoint(Endpoints.CSPlayerInterfaceType, 0);

		// The s2c endpoints for an unsolicited CsPlayerAgent PUSH (a guild event to a player who didn't just send a
		// request, so there is no incoming packet to echo). Retail addresses every reply the same way, and every
		// request carries the same endpoints - target = the guild-server class (CSPlayerAgent), caller =
		// CSPlayerInterface at instance 0 - so a push reuses the exact endpoints a reply would have echoed
		// (ReplySender(packet.Receiver) / packet.Sender). This is why we don't need to remember each client's endpoints.

		private readonly ConcurrentDictionary<uint, PendingGuildInvite> _pendingGuildInvites = [];
		private readonly Func<uint, INetworkClient?> _clientForCharacter;
		private readonly IDatabaseRepository _database;
		private readonly IGameServerDispatcher _gameServerDispatcher;
		private readonly ILogger _logger;

		public GuildService(IDatabaseRepository database, ILogger logger, IGameServerDispatcher gameServerDispatcher, Func<uint, INetworkClient?> clientForCharacter)
		{
			_database = database;
			_logger = logger;
			_gameServerDispatcher = gameServerDispatcher;
			_clientForCharacter = clientForCharacter;
		}

		// 0x201D DeclineGuildInvitation: drop the pending invite (logs the guild it was for). Returns the declined
		// invite's guild name (or null when there was none) so the listener can log it.
		public string? DeclineGuildInvitation(uint characterId)
		{
			return _pendingGuildInvites.TryRemove(characterId, out var declined)
				? declined.GuildName
				: null;
		}

		// 0x201C AcceptGuildInvitation(i_cGuildID): body is the guild ID32 from the 0x202A invite. Retail replies
		// with the 0x203F full update followed by the joiner's own 0x202F member-joined record (which also fans
		// out to the other online members), and sets the in-world tag with a GameServer 0x2550C99A.
		public async Task HandleAcceptGuildInvitation(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			body.ReadUInt32(); // ID32 type (0x0000DEB7)
			var guildId = body.ReadUInt32();

			if (!_pendingGuildInvites.TryRemove(character.Id, out var invite) || invite.GuildId != guildId)
			{
				_logger.LogWarning("AcceptGuildInvitation from {CharacterId:X8} for guild {GuildId:X} without a matching pending invite.", character.Id, guildId);
				return;
			}

			if (!await _database.SetCharacterGuild(character.Id, guildId, 0))
				return;

			character.GuildId = guildId;
			character.GuildRank = 0;

			var guild = await _database.GetGuild(guildId);
			if (guild == null)
				return;

			_logger.LogInformation("{Character} joined guild \"{Guild}\" (invited by {Inviter}).", character.Name, guild.Name, invite.InviterName);
			await SendGuildFullUpdate(client, ReplySender(packet.Receiver), packet.Sender, guild, character);
			await BroadcastGuildMemberJoined(guild, character); // includes the joiner, matching retail order
			await _gameServerDispatcher.SendGuildTag(character.Id, guild.Name);
		}

		// 0x201E ClientRequestGuildInfo([ID32 char]): the client resolves guild banners/tooltips of other
		// players with this. Reply is 0x2042 when the target is in the requester's own guild, 0x2043 (carrying
		// the guild name) when it is in another guild, and nothing when the target is guildless.
		public async Task HandleClientRequestGuildInfo(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // ID32 type (0x0000C350)
			var targetId = body.ReadUInt32();

			var target = await _database.GetCharacter(targetId);
			if (target?.GuildId is not { } targetGuildId)
				return;

			if (client.Session.Character?.GuildId == targetGuildId)
			{
				// [charId][guild ID32][03][02][charId][01] - the constants are not decoded (likely rank/flags);
				// taken verbatim from the capture (Dahviie, same-guild reply).
				await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchGuildUpdateMember,
					b => b.WriteUInt32(targetId).WriteUInt32(EntityClass.Guild).WriteUInt32(targetGuildId).WriteByte(0x03).WriteByte(0x02).WriteUInt32(targetId).WriteByte(0x01)));
				return;
			}

			var guild = await _database.GetGuild(targetGuildId);
			if (guild == null)
				return;

			// [charId][guild ID32][03][guildId][02][05][name][u32 0x3F1 = an EMPTY list][u32 0][u32 0] - verbatim from
			// the capture ("The Undreamed" reply); 0x3F1 also appears in the Authenticate 0x20D0 trailer, so it
			// is a server-wide constant rather than guild data.
			await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchGuildUpdateOther,
				b => b
					.WriteUInt32(targetId)
					.WriteUInt32(EntityClass.Guild)
					.WriteUInt32(targetGuildId)
					.WriteByte(0x03)
					.WriteUInt32(targetGuildId)
					.WriteByte(0x02)
					.WriteByte(0x05)
					.WriteString(guild.Name)
					.WriteUInt32(ListFrame.Empty)
					.WriteUInt32(0)
					.WriteUInt32(0)));
		}

		// 0x201F ClientRequestGuildName([ID32 guild]): the client resolving a guild id it has seen (a roster entry,
		// a renown ranking row) to a display name. Body is [u32 EntityClass.Guild][u32 guildId], reply is
		// DispatchGuildName [u32 guildId][string name] - NOT an ID32 on the way back, matching the field's `n`
		// prefix (`i_nGuildInstance`). Both directions are capture-verified as a PAIR in xxx_2clients_jumping:
		// the request at frame 1120 asks for guild 0x00075E9A and frame 1131 answers `00075e9a 000b "the chickas"`.
		// [V2: xxx_2clients_jumping.tshark.json frame 1131]
		public async Task HandleClientRequestGuildName(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // ID32 type (0x0000DEB7)
			var guildId = body.ReadUInt32();

			var guild = await _database.GetGuild(guildId);
			if (guild == null)
			{
				// Retail sends nothing for a guild it cannot resolve, and the client keeps showing the raw id. Say so
				// rather than inventing a placeholder name that would look like real data in the UI.
				_logger.LogWarning("ClientRequestGuildName for unknown guild 0x{GuildId:X8}; no reply.", guildId);
				return;
			}

			await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchGuildName,
				GuildNameCodec.Build(new GuildName(guildId, guild.Name))));
		}

		// 0x2021 CreateGuild. Body decoded from DemoL80_..._create-guild.pcapng (docs/subsystems/create-guild-decode.md):
		//   [name s16][u8 form][u16 0]   e.g. `0006 "faolan" 06 0000`. The `form` is the government/guild-form the
		// player picked in the registrar window (the blind impl used to drop it). NOTE: retail's IMMEDIATE reply is
		// DispatchGuildCreated (0x2044) - a [creator member record][guild header][renown/trailer] packet that opens
		// the window with NO relog - NOT the 0x203F we send here; that 0x2044 tail + its guild-chat 0x003c subs are
		// the documented follow-up. Today the guild still shows via the login 0x203F push after a relog.
		public async Task HandleCreateGuild(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			var name = body.ReadString();
			var form = (GuildGoverningForm)(body.Length - body.Position >= 1
				? body.ReadByte()
				: (byte)GuildGoverningForm.Cimmerian); // i_nGoverningForm (docs/subsystems/create-guild-decode.md)
			if (string.IsNullOrWhiteSpace(name) || character.GuildId != null)
			{
				_logger.LogWarning("CreateGuild rejected for {CharacterId:X8}: name \"{Name}\", already in guild: {InGuild}.", character.Id, name, character.GuildId != null);
				return;
			}

			var guild = await _database.CreateGuild(name, character.Id);
			if (guild == null)
			{
				// Retail answers with DispatchGuildBadName/-NonAlphanumericName here; those opcodes are not
				// decoded yet, so the failure is server-side only.
				_logger.LogWarning("CreateGuild failed for {CharacterId:X8}: guild name \"{Name}\" is taken.", character.Id, name);
				return;
			}

			character.GuildId = guild.Id;

			// STORE the form - it was read and dropped, so every guild kept the column's default and its rank ladder
			// was whatever that happened to be. And the leader's rung is per-form: Stygian tops out at 3, Cimmerian
			// and Harem at 4, the other four at 5, so a flat GuildRank.Leader was right for two forms of seven (#469).
			guild.GovernmentForm = form;
			character.GuildRank = (GuildRank)GuildRankLadder.LeaderRank(form);

			_logger.LogInformation("Guild \"{Name}\" ({GuildId:X}) created by {Character} as {Form}; leader rank {Rank} (\"{RankName}\").", guild.Name, guild.Id, character.Name, form,
				(byte)character.GuildRank, GuildRankLadder.NameOf(form, (byte)character.GuildRank!) ?? "?");
			// Retail's immediate create response is 0x2044 DispatchGuildCreated (opens the window with NO relog), not
			// the 0x203F full-update. If the client doesn't accept it the guild still shows on the next login (the
			// re-auth 0x203F push), so this only ADDS the no-relog path.
			await SendGuildCreated(client, ReplySender(packet.Receiver), packet.Sender, guild, character, (byte)form);
			await _gameServerDispatcher.SendGuildTag(character.Id, guild.Name);
		}

		// 0x2023 GuildInvite: same as by-name but the target is identified by ID32.
		public async Task HandleGuildInvite(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // ID32 type (0x0000C350)
			var targetId = body.ReadUInt32();
			var target = await _database.GetCharacter(targetId);
			await InviteCharacterToGuild(client, target, $"{targetId:X8}");
		}

		// 0x2022 GuildInviteByName(i_cCharacterName): inviter must be guilded; the target must be online (the
		// invite dialog is a live push - retail also rejects offline targets).
		public async Task HandleGuildInviteByName(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var targetName = body.ReadString();
			var target = await _database.GetCharacterByName(targetName, client.Session.Character.RealmId);
			await InviteCharacterToGuild(client, target, targetName);
		}

		// 0x2028 GuildLeave. The member-left fan-out packet is not decoded yet (no capture exercises it), so
		// the other members' rosters refresh on their next member-list request.
		public async Task HandleGuildLeave(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not { } guildId)
				return;

			if (!await _database.SetCharacterGuild(character.Id, null, 0))
				return;

			character.GuildId = null;
			character.GuildRank = 0;

			_logger.LogInformation("{Character} left guild {GuildId:X}.", character.Name, guildId);
			await _gameServerDispatcher.SendGuildTag(character.Id, "");
		}

		// 0x202E GuildMessageOfTheDay(i_cGuildMessageOfTheDay): persists the MOTD and pushes the targeted MOTD-change
		// packet (0x2057) to every online member so their guild window's MOTD refreshes + the toast pops.
		public async Task HandleGuildMessageOfTheDay(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not { } guildId)
				return;

			var motd = body.ReadString();
			var guild = await _database.GetGuild(guildId);
			if (guild == null)
				return;

			guild.MessageOfTheDay = motd;
			await _database.SaveGuild(guild);
			_logger.LogInformation("Guild \"{Guild}\" MOTD set by {Character} ({Length} chars).", guild.Name, character.Name, motd.Length);

			foreach (var member in guild.Members)
			{
				var memberClient = _clientForCharacter(member.Id);
				if (memberClient?.Session.Character == null)
					continue;

				await SendGuildMessageOfTheDayChanged(memberClient, PushSender, PushReceiver, motd);
			}
		}

		// Pushes guild state to a freshly authenticated guilded character: retail sends the 0x203F full update
		// right after the Authenticate ack.
		public async Task SendGuildStateOnLogin(INetworkClient client, Rpc packet)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not { } guildId)
				return;

			var guild = await _database.GetGuild(guildId);
			if (guild == null)
				return;

			await SendGuildFullUpdate(client, ReplySender(packet.Receiver), packet.Sender, guild, character);
		}

		// Fans a member-joined record out to every online member of the guild (their reply endpoints were
		// captured at Authenticate).
		private async Task BroadcastGuildMemberJoined(Guild guild, Character joined)
		{
			foreach (var member in guild.Members)
			{
				var memberClient = _clientForCharacter(member.Id);
				if (memberClient?.Session.Character == null)
					continue;

				await SendGuildMemberJoined(memberClient, PushSender, PushReceiver, joined);
			}
		}

		private async Task InviteCharacterToGuild(INetworkClient client, Character? target, string targetLabel)
		{
			var inviter = client.Session.Character;
			if (inviter?.GuildId is not { } guildId)
			{
				_logger.LogWarning("Guild invite by {Inviter}: inviter is not in a guild.", inviter?.Name);
				return;
			}

			if (target == null || target.GuildId != null)
			{
				_logger.LogWarning("Guild invite to \"{Target}\" by {Inviter}: target {Reason}.", targetLabel, inviter.Name, target == null
					? "not found"
					: "is already in a guild");
				return;
			}

			var targetClient = _clientForCharacter(target.Id);
			if (targetClient?.Session.Character == null)
			{
				_logger.LogWarning("Guild invite to \"{Target}\" by {Inviter}: target is not online on this listener.", targetLabel, inviter.Name);
				return;
			}

			var guild = await _database.GetGuild(guildId);
			if (guild == null)
				return;

			_pendingGuildInvites[target.Id] = new PendingGuildInvite(guild.Id, guild.Name, inviter.Name!);
			await SendGuildInvite(targetClient, PushSender, PushReceiver, guild, target.Id, inviter.Name!);
			_logger.LogInformation("Guild invite: {Inviter} invited {Target} to \"{Guild}\".", inviter.Name, target.Name, guild.Name);
		}

		// True when the character is connected to a world GameServer right now (drives the member record's
		// online flag; the member list has no other liveness source).
		private bool IsCharacterOnline(uint characterId)
		{
			return _gameServerDispatcher.GetWorldSessions().Any(s => s.Character?.Id == characterId);
		}

		// 0x203F full guild update - the packet that makes the client consider itself a guild member (guild
		// window, guild chat tab, MOTD popup). Retail pushes it right after the Authenticate ack on login and
		// again on join/member-list requests. The three arguments and their framing live in GuildFullUpdateCodec
		// (#530 wave 7); the renown block is built per-guild from Guild.Renown (#219), so our created guilds no
		// longer carry the captured guild's feat/city numbers.
		private async Task SendGuildFullUpdate(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, Character self)
		{
			// The roster must include EVERY member, the viewing player included - see the codec's header for why an
			// empty member map crashes the guild-management panel.
			var roster = guild.Members.Where(m => m.Name != null).Select(MemberRecord).ToList();
			var body = GuildFullUpdateCodec.Build(new GuildFullUpdate(GuildHeaders.ForFullUpdate(guild), MemberRecord(self), roster));

			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildUpdate, body));
		}

		// 0x202F member joined. Retail fans this out to every online guild member (including the joiner, right after
		// its 0x203F).
		private Task SendGuildMemberJoined(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Character member)
		{
			return client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildMemberJoined, GuildMemberJoinedCodec.Build(MemberRecord(member))));
		}

		// A stored member as a roster record, with the live-session fields (online / playfield / last-seen) resolved
		// here - the only place that knows who is connected.
		private GuildMemberRecord MemberRecord(Character member)
		{
			var online = IsCharacterOnline(member.Id);
			var lastSeen = online || member.LastConnection == null
				? 0u
				: (uint)member.LastConnection.Value.ToUnixTimeSeconds();

			return GuildMemberRecords.FromCharacter(member, online, online
				? member.MapId
				: 0, lastSeen);
		}

		// The reply SENDER endpoint for a CsPlayerAgent s2c packet: the class the client addressed, stamped with the
		// guild-server instance (see GuildServerInstance).
		public static ProtoEndpoint ReplySender(ProtoEndpoint clientTarget)
		{
			return new ProtoEndpoint
			{
				Type = clientTarget.Type,
				Instance = GuildServerInstance
			};
		}

		// 0x2044 DispatchGuildCreated - retail's IMMEDIATE create response, which opens the guild window WITH NO RELOG
		// (vs the 0x203F GuildUpdate, which only takes effect on the next login). Grammar in GuildCreatedCodec. The
		// creator is online (they just made it) and their playfield is their current map token.
		private static async Task SendGuildCreated(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, Character creator, byte form)
		{
			var body = GuildCreatedCodec.Build(new GuildCreated(GuildMemberRecords.FromCharacter(creator, true, creator.MapId, 0), GuildHeaders.ForGuildCreate(guild, form)));

			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildCreated, body));
		}

		private static Task SendGuildInvite(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, uint inviteeId, string inviterName)
		{
			var body = GuildInviteCodec.Build(new GuildInvite(guild.Id, guild.Name, inviteeId, inviterName));

			return client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildInvite, body));
		}

		// 0x2057 DispatchGuildMessageOfTheDayChanged(i_cMessageOfTheDay): retail's TARGETED MOTD-change push - updates
		// the stored MOTD + pops the "message of the day" toast, without re-sending the whole roster (unlike 0x203F).
		// No capture for it; the body is the single string arg from the client method sig, s16-framed like the other
		// guild strings (docs/re/aoc-client-rpc-methods.json). Validate in-client.
		private static async Task SendGuildMessageOfTheDayChanged(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, string motd)
		{
			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildMessageOfTheDayChanged, b => b.WriteString(motd)));
		}

		// A guild invite offered to a character, awaiting AcceptGuildInvitation/DeclineGuildInvitation. Keyed by
		// the invitee's character id; entries are dropped on accept/decline (retail also expires them - cooldown
		// handling can come later).
		private sealed record PendingGuildInvite(uint GuildId, string GuildName, string InviterName);
	}
}
