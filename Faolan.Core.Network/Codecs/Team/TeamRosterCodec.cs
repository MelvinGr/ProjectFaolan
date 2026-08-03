using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	// s2c DispatchYouJoinedTeam (CSPlayerInterface 0x200E): the WHOLE roster, sent to every member on any join.
	//   [u32 0xDEA9][u32 teamId][u32 0xC350][u32 leaderId][u32 (count+1)*0x3F1][ members ][18-byte loot settings]
	// The member list is COUNT-FRAMED (ListFrame) - the captured two-member roster's "0x00000BD3" is 3 * 0x3F1, not a
	// constant. The leader's own record comes first in every capture.
	public static class TeamRosterCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchYouJoinedTeam;

		// The roster's trailing LOOT SETTINGS block, byte-identical in all six captured rosters, and the same shape
		// the client sends in TeamSetLootOption ([u32][u8][u32]). Carried as captured until a capture varies it.
		private static readonly byte[] DefaultLootSettings = [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02];

		public static byte[] Build(TeamRoster value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Team).WriteUInt32(value.TeamId).WriteUInt32(EntityClass.Character).WriteUInt32(value.LeaderId).WriteUInt32(ListFrame.ForCount(value.Members.Count));
			foreach (var member in value.Members)
				TeamMemberRecordCodec.Write(s, member);

			return s.WriteArray(value.LootSettings).ToArray();
		}

		public static byte[] Build(uint teamId, uint leaderId, IReadOnlyList<TeamMemberRecord> members)
		{
			return Build(new TeamRoster(teamId, leaderId, members, DefaultLootSettings));
		}

		public static TeamRoster Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeamRosterCodec));
			r.ExpectU32Be(EntityClass.Team, "the team entity class");
			var teamId = r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "the leader's entity class");
			var leaderId = r.ReadU32Be();

			var count = r.ReadListCount();
			var members = new List<TeamMemberRecord>(count);
			for (var i = 0; i < count; i++)
				members.Add(TeamMemberRecordCodec.Read(ref r));

			var lootSettings = r.ReadBytes(DefaultLootSettings.Length).ToArray();
			r.EnsureAtEnd();

			return new TeamRoster(teamId, leaderId, members, lootSettings);
		}
	}

	public sealed record TeamRoster(uint TeamId, uint LeaderId, IReadOnlyList<TeamMemberRecord> Members, byte[] LootSettings);
}
