using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs;
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
		// The AoC count-framed-list multiplier (ListFrame.Unit): every such list on the wire starts with a u32 word ==
		// (count+1)*0x3F1 (the client's readers divide by it - 77 such readers in the exe). Shared by the renown
		// block's stat-lists (GuildRenownBlock), the 0x203F member list, and the 0x2038 rankings lists.
		internal const uint GuildListMultiplier = ListFrame.Unit;

		// CoreGuild_c::Read (the client deserializer, FUN_00565900) asserts streaming version == 3; the byte after
		// guildId is the GOVERNMENT FORM, whose value selects the body layout. Form 1 (which falls through into the
		// form-3 field block in the reader) is the layout GuildRenownBlock encodes, so both are fixed here. The old
		// code wrote guild.GovernmentForm into the version slot - harmless only because it defaulted to 3, but a
		// non-3 value would have tripped the client's "streaming version mismatch" assert (#219, docs/subsystems/guilds.md).
		private const byte CoreGuildStreamVersion = 0x03; // first body byte - CoreGuild_c streaming version (must be 3)
		private const byte GuildGovernmentForm = 0x01; // byte after guildId - the form selecting the case 1->3 layout

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
				await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchGuildUpdateMember,
					b => b.WriteUInt32(targetId).WriteUInt32(EntityClass.Guild).WriteUInt32(targetGuildId).WriteByte(0x03).WriteByte(0x02).WriteUInt32(targetId).WriteByte(0x01)));
				return;
			}

			var guild = await _database.GetGuild(targetGuildId);
			if (guild == null)
				return;

			// [charId][guild ID32][03][guildId][02][05][name][u32 0x3F1 = an EMPTY list][u32 0][u32 0] - verbatim from
			// the capture ("The Undreamed" reply); 0x3F1 also appears in the Authenticate 0x20D0 trailer, so it
			// is a server-wide constant rather than guild data.
			await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchGuildUpdateOther,
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

			using var reply = new FaolanStream();
			WriteGuildNameBody(reply, guildId, guild.Name);
			await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchGuildName, reply.ToArray()));
		}

		// The DispatchGuildName (0x2041) body: [u32 guildId][name s16]. Byte-exact against the capture
		// (GuildPacketTests).
		internal static void WriteGuildNameBody(FaolanStream s, uint guildId, string name)
		{
			s.WriteUInt32(guildId).WriteString(name);
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
			var form = body.Length - body.Position >= 1
				? body.ReadByte()
				: (byte)0; // guild-form (docs/subsystems/create-guild-decode.md)
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
			character.GuildRank = GuildRank.Leader;

			_logger.LogInformation("Guild \"{Name}\" ({GuildId:X}) created by {Character} (form 0x{Form:X2}).", guild.Name, guild.Id, character.Name, form);
			// Retail's immediate create response is 0x2044 DispatchGuildCreated (opens the window with NO relog), not
			// the 0x203F full-update. If the client doesn't accept it the guild still shows on the next login (the
			// re-auth 0x203F push), so this only ADDS the no-relog path.
			await SendGuildCreated(client, ReplySender(packet.Receiver), packet.Sender, guild, character, form);
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
		// again on join/member-list requests. DispatchGuildUpdate reads THREE arguments in order (FUN_013428d0),
		// each with its own "stream bad after '<arg>'" abort:
		//   i_pcGuild    = [version u8=3][guildId u32][govForm u8=1][cash u64][b u8=03][u32 0][b u8=00]
		//                  [name s16][description s16][motd s16][277-byte renown block]
		//   i_pcMember   = [self member record]                       (the authenticated member)
		//   i_cMemberList= [u32 (otherCount+1)*0x3F1][other member records]   (0x3F1-count-framed list)
		// The renown block is built per-guild from Guild.Renown (GuildRenownBlock.ForRenown), now that the 277-byte
		// block is a typed codec (#219) - our created guilds no longer carry the captured guild's feat/city numbers.
		private async Task SendGuildFullUpdate(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, Character self)
		{
			using var body = new FaolanStream();
			body
				.WriteByte(CoreGuildStreamVersion)
				.WriteUInt32(guild.Id)
				.WriteByte(GuildGovernmentForm)
				.WriteUInt64(guild.Cash)
				.WriteByte(0x03)
				.WriteUInt32(0)
				.WriteByte(0x00)
				.WriteString(guild.Name)
				.WriteString(guild.Description ?? "")
				.WriteString(guild.MessageOfTheDay ?? "")
				.WriteArray(GuildRenownBlock.ForRenown(guild.Renown).Encode());

			WriteGuildMemberRecord(body, self); // i_pcMember: the authenticated member

			// i_cMemberList: the FULL roster as a 0x3F1-framed list ([u32 (count+1)*0x3F1][records]). CoreGuild's list
			// reader (FUN_0134ed30) asserts the count-word is a multiple of 0x3F1; the old WriteUInt32(0) here (misread
			// as a "self contribution" counter) fails that check, so the client aborts DispatchGuildUpdate ("stream bad
			// after i_cMemberList") and the character shows as guildless. Retail's word for a 173-member roster was
			// 0x2ADCE = (173+1)*0x3F1.
			//
			// The list must include EVERY member, the viewing player included - it fills the client's member std::map
			// (CoreGuild+0x1e4 node ptr / +0x1e8 _Mysize count). i_pcMember above is a separate self-pointer, NOT a map
			// entry, so excluding self here left the map empty. The guild-management panel then averages renown over the
			// members (FUN_0176d4a0: sum(member renown) / map._Mysize), and a 0 count is an Integer-Divide-By-Zero crash
			// (__aulldiv) the instant the panel opens. Sending the full roster keeps the count >= 1.
			var roster = guild.Members.Where(m => m.Name != null).ToList();
			body.WriteUInt32((roster.Count + 1) * GuildListMultiplier);
			foreach (var member in roster)
				WriteGuildMemberRecord(body, member);

			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceOpcodes.DispatchGuildUpdate, body.ToArray()));
		}

		// 0x202F member joined: [member ID32][member record]. Retail fans this out to every online guild member
		// (including the joiner, right after its 0x203F).
		private Task SendGuildMemberJoined(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Character member)
		{
			using var body = new FaolanStream();
			body.WriteUInt32(EntityClass.Character).WriteUInt32(member.Id);
			WriteGuildMemberRecord(body, member);
			return client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceOpcodes.DispatchGuildMemberJoined, body.ToArray()));
		}

		private void WriteGuildMemberRecord(FaolanStream s, Character member)
		{
			var online = IsCharacterOnline(member.Id);
			var lastSeen = online || member.LastConnection == null
				? 0u
				: (uint)member.LastConnection.Value.ToUnixTimeSeconds();

			WriteGuildMemberRecord(s, member, online, online
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

		// 0x2044 DispatchGuildCreated - retail's IMMEDIATE create response, which opens the guild window WITH NO
		// RELOG (vs the 0x203F GuildUpdate, which only takes effect on the next login). Body decoded byte-exact
		// (docs/subsystems/create-guild-decode.md, GuildPacketTests): [creator member record][guild header][225-byte
		// renown block]. The header is the 0x203F head EXCEPT the post-cash byte carries the request's guild FORM
		// (0x203F hardcodes 0x03). The 225-byte tail is the SAME CoreGuild_c::Read renown block as the 0x203F, written
		// in its create-time form (GuildRenownBlock.ForGuildCreate) - no longer a verbatim blob. The creator is online
		// (they just made it); `playfield` is their current map/playfield token.
		internal static void WriteGuildCreatedBody(FaolanStream s, Guild guild, Character creator, byte form, uint playfield) // TODO: Just inline, (remove the test?)
		{
			WriteGuildMemberRecord(s, creator, true, playfield, 0);
			s
				.WriteByte(CoreGuildStreamVersion)
				.WriteUInt32(guild.Id)
				.WriteByte(GuildGovernmentForm)
				.WriteUInt64(guild.Cash)
				.WriteByte(form)
				.WriteUInt32(0)
				.WriteByte(0x00)
				.WriteString(guild.Name)
				.WriteString(guild.Description ?? "")
				.WriteString(guild.MessageOfTheDay ?? "")
				.WriteArray(GuildRenownBlock.ForGuildCreate().Encode());
		}

		// 0x202A guild invite: [guild ID32][guildName s16][invitee ID32][inviterName s16]. Shows the client's
		// accept/decline dialog. Byte-exact against the captured Kyloth->Hoxxling invite (GuildPacketTests).
		internal static void WriteGuildInviteBody(FaolanStream s, Guild guild, uint inviteeId, string inviterName) // TODO: Just inline, (remove the test?)
		{
			s.WriteUInt32(EntityClass.Guild).WriteUInt32(guild.Id).WriteString(guild.Name).WriteUInt32(EntityClass.Character).WriteUInt32(inviteeId).WriteString(inviterName);
		}

		// One member record of the roster/joined/update packets (75 bytes + name):
		//   [03 03][charId u32][name s16][rank u8][online u8][playfield u32][level u8][class u32]
		//   [unknown u32][lastSeenUnix u32][48 bytes sparse renown/pvp counters]
		// Playfield is 0 and lastSeen is the last-connection time when offline; both flip when online.
		// Byte-exact against the capture's Hoxxling 0x202F record (GuildPacketTests).
		internal static void WriteGuildMemberRecord(FaolanStream s, Character member, bool online, uint playfield, uint lastSeen) // TODO: Just inline, (remove the test?)
		{
			s
				.WriteByte(0x03)
				.WriteByte(0x03)
				.WriteUInt32(member.Id)
				.WriteString(member.Name!)
				.WriteByte(member.GuildRank!)
				.WriteByte(online)
				.WriteUInt32(playfield)
				.WriteByte(member.Level)
				.WriteUInt32(member.Class)
				.WriteUInt32(0)
				.WriteUInt32(lastSeen)
				.WriteArray(new byte[48]);
		}

		private static async Task SendGuildCreated(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, Character creator, byte form)
		{
			using var body = new FaolanStream();
			WriteGuildCreatedBody(body, guild, creator, form, creator.MapId);
			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceOpcodes.DispatchGuildCreated, body.ToArray()));
		}

		private static Task SendGuildInvite(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, uint inviteeId, string inviterName)
		{
			using var body = new FaolanStream();
			WriteGuildInviteBody(body, guild, inviteeId, inviterName);
			return client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceOpcodes.DispatchGuildInvite, body.ToArray()));
		}

		// 0x2057 DispatchGuildMessageOfTheDayChanged(i_cMessageOfTheDay): retail's TARGETED MOTD-change push - updates
		// the stored MOTD + pops the "message of the day" toast, without re-sending the whole roster (unlike 0x203F).
		// No capture for it; the body is the single string arg from the client method sig, s16-framed like the other
		// guild strings (docs/re/aoc-client-rpc-methods.json). Validate in-client.
		private static async Task SendGuildMessageOfTheDayChanged(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, string motd)
		{
			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceOpcodes.DispatchGuildMessageOfTheDayChanged, b => b.WriteString(motd)));
		}

		// A guild invite offered to a character, awaiting AcceptGuildInvitation/DeclineGuildInvitation. Keyed by
		// the invitee's character id; entries are dropped on accept/decline (retail also expires them - cooldown
		// handling can come later).
		private sealed record PendingGuildInvite(uint GuildId, string GuildName, string InviterName);
	}
}
