using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     The client's <c>cTeam</c> - a whole team: who it is, who leads it, everyone in it, and how it splits loot.
	/// </summary>
	/// <remarks>
	///     It is the body of three methods, which is why it declares <c>HandledMethods</c>: s2c
	///     <c>DispatchYouJoinedTeam</c> (capture-proven) and the two raid ones, <c>DispatchRaidTeamJoined</c> and
	///     <c>DispatchRaidTeamUpdate</c>. All three read through the client's <c>FUN_00557960</c> - a raid is the team
	///     system one level up, so a raid announcing one of its member teams sends the same record a player gets on
	///     joining a team.
	///     <code>
	///     [ID32 team][ID32 leader][list frame][ members ][ TeamSettings ]
	///     </code>
	///     <para>
	///         The member list is count-framed through <c>ListFrame</c>; the word a two-member roster carries is that
	///         count, not a constant. The leader's own record comes first in every capture.
	///     </para>
	/// </remarks>
	public static class TeamRosterCodec
	{
		public static readonly CSPlayerInterfaceMethods[] HandledMethods =
			[CSPlayerInterfaceMethods.DispatchYouJoinedTeam, CSPlayerInterfaceMethods.DispatchRaidTeamJoined, CSPlayerInterfaceMethods.DispatchRaidTeamUpdate];

		public static byte[] Build(TeamRoster value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint teamId, uint leaderId, IReadOnlyList<TeamMemberRecord> members)
		{
			return Build(new TeamRoster(teamId, leaderId, members, TeamSettings.Default));
		}

		public static TeamRoster Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeamRosterCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TeamRoster Read(ref SpanReader r)
		{
			r.ExpectU32Be(EntityClass.Team, "the team entity class");
			var teamId = r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "the leader's entity class");
			var leaderId = r.ReadU32Be();

			var count = r.ReadListCount();
			var members = new List<TeamMemberRecord>(count);
			for (var i = 0; i < count; i++)
				members.Add(TeamMemberRecordCodec.Read(ref r));

			return new TeamRoster(teamId, leaderId, members, ReadSettings(ref r));
		}

		internal static void Write(FaolanStream s, TeamRoster v)
		{
			s.WriteUInt32(EntityClass.Team).WriteUInt32(v.TeamId).WriteUInt32(EntityClass.Character).WriteUInt32(v.LeaderId).WriteUInt32(ListFrame.ForCount(v.Members.Count));
			foreach (var member in v.Members)
				TeamMemberRecordCodec.Write(s, member);

			WriteSettings(s, v.Settings);
		}

		private static TeamSettings ReadSettings(ref SpanReader r)
		{
			var lootMode = r.ReadU32Be();
			var useNeedGreed = r.ReadU8() != 0;
			var flag = r.ReadU8() != 0;
			var masterLooter = EntityIdCodec.Read(ref r);

			return new TeamSettings(lootMode, useNeedGreed, flag, masterLooter, r.ReadU32Be());
		}

		private static void WriteSettings(FaolanStream s, TeamSettings v)
		{
			s.WriteUInt32(v.LootMode).WriteByte(v.UseNeedGreed).WriteByte(v.Flag);
			EntityIdCodec.Write(s, v.MasterLooter);
			s.WriteUInt32(v.LootThreshold);
		}
	}

	/// <summary>The 18 bytes that close a <c>cTeam</c>.</summary>
	/// <remarks>
	///     The client's reader splits them, and the split is corroborated from the other end: the first, second and last
	///     field are exactly <c>TeamSetLootOption(int i_eLootMode, bool i_bUseNeedGreed, int i_nNewLootThreshold)</c>,
	///     whose captured c2s body is <c>00000000 01 00000002</c> - the same 0 / true / 2 this block carries.
	///     <para>
	///         <c>MasterLooter</c> is read with the ID32 idiom (a class word then an instance word, the same pair shape
	///         the team and leader ids at the head of the record use), and <c>TeamSetMasterLooter(const ID32&amp;
	///         i_cCharacterID)</c> / <c>DispatchTeamMasterLooterChanged</c> exist for exactly this slot. It is (0, 0) in
	///         every captured roster - master looter unset - so the reading is structural rather than observed. [V0]
	///     </para>
	///     <para><c>Flag</c> is a second bool beside <c>UseNeedGreed</c>, zero in every capture and with no c2s
	///     counterpart. [V0]</para>
	/// </remarks>
	public sealed record TeamSettings(uint LootMode, bool UseNeedGreed, bool Flag, EntityId MasterLooter, uint LootThreshold)
	{
		// What every captured roster carries: free-for-all, need/greed on, no master looter, threshold 2.
		public static TeamSettings Default { get; } = new TeamSettings(0, true, false, new EntityId(0, 0), 2);
	}

	public sealed record TeamRoster(uint TeamId, uint LeaderId, IReadOnlyList<TeamMemberRecord> Members, TeamSettings Settings);
}
