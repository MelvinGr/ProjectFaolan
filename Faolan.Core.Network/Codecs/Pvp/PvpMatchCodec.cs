using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Pvp
{
	// The `i_cMatch` payload - a whole custom PvP match - which is the entire body of BOTH s2c methods that carry
	// one: DispatchPvPMatchMakingYouJoinedMatch (0x207B, sent to a player entering the match, the creator included -
	// the client's copier raises its own YouCreatedMatch fifo when the tail's leader ID32 is the local player) and
	// DispatchPvPMatchMakingMatchInfo (0x207D, the answer to PvPMatchMakingGetMatchInfo).
	//
	// ⚠️ ZERO captured instances - no captured queue ever filled and no capture creates a custom match. The layout
	// is nevertheless pinned, because the client holds all three halves of it and they agree: the s2c readers
	// (`FUN_01346ef0` / `FUN_013470d0`), the c2s writer (`FUN_00558880`, PvPMatchMakingCreateMatch), and the local
	// copy (`FUN_00547070`, which stores the joined match and whose stride/tail checks are the carve's proof).
	// docs/re/minigame-massivepvp-messages.md is the decode; Gitea #146 is the ticket.
	//
	//   [ListFrame(n)] { n x cCharacter }     the member roster - the SAME record the team subsystem carries
	//                                         (TeamMemberRecordCodec, client reader FUN_005574d0, #669)
	//   [ListFrame(m)] { m x u32 }            an id list; the client stores it unnamed (reader FUN_004d4ef0)
	//   PvPMatchRules_c                       PvpMatchRulesCodec
	//   [u32 matchId]                         the debug view's ID column and its "Match %d created." line
	//   if (!rules.Silent) [ID32 leader]      the match leader's two-word ID32
	//
	// ⭐ The tail pair is the LEADER's ID32, not "two unnamed words": the copier's fifo chooser (00547117..2f)
	// compares the stored pair against the LOCAL PLAYER's ID32 and raises `PvPMatchMakingYouCreatedMatch` on a
	// match, `PvPMatchMakingYouJoinedMatch` otherwise - which also says the announce path is exactly "is this my
	// match", and why a silent match does not carry it.
	public static class PvpMatchCodec
	{
		public static readonly CSPlayerInterfaceMethods[] HandledMethods =
		[
			CSPlayerInterfaceMethods.DispatchPvPMatchMakingYouJoinedMatch, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchInfo
		];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(PvpMatch value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Roster.Count));
			foreach (var member in value.Roster)
				TeamMemberRecordCodec.Write(s, member);

			s.WriteUInt32(ListFrame.ForCount(value.IdList.Count));
			foreach (var id in value.IdList)
				s.WriteUInt32(id);

			PvpMatchRulesCodec.Write(s, value.Rules);
			s.WriteUInt32(value.MatchId);

			// The leader ID32 rides only a NON-silent match - the client's reader and copier both gate on the same
			// rules byte, so a silent match's body simply ends at the id.
			if (!value.Rules.Silent)
				s.WriteUInt32(EntityClass.Character).WriteUInt32(value.LeaderId);

			return s.ToArray();
		}

		public static PvpMatch Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PvpMatchCodec));

			var rosterCount = r.ReadListCount();
			var roster = new List<TeamMemberRecord>(rosterCount);
			for (var i = 0; i < rosterCount; i++)
				roster.Add(TeamMemberRecordCodec.Read(ref r));

			var idCount = r.ReadListCount();
			var ids = new List<uint>(idCount);
			for (var i = 0; i < idCount; i++)
				ids.Add(r.ReadU32Be());

			var rules = PvpMatchRulesCodec.Read(ref r);
			var matchId = r.ReadU32Be();

			var value = new PvpMatch(roster, ids, rules, matchId);
			if (!rules.Silent)
			{
				r.ExpectU32Be(EntityClass.Character, "the leader's entity class");
				value = value with { LeaderId = r.ReadU32Be() };
			}

			r.EnsureAtEnd();
			return value;
		}
	}

	/// <summary>A whole custom match: its roster, ruleset and id.</summary>
	/// <param name="Roster">The members. The leader is named by the tail ID32, not by roster position.</param>
	/// <param name="IdList">The unnamed u32 list between roster and rules; the client stores it without a consumer found.</param>
	/// <param name="Rules">The ruleset the match was created with.</param>
	/// <param name="MatchId">The match's id - "Match %d created."</param>
	public sealed record PvpMatch(IReadOnlyList<TeamMemberRecord> Roster, IReadOnlyList<uint> IdList, PvpMatchRules Rules, uint MatchId)
	{
		/// <summary>
		///     The match leader's character id, carried (as a two-word ID32) only on a non-silent match - the client
		///     compares it against the local player to choose the YouCreatedMatch vs YouJoinedMatch announcement.
		/// </summary>
		public uint LeaderId { get; init; }
	}
}
