namespace Faolan.Core.Progression
{
	// Per-level experience curve, anchored on REAL retail values harvested from the captures (#297): the server sends
	// MaxExp (stat 0x15E) per character level, and pairing it with Level (stat 0x36) in the same stat block gives the
	// xp to advance FROM that level. The curve is a hand-tuned SERVER table - the client does not hold it (the exe
	// contains none of the high-level values), and no simple formula fits (a power-law / cubic through the anchors
	// diverges badly). So we anchor on the captured levels and linearly INTERPOLATE between them; the anchored levels
	// are exact retail, the in-between levels are best-effort pending more captures. Experience is within-level progress
	// (0..MaxExp, resets on ding), not a running total.
	public static class LevelingResolver
	{
		// AoC's level cap.
		public const uint MaxLevel = 80;

		// (level -> xp to advance FROM `level` to level+1), captured from retail: the MaxExp stat paired with the
		// character's Level. L1/2/4/5 (beach/early logins), L23/24 (lvl23/24 account captures), L80 (max-level logins).
		private static readonly (uint Level, uint Xp)[] Anchors = [(1, 100), (2, 340), (4, 1440), (5, 3940), (23, 263640), (24, 298040), (80, 47907140)];

		// Spends accumulated `experience` against the curve from `level`: returns how many levels were gained and the
		// leftover within-level experience. Stops at MaxLevel (no overflow - leftover is zeroed at the cap).
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

		// Running-total experience needed to have REACHED `level` (sum of every XpForNextLevel below it). The
		// NewLevelIIR level-up summary carries cumulative XP; this projects the per-level curve onto that running-total
		// form. Cumulative(1) = 0.
		public static uint CumulativeXpForLevel(uint level)
		{
			var l = level == 0
				? 1u
				: level;

			uint sum = 0;
			for (uint k = 1; k < l; k++)
				sum += XpForNextLevel(k);

			return sum;
		}

		// Experience required to advance FROM `level` to level+1 — exact at a captured anchor, linearly interpolated
		// between the surrounding anchors otherwise (retail's real table is server-held; the anchors pin it at the
		// captured levels).
		public static uint XpForNextLevel(uint level)
		{
			var l = level == 0
				? 1u
				: level;

			if (l >= MaxLevel)
				return Anchors[^1].Xp;

			for (var i = 0; i < Anchors.Length; i++)
			{
				if (Anchors[i].Level == l)
					return Anchors[i].Xp;

				if (Anchors[i].Level > l)
				{
					var (la, xa) = Anchors[i - 1];
					var (lb, xb) = Anchors[i];
					return xa + (uint)((ulong)(xb - xa) * (l - la) / (lb - la));
				}
			}

			return Anchors[^1].Xp;
		}
	}
}
