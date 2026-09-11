using System;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The Khitai reputation ladder: the bounds a standing is clamped to, the neutral band, and the point
	///     thresholds a rank title is earned at.
	/// </summary>
	/// <remarks>
	///     Every number here is the client's, from its own tuning sheet - RDB 1000516, the tweak table
	///     (<c>--report rdb-tweaks --report-arg Faction</c>). None of it is balanced by us, which matters because a
	///     reputation curve invented server-side would be wrong in a way no test could catch.
	///     <para />
	///     The ladder is signed and symmetric: not 0 to 1,000,000, but -1,100,000 to +1,100,000 with a +/-5,000
	///     neutral band in the middle. That is what makes the rival-pair rule expressible at all - killing one side of
	///     a pair costs standing with them and grants it to their rival, so a farmed-against faction sits below the
	///     band, hostile, rather than merely at zero.
	///     <para />
	///     The thresholds and the payout matrix corroborate each other, which is the check that they belong to one
	///     system: at 120 points for a solo normal kill, rank 1 is ~417 kills and rank 4 ~8,333; on the group-boss row
	///     (1,800) rank 4 is 556. An unrelated threshold set would have no reason to land on that order of magnitude.
	///     <para />
	///     One ladder serves all sixteen factions - the tweak names carry no per-faction dimension. Only nine of the
	///     sixteen carry a rank title ladder in LDB[254] (<c>Faction.HasPlayerRanks</c>); the rest look like NPC-only
	///     reputations, so a rank number on one of those has no title to draw.
	/// </remarks>
	public static class FactionLadder
	{
		/// <summary>RDB 1000516 <c>MaxFactionPoints</c> / <c>MinFactionPoints</c>.</summary>
		public const int MaxPoints = 1_100_000;
		public const int MinPoints = -1_100_000;

		/// <summary>
		///     <c>NeutralMaxFactionPoints</c> / <c>NeutralMinFactionPoints</c> - the band around zero that counts as
		///     neither friend nor foe.
		/// </summary>
		public const int NeutralMax = 5_000;
		public const int NeutralMin = -5_000;

		/// <summary><c>FactionValueRank1..4</c>. Absolute point counts, not deltas.</summary>
		public const int Rank1 = 50_000;
		public const int Rank2 = 200_000;
		public const int Rank3 = 450_000;
		public const int Rank4 = 1_000_000;

		/// <summary>
		///     The highest rank a standing has reached, 0 (none) through 4. Rank 0 covers everything below the rank-1
		///     threshold, including the whole negative half - a hostile standing has no rank, it has a hostile
		///     standing.
		/// </summary>
		public static int RankFor(int points)
		{
			return points >= Rank4 ? 4
				: points >= Rank3 ? 3
				: points >= Rank2 ? 2
				: points >= Rank1 ? 1
				: 0;
		}

		/// <summary>
		///     True while the standing sits inside the neutral band - the state a character starts in with every
		///     faction, and the reason a missing <c>CharacterFactionStanding</c> row reads as neutral rather than as
		///     unknown.
		/// </summary>
		public static bool IsNeutral(int points)
		{
			return points is >= NeutralMin and <= NeutralMax;
		}

		/// <summary>
		///     True once the standing has fallen below the neutral band. Named for what the data says rather than for
		///     a behaviour: what a hostile standing makes an NPC do is not decoded, so nothing here claims it.
		/// </summary>
		public static bool IsHostile(int points)
		{
			return points < NeutralMin;
		}

		/// <summary>
		///     Applies a signed award and clamps to the client's bounds, returning the new total. The clamp is the
		///     reason this is a function rather than an addition at the call site: an unclamped grind would run a
		///     standing past a ceiling the client draws its bar against.
		/// </summary>
		public static int Apply(int points, int delta)
		{
			return Math.Clamp(points + delta, MinPoints, MaxPoints);
		}
	}
}
