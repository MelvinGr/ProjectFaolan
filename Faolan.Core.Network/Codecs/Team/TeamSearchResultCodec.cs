using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	// Byte-exact codec for the 0x200F DispatchTeamSearchResult body - the LFG "search for team" result list, the answer
	// to 0x200B SearchForTeam.
	//
	//   [u32 (count+1)*0x3F1]  { count x row }  [u8 i_bMoreHits]
	//   row = [ cTeam (TeamRosterCodec) ][ u16 len + LATIN1 message ]
	//
	// ⚠️ The corpus cannot decode this on its own: all SIX captured replies are the EMPTY body `000003f1 00`
	// (Hoxxling_mage-heraldofxotli_login, addfriends x2, login-with-friends, login_2-clients_group-trade-friend and
	// the group-finder/pvp capture), because nobody in any traced session had flagged a team as looking for members.
	// The grammar above is the CLIENT'S OWN READER (#889):
	//
	//   `CSPlayerInterfaceDispatch_t::DispatchTeamSearchResult()` is `FUN_0133de00` (found by its two "stream bad
	//   after 'i_cResult'/'i_bMoreHits'" assert strings). It reads the vector through `FUN_0134e9c0` and then ONE
	//   byte through the 1-byte primitive - so the trailing byte every captured body carries is `i_bMoreHits`, the
	//   paging flag, and not a "list trailer".
	//   `FUN_0134e9c0` is the ordinary count-framed list reader (`word / 0x3F1`, capped at 0x7531 = 30000 elements),
	//   and per element it calls `FUN_00557960` then `FUN_0048f840`. Those two are already known: `FUN_00557960` is
	//   the `cTeam` reader TeamRosterCodec was decoded from (#669), and `FUN_0048f840` is the std::string read - the
	//   same call `FUN_005574d0` makes for a member's NAME.
	//
	// The row's class name is the client's own: `TeamSearchResultRow_c`, from the RTTI of the GUI slot that consumes
	// the vector (`Slot2<…, const vector<TeamSearchResultRow_c>&, bool>` bound to `TeamAndGuildSearchView_c` - the
	// window that hosts both the team and the guild search tab).
	//
	// The MESSAGE is the team's looking-for-members advert: it is the only per-team string in the LFG feature set,
	// and it is what c2s `SetLookingForMemberStatus(bool i_bLookingForMember, const std::string& i_cMessage)` /
	// s2c `DispatchTeamLookingForMemberStatus(i_bLookingForMember, i_cMessage)` carry for the same team. Structural,
	// not observed - no captured row exists to read it off. [V0]
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

	// One row of the result list - the client's `TeamSearchResultRow_c`: a whole team, plus the advert its leader set.
	public sealed record TeamSearchResultRow(TeamRoster Team, string Message);

	// `MoreHits` is the client's `i_bMoreHits`: the request pages (`SearchForTeam` carries `i_nSkipResults`), so this
	// says whether anything was left behind after the page that was sent. Every captured reply is the empty result
	// with the flag clear.
	public sealed record TeamSearchResult(IReadOnlyList<TeamSearchResultRow> Rows, bool MoreHits)
	{
		// What retail answers a search that matched nothing: `000003f1 00`, byte-exact from all six captured replies.
		public static TeamSearchResult Empty { get; } = new TeamSearchResult([], false);
	}
}
