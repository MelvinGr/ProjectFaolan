using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// Guild RPC handlers + wire builders + flow state for the CsPlayerAgent connection (create / invite / accept /
	// decline / leave / MOTD / info). Formats decoded from the hoxxling guild_invite_accepted capture (see
	// Captures/decode_guild.py). Extracted from CsPlayerAgentListener as a composed collaborator: it owns the
	// pending-invite + per-client reply-endpoint state and routes guild events to other connected members. The
	// cross-connection client lookup (ClientForCharacter, on the Server base) is injected as a delegate seam; the
	// in-world guild-tag push rides the existing IGameServerDispatcher.
	public sealed class GuildService
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly IGameServerDispatcher _gameServerDispatcher;
		private readonly Func<uint, INetworkClient> _clientForCharacter;

		public GuildService(IDatabaseRepository database, ILogger logger, IGameServerDispatcher gameServerDispatcher, Func<uint, INetworkClient> clientForCharacter)
		{
			_database = database;
			_logger = logger;
			_gameServerDispatcher = gameServerDispatcher;
			_clientForCharacter = clientForCharacter;
		}

		private const uint CharacterWireType = EntityClass.Character;

		// A guild invite offered to a character, awaiting AcceptGuildInvitation/DeclineGuildInvitation. Keyed by
		// the invitee's character id; entries are dropped on accept/decline (retail also expires them - cooldown
		// handling can come later).
		private sealed record PendingGuildInvite(uint GuildId, string GuildName, string InviterName);

		private readonly ConcurrentDictionary<uint, PendingGuildInvite> _pendingGuildInvites = new();

		// Reply endpoints per connected client, captured from its Authenticate packet, so guild events can be
		// PUSHED to a different player's connection (invites, member-joined fan-out) with correctly addressed
		// headers - those packets are not replies, so there is no incoming packet to echo.
		private readonly ConcurrentDictionary<INetworkClient, (ProtoMessage Sender, ProtoMessage Receiver)> _replyEndpoints = new();

		// Remember a connected client's reply endpoints (captured from its Authenticate packet) so guild pushes can
		// be addressed to it later. Called by the listener on auth.
		public void RememberReplyEndpoints(INetworkClient client, ProtoMessage sender, ProtoMessage receiver)
		{
			_replyEndpoints[client] = (sender, receiver);
		}

		// Drop a disconnected client's remembered endpoints. Called by the listener's ClientDisconnected override.
		public void OnClientDisconnected(INetworkClient client)
		{
			_replyEndpoints.TryRemove(client, out _);
		}

		// 0x201D DeclineGuildInvitation: drop the pending invite (logs the guild it was for). Returns the declined
		// invite's guild name (or null when there was none) so the listener can log it.
		public string DeclineGuildInvitation(uint characterId)
		{
			return _pendingGuildInvites.TryRemove(characterId, out var declined) ? declined.GuildName : null;
		}

		// True when the character is connected to a world GameServer right now (drives the member record's
		// online flag; the member list has no other liveness source).
		private bool IsCharacterOnline(uint characterId)
		{
			return _gameServerDispatcher
				.GetSessions()
				.Any(s => s.CharacterId == characterId || s.ClientInstance == characterId);
		}

		// One member record of the roster/joined/update packets (75 bytes + name):
		//   [03 03][charId u32][name s16][rank u8][online u8][playfield u32][level u8][class u32]
		//   [unknown u32][lastSeenUnix u32][48 bytes sparse renown/pvp counters]
		// Playfield is 0 and lastSeen is the last-connection time when offline; both flip when online.
		// Byte-exact against the capture's Hoxxling 0x202F record (GuildPacketTests).
		internal static void WriteGuildMemberRecord(FaolanStream s, Character member, bool online, uint playfield, uint lastSeen)
		{
			s
				.WriteByte(0x03)
				.WriteByte(0x03)
				.WriteUInt32(member.Id)
				.WriteString(member.Name ?? "")
				.WriteByte(member.GuildRank)
				.WriteByte(online)
				.WriteUInt32(playfield)
				.WriteByte((byte)member.Level)
				.WriteUInt32((byte)member.Class)
				.WriteUInt32(0)
				.WriteUInt32(lastSeen)
				.WriteArray(new byte[48]);
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

		// 0x203F full guild update - the packet that makes the client consider itself a guild member (guild
		// window, guild chat tab, MOTD popup). Retail pushes it right after the Authenticate ack on login and
		// again on join/member-list requests:
		//   [govForm u8][guildId u32][01][cash u64][03][u32 0][00][name s16][description s16][motd s16]
		//   [renown block][self member record][selfContribution u32][other member records ... to packet end]
		// The renown block is built per-guild from Guild.Renown (GuildRenownBlock.ForRenown), now that the 277-byte
		// block is a typed codec (#219) - our created guilds no longer carry the captured guild's feat/city numbers.
		private async Task SendGuildFullUpdate(INetworkClient client, ProtoMessage sender, ProtoMessage receiver, Guild guild, Character self)
		{
			var ps = new PacketStream();
			ps
				.WriteHeader(sender, receiver, CsPlayerAgentResponseOpcodes.GuildFullUpdate)
				.WriteByte(guild.GovernmentForm)
				.WriteUInt32(guild.Id)
				.WriteByte(0x01)
				.WriteUInt64(guild.Cash)
				.WriteByte(0x03)
				.WriteUInt32(0)
				.WriteByte(0x00)
				.WriteString(guild.Name)
				.WriteString(guild.Description ?? "")
				.WriteString(guild.MessageOfTheDay ?? "")
				.WriteArray(GuildRenownBlock.ForRenown(guild.Renown).Encode());

			WriteGuildMemberRecord(ps, self);
			ps.WriteUInt32(0); // self contribution counter (grows with renown gain in the capture)

			foreach (var member in guild.Members.Where(m => m.Id != self.Id && m.Name != null))
				WriteGuildMemberRecord(ps, member);

			await ps.SendAsync(client);
		}

		// 0x202A guild invite: [guild ID32][guildName s16][invitee ID32][inviterName s16]. Shows the client's
		// accept/decline dialog. Byte-exact against the captured Kyloth->Hoxxling invite (GuildPacketTests).
		internal static void WriteGuildInviteBody(FaolanStream s, Guild guild, uint inviteeId, string inviterName)
		{
			s
				.WriteUInt32(Guild.WireType)
				.WriteUInt32(guild.Id)
				.WriteString(guild.Name)
				.WriteUInt32(CharacterWireType)
				.WriteUInt32(inviteeId)
				.WriteString(inviterName);
		}

		private static Task SendGuildInvite(INetworkClient client, ProtoMessage sender, ProtoMessage receiver, Guild guild, uint inviteeId, string inviterName)
		{
			var ps = new PacketStream();
			ps.WriteHeader(sender, receiver, CsPlayerAgentResponseOpcodes.GuildInvite);
			WriteGuildInviteBody(ps, guild, inviteeId, inviterName);
			return ps.SendAsync(client);
		}

		// 0x202F member joined: [member ID32][member record]. Retail fans this out to every online guild member
		// (including the joiner, right after its 0x203F).
		private Task SendGuildMemberJoined(INetworkClient client, ProtoMessage sender, ProtoMessage receiver, Character member)
		{
			var ps = new PacketStream();
			ps
				.WriteHeader(sender, receiver, CsPlayerAgentResponseOpcodes.GuildMemberJoined)
				.WriteUInt32(CharacterWireType)
				.WriteUInt32(member.Id);
			WriteGuildMemberRecord(ps, member);
			return ps.SendAsync(client);
		}

		// Fans a member-joined record out to every online member of the guild (their reply endpoints were
		// captured at Authenticate).
		private async Task BroadcastGuildMemberJoined(Guild guild, Character joined)
		{
			foreach (var member in guild.Members)
			{
				var memberClient = _clientForCharacter(member.Id);
				if (memberClient == null || !_replyEndpoints.TryGetValue(memberClient, out var endpoints))
					continue;

				await SendGuildMemberJoined(memberClient, endpoints.Sender, endpoints.Receiver, joined);
			}
		}

		// Pushes guild state to a freshly authenticated guilded character: retail sends the 0x203F full update
		// right after the Authenticate ack.
		public async Task SendGuildStateOnLogin(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not uint guildId)
				return;

			var guild = await _database.GetGuild(guildId);
			if (guild == null)
				return;

			await SendGuildFullUpdate(client, packet.ReceiverBytes, packet.SenderBytes, guild, character);
		}
	
		// 0x2021 CreateGuild(i_cGuildName). No retail capture of the upload exists; the recovered signature is a
		// single string, read as the standard u16-length string.
		public async Task HandleCreateGuild(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var name = packet.Body.ReadString();
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
			character.GuildRank = Character.GuildLeaderRank;

			_logger.LogInformation("Guild \"{Name}\" ({GuildId:X}) created by {Character}.", guild.Name, guild.Id, character.Name);
			await SendGuildFullUpdate(client, packet.ReceiverBytes, packet.SenderBytes, guild, character);
			await _gameServerDispatcher.SendGuildTag(character.Id, guild.Name);
		}

		// 0x2022 GuildInviteByName(i_cCharacterName): inviter must be guilded; the target must be online (the
		// invite dialog is a live push - retail also rejects offline targets).
		public async Task HandleGuildInviteByName(INetworkClient client, ConanPacket packet)
		{
			var targetName = packet.Body.ReadString();
			var target = await _database.GetCharacterByName(targetName);
			await InviteCharacterToGuild(client, target, targetName);
		}

		// 0x2023 GuildInvite: same as by-name but the target is identified by ID32.
		public async Task HandleGuildInvite(INetworkClient client, ConanPacket packet)
		{
			packet.Body.ReadUInt32(); // ID32 type (0x0000C350)
			var targetId = packet.Body.ReadUInt32();
			var target = await _database.GetCharacter(targetId);
			await InviteCharacterToGuild(client, target, $"{targetId:X8}");
		}

		private async Task InviteCharacterToGuild(INetworkClient client, Character target, string targetLabel)
		{
			var inviter = client.Session.Character;
			if (inviter?.GuildId is not uint guildId)
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
			if (targetClient == null || !_replyEndpoints.TryGetValue(targetClient, out var endpoints))
			{
				_logger.LogWarning("Guild invite to \"{Target}\" by {Inviter}: target is not online on this listener.", targetLabel, inviter.Name);
				return;
			}

			var guild = await _database.GetGuild(guildId);
			if (guild == null)
				return;

			_pendingGuildInvites[target.Id] = new PendingGuildInvite(guild.Id, guild.Name, inviter.Name);
			await SendGuildInvite(targetClient, endpoints.Sender, endpoints.Receiver, guild, target.Id, inviter.Name);
			_logger.LogInformation("Guild invite: {Inviter} invited {Target} to \"{Guild}\".", inviter.Name, target.Name, guild.Name);
		}

		// 0x201C AcceptGuildInvitation(i_cGuildID): body is the guild ID32 from the 0x202A invite. Retail replies
		// with the 0x203F full update followed by the joiner's own 0x202F member-joined record (which also fans
		// out to the other online members), and sets the in-world tag with a GameServer 0x2550C99A.
		public async Task HandleAcceptGuildInvitation(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			packet.Body.ReadUInt32(); // ID32 type (0x0000DEB7)
			var guildId = packet.Body.ReadUInt32();

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
			await SendGuildFullUpdate(client, packet.ReceiverBytes, packet.SenderBytes, guild, character);
			await BroadcastGuildMemberJoined(guild, character); // includes the joiner, matching retail order
			await _gameServerDispatcher.SendGuildTag(character.Id, guild.Name);
		}

		// 0x2028 GuildLeave. The member-left fan-out packet is not decoded yet (no capture exercises it), so
		// the other members' rosters refresh on their next member-list request.
		public async Task HandleGuildLeave(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not uint guildId)
				return;

			if (!await _database.SetCharacterGuild(character.Id, null, 0))
				return;

			character.GuildId = null;
			character.GuildRank = 0;

			_logger.LogInformation("{Character} left guild {GuildId:X}.", character.Name, guildId);
			await _gameServerDispatcher.SendGuildTag(character.Id, "");
		}

		// 0x202E GuildMessageOfTheDay(i_cGuildMessageOfTheDay): persists the MOTD and re-pushes the full update
		// to every online member so their guild window refreshes.
		public async Task HandleGuildMessageOfTheDay(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not uint guildId)
				return;

			var motd = packet.Body.ReadString();
			var guild = await _database.GetGuild(guildId);
			if (guild == null)
				return;

			guild.MessageOfTheDay = motd;
			await _database.SaveGuild(guild);
			_logger.LogInformation("Guild \"{Guild}\" MOTD set by {Character} ({Length} chars).", guild.Name, character.Name, motd.Length);

			foreach (var member in guild.Members)
			{
				var memberClient = _clientForCharacter(member.Id);
				if (memberClient?.Session?.Character == null || !_replyEndpoints.TryGetValue(memberClient, out var endpoints))
					continue;

				await SendGuildFullUpdate(memberClient, endpoints.Sender, endpoints.Receiver, guild, memberClient.Session.Character);
			}
		}

		// 0x201E ClientRequestGuildInfo([ID32 char]): the client resolves guild banners/tooltips of other
		// players with this. Reply is 0x2042 when the target is in the requester's own guild, 0x2043 (carrying
		// the guild name) when it is in another guild, and nothing when the target is guildless.
		public async Task HandleClientRequestGuildInfo(INetworkClient client, ConanPacket packet)
		{
			packet.Body.ReadUInt32(); // ID32 type (0x0000C350)
			var targetId = packet.Body.ReadUInt32();

			var target = await _database.GetCharacter(targetId);
			if (target?.GuildId is not uint targetGuildId)
				return;

			if (client.Session.Character?.GuildId == targetGuildId)
			{
				// [charId][guild ID32][03][02][charId][01] - the constants are not decoded (likely rank/flags);
				// taken verbatim from the capture (Dahviie, same-guild reply).
				await new PacketStream()
					.WriteHeader(packet.ReceiverBytes, packet.SenderBytes, CsPlayerAgentResponseOpcodes.GuildInfoSameGuild)
					.WriteUInt32(targetId)
					.WriteUInt32(Guild.WireType)
					.WriteUInt32(targetGuildId)
					.WriteByte(0x03)
					.WriteByte(0x02)
					.WriteUInt32(targetId)
					.WriteByte(0x01)
					.SendAsync(client);
				return;
			}

			var guild = await _database.GetGuild(targetGuildId);
			if (guild == null)
				return;

			// [charId][guild ID32][03][guildId][02][05][name][u32 0x3F1][u32 0][u32 0] - constants verbatim from
			// the capture ("The Undreamed" reply); 0x3F1 also appears in the Authenticate 0x20D0 trailer, so it
			// is a server-wide constant rather than guild data.
			await new PacketStream()
				.WriteHeader(packet.ReceiverBytes, packet.SenderBytes, CsPlayerAgentResponseOpcodes.GuildInfoOtherGuild)
				.WriteUInt32(targetId)
				.WriteUInt32(Guild.WireType)
				.WriteUInt32(targetGuildId)
				.WriteByte(0x03)
				.WriteUInt32(targetGuildId)
				.WriteByte(0x02)
				.WriteByte(0x05)
				.WriteString(guild.Name)
				.WriteUInt32(0x3F1)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.SendAsync(client);
		}
	
}
}
