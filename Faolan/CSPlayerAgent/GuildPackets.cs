using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// Guild wire builders + flow state for the CsPlayerAgent connection. Formats decoded from the hoxxling
	// guild_invite_accepted capture; see Captures/decode_guild.py for the byte-level grammar and validation.
	public partial class CsPlayerAgentListener
	{
		private const uint CharacterWireType = 0x0000C350;

		// A guild invite offered to a character, awaiting AcceptGuildInvitation/DeclineGuildInvitation. Keyed by
		// the invitee's character id; entries are dropped on accept/decline (retail also expires them - cooldown
		// handling can come later).
		private sealed record PendingGuildInvite(uint GuildId, string GuildName, string InviterName);

		private readonly ConcurrentDictionary<uint, PendingGuildInvite> _pendingGuildInvites = new();

		// Reply endpoints per connected client, captured from its Authenticate packet, so guild events can be
		// PUSHED to a different player's connection (invites, member-joined fan-out) with correctly addressed
		// headers - those packets are not replies, so there is no incoming packet to echo.
		private readonly ConcurrentDictionary<INetworkClient, (ProtoMessage Sender, ProtoMessage Receiver)> _replyEndpoints = new();

		protected override void ClientDisconnected(INetworkClient client)
		{
			_replyEndpoints.TryRemove(client, out _);
			base.ClientDisconnected(client);
		}

		// The 277-byte renown/feat/city-stats block of the 0x203F full update, verbatim from the capture (it was
		// the same length in all three retail snapshots, only a few counters ticked). Its internal framing is not
		// decoded yet, so a synthesized block could misalign the roster that follows it; the retail bytes are
		// known-parseable. Cosmetic-only: the renown tab shows the captured guild's numbers until it is decoded.
		private static readonly byte[] GuildRenownBlockTemplate =
		[
			0x01, 0x00, 0x00, 0x37, 0x2E, 0x00, 0x04, 0xFF, 0xB0, 0x00, 0x04, 0xFF, 0xB1, 0x00, 0x05, 0x00, 0x14, 0x00, 0x05, 0x00, 0x78, 0x00, 0x05, 0x00, 0xDC, 0x00, 0x05, 0x01, 0x40, 0x00, 0x05, 0x01, 0xA4, 0x00, 0x05,
			0x02, 0x08, 0x00, 0x05, 0x02, 0x09, 0x00, 0x05, 0x02, 0x6C, 0x00, 0x05, 0x02, 0x6D, 0x00, 0x05, 0x02, 0xD0, 0x00, 0x05, 0x03, 0x34, 0x01, 0x00, 0x00, 0x00, 0x05, 0xF3, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x08,
			0x16, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F, 0xC4, 0x00, 0x00, 0x04, 0x10, 0x00, 0x00, 0x08, 0x16, 0x00, 0x00, 0x0B, 0xEA, 0x00, 0x00, 0x00, 0x39, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x54, 0x00, 0x00, 0x00, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0xB4, 0x83, 0x00, 0x00, 0x00, 0x00, 0x01, 0x32, 0x7B, 0xAD, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x12, 0xB2, 0x8F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xB0, 0x81, 0x00, 0x00, 0x00, 0x00, 0x00, 0xD4, 0x0A, 0xCB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x26, 0x74, 0x00, 0x00, 0x00, 0x03, 0x01, 0xC9,
			0xC3, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x7F, 0x2C,
			0xBD, 0x00, 0x00, 0x00, 0x00, 0x02, 0x85, 0x3C, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x01, 0x90, 0x0C, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3A, 0x1D, 0x51, 0xC0
		];

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
				.WriteArray(GuildRenownBlockTemplate);

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
				var memberClient = ClientForCharacter(member.Id);
				if (memberClient == null || !_replyEndpoints.TryGetValue(memberClient, out var endpoints))
					continue;

				await SendGuildMemberJoined(memberClient, endpoints.Sender, endpoints.Receiver, joined);
			}
		}

		// Pushes guild state to a freshly authenticated guilded character: retail sends the 0x203F full update
		// right after the Authenticate ack.
		private async Task SendGuildStateOnLogin(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not uint guildId)
				return;

			var guild = await Database.GetGuild(guildId);
			if (guild == null)
				return;

			await SendGuildFullUpdate(client, packet.ReceiverBytes, packet.SenderBytes, guild, character);
		}
	}
}
