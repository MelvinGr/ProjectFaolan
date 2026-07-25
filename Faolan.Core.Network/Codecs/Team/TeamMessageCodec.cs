using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Team
{
	// The team (group) messages, all of them built from TeamMemberRecord plus a ProtoID32 or two. Decoded from
	// login_2-clients_group-trade-friend; see tools/capture/decode_team_trade_friend.py, which reads every one of
	// them to its exact end, and docs/subsystems/teams.md for the sequence.
	//
	// No `Opcode` constant: these are RPC METHODS on the CsPlayerInterface (16-bit method indices), not 0x2000 IIR
	// types, so there is nothing for CodecConventionTests' IIR rule to attribute. The group MEMBERSHIP records that
	// ride the world connection ARE IIRs and live in GroupMembershipCodec.
	public static class TeamMessageCodec
	{
		// The team's own ProtoID32 class word. Team instances are minted sequentially by the CS tier - the capture's
		// three groupings are 0x0051DD9A, 0x0051DD9B, 0x0051DD9C.
		public const uint TeamClass = 0x0000DEA9;

		// The roster's trailing LOOT SETTINGS block, byte-identical in all six captured rosters, and the same shape
		// the client sends in TeamSetLootOption ([u32][u8][u32]). Carried as captured until a capture varies it.
		private static readonly byte[] RosterLootSettings = [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02];

		// A member record from a character row. The 9-byte per-character opaque is left at ZERO: it is CS-tier state we
		// cannot derive (see gaps.md), so we send nothing rather than something invented, and an in-client check will
		// say whether the client cares.
		public static TeamMemberRecord MemberOf(Character character)
		{
			return new TeamMemberRecord
			{
				CharacterId = character.Id,
				Name = character.Name,
				Level = character.Level,
				MapId = character.MapId
			};
		}

		// s2c DispatchYouInvitedToTeam (0x200A) / DispatchYouGotKickedFromTeam (0x2022): the member alone.
		public static byte[] BuildMemberOnly(TeamMemberRecord member)
		{
			var s = new FaolanStream();
			member.Write(s);
			return s.ToArray();
		}

		// s2c DispatchTeamInvite (0x2001) and c2s RequestTeamInvite (0x2091) - the member plus a zero u16.
		public static byte[] BuildInvite(TeamMemberRecord member)
		{
			var s = new FaolanStream();
			member.Write(s);
			return s.WriteUInt16(0).ToArray();
		}

		// s2c DispatchReplyTeamInvite (0x209F) - as BuildInvite with one more zero byte.
		public static byte[] BuildReplyInvite(TeamMemberRecord member)
		{
			var s = new FaolanStream();
			member.Write(s);
			return s.WriteUInt16(0).WriteByte(0).ToArray();
		}

		// s2c DispatchTeamJoin (0x2013) / DispatchTeamLeave (0x2012) / DispatchSetNewTeamLeader (0x2016): which team,
		// and the member it happened to. All three share one layout - the OPCODE carries the verb.
		public static byte[] BuildTeamMemberEvent(uint teamId, TeamMemberRecord member)
		{
			var s = new FaolanStream().WriteUInt32(TeamClass).WriteUInt32(teamId);
			member.Write(s);
			return s.ToArray();
		}

		// s2c DispatchYouJoinedTeam (0x200E): the WHOLE roster, sent to every member on any join. The member list is
		// COUNT-FRAMED (ListFrame) - the captured two-member roster's "0x00000BD3" is 3 * 0x3F1, not a constant. The
		// leader's own record comes first in every capture.
		public static byte[] BuildRoster(uint teamId, uint leaderId, IReadOnlyList<TeamMemberRecord> members)
		{
			var s = new FaolanStream().WriteUInt32(TeamClass).WriteUInt32(teamId).WriteUInt32(TeamMemberRecord.CharacterClass).WriteUInt32(leaderId).WriteUInt32(ListFrame.ForCount(members.Count));
			foreach (var member in members)
				member.Write(s);

			return s.WriteArray(RosterLootSettings).ToArray();
		}

		// c2s TeamInvite (0x2003): who to invite, then three bytes (0, 0, 1 in every capture).
		public static uint ParseInviteTarget(FaolanStream s)
		{
			s.ReadUInt32(); // 0xC350
			return s.ReadUInt32();
		}

		// c2s AcceptTeamInvite (0x2008, CS tier) / KickFromTeam (0x2009, game tier) / the trade ProtoID32 bodies:
		// a bare [u32 class][u32 id].
		public static uint ParseCharacterId(FaolanStream s)
		{
			s.ReadUInt32(); // 0xC350
			return s.ReadUInt32();
		}
	}
}
