using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// Guild RPC handlers (CsPlayerAgent client->server methods). Wire formats: Captures/decode_guild.py.
	public partial class CsPlayerAgentListener
	{
		// 0x2021 CreateGuild(i_cGuildName). No retail capture of the upload exists; the recovered signature is a
		// single string, read as the standard u16-length string.
		private async Task HandleCreateGuild(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var name = packet.Body.ReadString();
			if (string.IsNullOrWhiteSpace(name) || character.GuildId != null)
			{
				Logger.LogWarning("CreateGuild rejected for {CharacterId:X8}: name \"{Name}\", already in guild: {InGuild}.", character.Id, name, character.GuildId != null);
				return;
			}

			var guild = await Database.CreateGuild(name, character.Id);
			if (guild == null)
			{
				// Retail answers with DispatchGuildBadName/-NonAlphanumericName here; those opcodes are not
				// decoded yet, so the failure is server-side only.
				Logger.LogWarning("CreateGuild failed for {CharacterId:X8}: guild name \"{Name}\" is taken.", character.Id, name);
				return;
			}

			character.GuildId = guild.Id;
			character.GuildRank = Character.GuildLeaderRank;

			Logger.LogInformation("Guild \"{Name}\" ({GuildId:X}) created by {Character}.", guild.Name, guild.Id, character.Name);
			await SendGuildFullUpdate(client, packet.ReceiverBytes, packet.SenderBytes, guild, character);
			await _gameServerDispatcher.SendGuildTag(character.Id, guild.Name);
		}

		// 0x2022 GuildInviteByName(i_cCharacterName): inviter must be guilded; the target must be online (the
		// invite dialog is a live push - retail also rejects offline targets).
		private async Task HandleGuildInviteByName(INetworkClient client, ConanPacket packet)
		{
			var targetName = packet.Body.ReadString();
			var target = await Database.GetCharacterByName(targetName);
			await InviteCharacterToGuild(client, target, targetName);
		}

		// 0x2023 GuildInvite: same as by-name but the target is identified by ID32.
		private async Task HandleGuildInvite(INetworkClient client, ConanPacket packet)
		{
			packet.Body.ReadUInt32(); // ID32 type (0x0000C350)
			var targetId = packet.Body.ReadUInt32();
			var target = await Database.GetCharacter(targetId);
			await InviteCharacterToGuild(client, target, $"{targetId:X8}");
		}

		private async Task InviteCharacterToGuild(INetworkClient client, Character target, string targetLabel)
		{
			var inviter = client.Session.Character;
			if (inviter?.GuildId is not uint guildId)
			{
				Logger.LogWarning("Guild invite by {Inviter}: inviter is not in a guild.", inviter?.Name);
				return;
			}

			if (target == null || target.GuildId != null)
			{
				Logger.LogWarning("Guild invite to \"{Target}\" by {Inviter}: target {Reason}.", targetLabel, inviter.Name, target == null
					? "not found"
					: "is already in a guild");
				return;
			}

			var targetClient = ClientForCharacter(target.Id);
			if (targetClient == null || !_replyEndpoints.TryGetValue(targetClient, out var endpoints))
			{
				Logger.LogWarning("Guild invite to \"{Target}\" by {Inviter}: target is not online on this listener.", targetLabel, inviter.Name);
				return;
			}

			var guild = await Database.GetGuild(guildId);
			if (guild == null)
				return;

			_pendingGuildInvites[target.Id] = new PendingGuildInvite(guild.Id, guild.Name, inviter.Name);
			await SendGuildInvite(targetClient, endpoints.Sender, endpoints.Receiver, guild, target.Id, inviter.Name);
			Logger.LogInformation("Guild invite: {Inviter} invited {Target} to \"{Guild}\".", inviter.Name, target.Name, guild.Name);
		}

		// 0x201C AcceptGuildInvitation(i_cGuildID): body is the guild ID32 from the 0x202A invite. Retail replies
		// with the 0x203F full update followed by the joiner's own 0x202F member-joined record (which also fans
		// out to the other online members), and sets the in-world tag with a GameServer 0x2550C99A.
		private async Task HandleAcceptGuildInvitation(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			packet.Body.ReadUInt32(); // ID32 type (0x0000DEB7)
			var guildId = packet.Body.ReadUInt32();

			if (!_pendingGuildInvites.TryRemove(character.Id, out var invite) || invite.GuildId != guildId)
			{
				Logger.LogWarning("AcceptGuildInvitation from {CharacterId:X8} for guild {GuildId:X} without a matching pending invite.", character.Id, guildId);
				return;
			}

			if (!await Database.SetCharacterGuild(character.Id, guildId, 0))
				return;

			character.GuildId = guildId;
			character.GuildRank = 0;

			var guild = await Database.GetGuild(guildId);
			if (guild == null)
				return;

			Logger.LogInformation("{Character} joined guild \"{Guild}\" (invited by {Inviter}).", character.Name, guild.Name, invite.InviterName);
			await SendGuildFullUpdate(client, packet.ReceiverBytes, packet.SenderBytes, guild, character);
			await BroadcastGuildMemberJoined(guild, character); // includes the joiner, matching retail order
			await _gameServerDispatcher.SendGuildTag(character.Id, guild.Name);
		}

		// 0x2028 GuildLeave. The member-left fan-out packet is not decoded yet (no capture exercises it), so
		// the other members' rosters refresh on their next member-list request.
		private async Task HandleGuildLeave(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not uint guildId)
				return;

			if (!await Database.SetCharacterGuild(character.Id, null, 0))
				return;

			character.GuildId = null;
			character.GuildRank = 0;

			Logger.LogInformation("{Character} left guild {GuildId:X}.", character.Name, guildId);
			await _gameServerDispatcher.SendGuildTag(character.Id, "");
		}

		// 0x202E GuildMessageOfTheDay(i_cGuildMessageOfTheDay): persists the MOTD and re-pushes the full update
		// to every online member so their guild window refreshes.
		private async Task HandleGuildMessageOfTheDay(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not uint guildId)
				return;

			var motd = packet.Body.ReadString();
			var guild = await Database.GetGuild(guildId);
			if (guild == null)
				return;

			guild.MessageOfTheDay = motd;
			await Database.SaveGuild(guild);
			Logger.LogInformation("Guild \"{Guild}\" MOTD set by {Character} ({Length} chars).", guild.Name, character.Name, motd.Length);

			foreach (var member in guild.Members)
			{
				var memberClient = ClientForCharacter(member.Id);
				if (memberClient?.Session?.Character == null || !_replyEndpoints.TryGetValue(memberClient, out var endpoints))
					continue;

				await SendGuildFullUpdate(memberClient, endpoints.Sender, endpoints.Receiver, guild, memberClient.Session.Character);
			}
		}

		// 0x201E ClientRequestGuildInfo([ID32 char]): the client resolves guild banners/tooltips of other
		// players with this. Reply is 0x2042 when the target is in the requester's own guild, 0x2043 (carrying
		// the guild name) when it is in another guild, and nothing when the target is guildless.
		private async Task HandleClientRequestGuildInfo(INetworkClient client, ConanPacket packet)
		{
			packet.Body.ReadUInt32(); // ID32 type (0x0000C350)
			var targetId = packet.Body.ReadUInt32();

			var target = await Database.GetCharacter(targetId);
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

			var guild = await Database.GetGuild(targetGuildId);
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
