using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     s2c <c>DispatchTeamJoin</c> / <c>DispatchTeamLeave</c> / <c>DispatchSetNewTeamLeader</c> /
	///     <c>DispatchTeamMemberUpdate</c>: which team, and the member it happened to, as
	///     <c>[u32 teamEntityClass][u32 teamId][TeamMemberRecord]</c>.
	/// </summary>
	/// <remarks>
	///     All four share one layout, with the method carrying the verb, which is why this declares
	///     <c>HandledMethods</c> rather than naming one of them. <c>DispatchTeamMemberUpdate</c> is the incremental member
	///     refresh - the one verb of the four that changes a member in place rather than adding or removing one - and is
	///     proved byte-exact in <c>TeamMemberEventCodecTests</c>.
	///     <para>
	///         Team instances are minted sequentially by the CS tier; the capture's three groupings are 0x0051DD9A,
	///         0x0051DD9B and 0x0051DD9C.
	///     </para>
	/// </remarks>
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
