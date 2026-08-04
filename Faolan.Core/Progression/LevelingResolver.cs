namespace Faolan.Core.Progression
{
	// Per-level experience curve, anchored on REAL retail values harvested from the captures (#297).
	//
	// ⚠️ **Experience is CUMULATIVE on the wire, not within-level.** This is the correction that matters: the three
	// XP stats retail sends are a running total and its two boundaries -
	//   Exp (0x034)                 the character's running total
	//   ExpCurrentLevelBase (0x039) the running total at which the CURRENT level began
	//   MaxExp (0x15E)              the running total at which the NEXT level begins
	// so the client's XP bar fills `(Exp - Base) / (MaxExp - Base)` and the cost of a level is the DIFFERENCE
	// `MaxExp - Base`. Every captured snapshot agrees, and the two adjacent captures prove it outright: a level-23
	// character carries base=230240 max=263640, and a level-24 one carries base=263640 - the same number. The
	// level-up event says it a third time: `NewLevelIIR`'s six "deltas" are `[Exp, Base, MaxExp, 1, 8, 0]`
	// (L2 ding: [100|120|168, 100, 340, 1, 8, 0] across seven captures - the first varies per player, the next two
	// never do).
	//
	// This table used to hold those captured MaxExp values and hand them back as PER-LEVEL costs, which made a level
	// cost the whole running total up to it: level 24 asked for 298,040 XP instead of 34,400, and
	// CumulativeXpForLevel summed the inflated values on top. So the anchors below are stored as what they were
	// measured as - CUMULATIVE - and the per-level cost is derived.
	//
	// The curve is a hand-tuned SERVER table. The client does not hold it: the 25 named `CoreTables` data tables
	// contain no XP table, `GAMETWEAKS` (RDB 1000516) has no level-threshold entry, and the exe's only XP strings are
	// the three UI value names it fills from these very stats. So captures are the only source, and levels between two
	// anchors are linearly interpolated ON THE CUMULATIVE CURVE (monotone, so the derived per-level costs stay
	// non-decreasing); the anchored levels are exact retail.
	public static class LevelingResolver
	{
		// AoC's level cap.
		public const uint MaxLevel = 80;

		// (level -> the CUMULATIVE experience at which `level` begins), harvested from retail. Sources per anchor:
		//   L1-L5, L6, L9   the NewLevelIIR ding events (deltas[1] = the new level's base, deltas[2] = the next
		//                   level's, so one ding pins two levels) across the create/levelup captures
		//   L8              the L8 ding's own base
		//   L23/L24/L25     the lvl23 + lvl24 account snapshots (base + MaxExp)
		//   L80/L81         the max-level logins; 81 is only ever a bar boundary, never reachable
		// L7 and everything in 10..22 / 26..79 has no capture and is interpolated.
		private static readonly (uint Level, uint Cumulative)[] Anchors =
		[
			(1, 0), (2, 100), (3, 340), (4, 740), (5, 1440), (6, 3940), (8, 10240), (9, 14140),
			(23, 230240), (24, 263640), (25, 298040), (80, 34771240), (81, 47907140)
		];

		// Spends accumulated `experience` against the curve from `level`: returns how many levels were gained and the
		// leftover within-level experience. Stops at MaxLevel (no overflow - leftover is zeroed at the cap).
		//
		// `experience` here is WITHIN-LEVEL progress, which is how the character row stores it; the cumulative form is
		// reconstructed for the wire as CumulativeXpForLevel(level) + progress.
		public static (uint LevelsGained, uint RemainingXp) Apply(uint level, uint experience)
		{
			uint gained = 0;
			while (level + gained < MaxLevel)
			{
				var need = XpForNextLevel(level + gained);
				if (experience < need)
					break;

				experience -= need;
				gained++;
			}

			if (level + gained >= MaxLevel)
				experience = 0; // capped: the bar sits empty at max level

			return (gained, experience);
		}

		// The CUMULATIVE experience at which `level` begins - retail's `ExpCurrentLevelBase` for a character of that
		// level, and its `MaxExp` when asked for level+1. Exact at a captured anchor, linearly interpolated between
		// the surrounding two otherwise. Cumulative(1) = 0.
		public static uint CumulativeXpForLevel(uint level)
		{
			var l = level == 0
				? 1u
				: level;

			if (l >= Anchors[^1].Level)
				return Anchors[^1].Cumulative;

			for (var i = 0; i < Anchors.Length; i++)
			{
				if (Anchors[i].Level == l)
					return Anchors[i].Cumulative;

				if (Anchors[i].Level > l)
				{
					var (la, ca) = Anchors[i - 1];
					var (lb, cb) = Anchors[i];
					return ca + (uint)((ulong)(cb - ca) * (l - la) / (lb - la));
				}
			}

			return Anchors[^1].Cumulative;
		}

		// Experience required to advance FROM `level` to level+1 - the DIFFERENCE between two cumulative thresholds,
		// which is what retail's own bar measures (`MaxExp - ExpCurrentLevelBase`). Exact wherever both ends are
		// anchored (L1-L5: 100/240/400/700/2500; L23: 33,400; L24: 34,400).
		public static uint XpForNextLevel(uint level)
		{
			var l = level == 0
				? 1u
				: level;

			return CumulativeXpForLevel(l + 1) - CumulativeXpForLevel(l);
		}
	}
}
