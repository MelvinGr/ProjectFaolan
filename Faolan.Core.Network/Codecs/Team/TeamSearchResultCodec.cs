using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     The <c>DispatchTeamSearchResult</c> body - the LFG "search for team" result list, the answer to
	///     <c>SearchForTeam</c>.
	/// </summary>
	/// <remarks>
	///     <code>
	///     [list frame]  { count x row }  [u8 i_bMoreHits]
	///     row = [ cTeam (TeamRosterCodec) ][ u16 len + Latin1 message ]
	///     </code>
	///     <para>
	///         The corpus cannot decode this on its own: all six captured replies are the empty body, because nobody in
	///         any traced session had flagged a team as looking for members. The grammar comes from the client's own
	///         reader. <c>CSPlayerInterfaceDispatch_t::DispatchTeamSearchResult()</c> is <c>FUN_0133de00</c> (found by its
	///         two "stream bad after 'i_cResult'/'i_bMoreHits'" assert strings); it reads the vector through
	///         <c>FUN_0134e9c0</c> and then one byte through the 1-byte primitive, so the trailing byte every captured
	///         body carries is <c>i_bMoreHits</c>, the paging flag, rather than a list trailer. <c>FUN_0134e9c0</c> is the
	///         ordinary count-framed list reader (capped at 30000 elements), and per element it calls <c>FUN_00557960</c>
	///         then <c>FUN_0048f840</c> - respectively the <c>cTeam</c> reader <see cref="TeamRosterCodec" /> was decoded
	///         from and the std::string read.
	///     </para>
	///     <para>
	///         The row's class name is the client's own, <c>TeamSearchResultRow_c</c>, from the RTTI of the GUI slot that
	///         consumes the vector (bound to <c>TeamAndGuildSearchView_c</c>, the window hosting both the team and the
	///         guild search tab).
	///     </para>
	///     <para>
	///         The message is the team's looking-for-members advert: the only per-team string in the LFG feature set, and
	///         what c2s <c>SetLookingForMemberStatus</c> / s2c <c>DispatchTeamLookingForMemberStatus</c> carry for the
	///         same team. Structural rather than observed - no captured row exists to read it off. [V0]
	///     </para>
	/// </remarks>
	public static class TeamSearchResultCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchTeamSearchResult;

		public static byte[] Build(TeamSearchResult value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Rows.Count));
			foreach (var row in value.Rows)
			{
				TeamRosterCodec.Write(s, row.Team);
				s.WriteString(row.Message);
			}

			return s.WriteByte(value.MoreHits).ToArray();
		}

		public static TeamSearchResult Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeamSearchResultCodec));
			var count = r.ReadListCount();
			var rows = new List<TeamSearchResultRow>(count);
			for (var i = 0; i < count; i++)
				rows.Add(new TeamSearchResultRow(TeamRosterCodec.Read(ref r), r.ReadStringU16Length()));

			var moreHits = r.ReadU8() != 0;
			r.EnsureAtEnd();

			return new TeamSearchResult(rows, moreHits);
		}
	}

	/// <summary>
	///     One row of the result list - the client's <c>TeamSearchResultRow_c</c>: a whole team, plus the advert its
	///     leader set.
	/// </summary>
	public sealed record TeamSearchResultRow(TeamRoster Team, string Message);

	/// <remarks>
	///     <c>MoreHits</c> is the client's <c>i_bMoreHits</c>: the request pages (<c>SearchForTeam</c> carries
	///     <c>i_nSkipResults</c>), so this says whether anything was left behind after the page that was sent. Every
	///     captured reply is the empty result with the flag clear.
	/// </remarks>
	public sealed record TeamSearchResult(IReadOnlyList<TeamSearchResultRow> Rows, bool MoreHits)
	{
		// What retail answers a search that matched nothing, byte-exact from all six captured replies.
		public static TeamSearchResult Empty { get; } = new TeamSearchResult([], false);
	}
}
