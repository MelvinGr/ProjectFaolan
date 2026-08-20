using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	// s2c DispatchTeamJoin (0x2013) / DispatchTeamLeave (0x2012) / DispatchSetNewTeamLeader (0x2016) /
	// DispatchTeamMemberUpdate (0x2014): which team, and the member it happened to.
	// [u32 0xDEA9][u32 teamId][TeamMemberRecord]. All FOUR share one layout - the OPCODE carries the verb, which is
	// why this declares HandledMethods rather than picking one of them.
	//
	// `DispatchTeamMemberUpdate` joined them in #874: it was on the never-sent list with a 106-byte body nobody had
	// looked at, and the body turned out to be this one exactly (proved byte-exact in TeamMemberEventCodecTests). It
	// is the INCREMENTAL member refresh - the one verb of the four that changes a member in place rather than adding
	// or removing one - and teams currently have no incremental refresh path at all.
	//
	// Team instances are minted sequentially by the CS tier - the capture's three groupings are 0x0051DD9A,
	// 0x0051DD9B, 0x0051DD9C.
	public static class TeamMemberEventCodec
	{
		public static readonly CSPlayerInterfaceMethods[] HandledMethods =
		[
			CSPlayerInterfaceMethods.DispatchTeamJoin, CSPlayerInterfaceMethods.DispatchTeamLeave,
			CSPlayerInterfaceMethods.DispatchSetNewTeamLeader, CSPlayerInterfaceMethods.DispatchTeamMemberUpdate
		];

		public static byte[] Build(TeamMemberEvent value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Team).WriteUInt32(value.TeamId);
			TeamMemberRecordCodec.Write(s, value.Member);

			return s.ToArray();
		}

		public static byte[] Build(uint teamId, TeamMemberRecord member)
		{
			return Build(new TeamMemberEvent(teamId, member));
		}

		public static TeamMemberEvent Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeamMemberEventCodec));
			r.ExpectU32Be(EntityClass.Team, "the team entity class");
			var teamId = r.ReadU32Be();
			var member = TeamMemberRecordCodec.Read(ref r);
			r.EnsureAtEnd();

			return new TeamMemberEvent(teamId, member);
		}
	}

	public sealed record TeamMemberEvent(uint TeamId, TeamMemberRecord Member);
}
