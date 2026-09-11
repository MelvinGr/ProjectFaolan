using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Pvp
{
	/// <summary>
	///     The <c>i_cMatch</c> payload - a whole custom PvP match - which is the entire body of both s2c methods that
	///     carry one: <c>DispatchPvPMatchMakingYouJoinedMatch</c>, sent to a player entering the match (the creator
	///     included, since the client's copier raises its own YouCreatedMatch fifo when the tail's leader ID32 is the
	///     local player), and <c>DispatchPvPMatchMakingMatchInfo</c>, the answer to
	///     <c>PvPMatchMakingGetMatchInfo</c>.
	/// </summary>
	/// <remarks>
	///     There are no captured instances: no captured queue ever filled and no capture creates a custom match. The
	///     layout is pinned anyway, because the client holds all three halves of it and they agree - the s2c readers
	///     (<c>FUN_01346ef0</c> / <c>FUN_013470d0</c>), the c2s writer (<c>FUN_00558880</c>,
	///     <c>PvPMatchMakingCreateMatch</c>) and the local copy (<c>FUN_00547070</c>, which stores the joined match and
	///     whose stride and tail checks are the carve's proof). docs/re/minigame-massivepvp-messages.md is the decode.
	///     <code>
	///     [list frame] { n x cCharacter }    the member roster - the same record the team subsystem carries
	///     [list frame] { m x u32 }           an id list; the client stores it unnamed (reader FUN_004d4ef0)
	///     PvPMatchRules_c                    PvpMatchRulesCodec
	///     [u32 matchId]                      the debug view's ID column and its "Match %d created." line
	///     if (!rules.Silent) [ID32 leader]   the match leader's two-word ID32
	///     </code>
	///     <para>
	///         The tail pair is the leader's ID32 rather than two unnamed words: the copier's fifo chooser compares the
	///         stored pair against the local player's ID32 and raises <c>PvPMatchMakingYouCreatedMatch</c> on a match
	///         and <c>PvPMatchMakingYouJoinedMatch</c> otherwise. That also explains why a silent match does not carry
	///         it.
	///     </para>
	/// </remarks>
	public static class PvpMatchCodec
	{
		public static readonly CSPlayerInterfaceMethods[] HandledMethods =
		[
			CSPlayerInterfaceMethods.DispatchPvPMatchMakingYouJoinedMatch, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchInfo
		];

		// Canonical: the only method here that writes bytes.
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

			// The leader ID32 rides only a non-silent match: the client's reader and copier both gate on the same rules
			// byte, so a silent match's body ends at the id.
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
