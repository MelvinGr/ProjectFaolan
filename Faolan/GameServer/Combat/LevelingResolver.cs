namespace Faolan.GameServer.Combat
{
	// Provisional levelling curve (docs/archive/server-architecture-roadmap.md). Experience is treated as progress WITHIN the
	// current level (the 0..MaxExp bar that resets on ding), not a running total. The real AoC curve isn't modelled
	// yet (like the damage/XP values), so this is a simple linear ramp; pure + testable so the combat layer just feeds
	// it the accumulated experience.
	public static class LevelingResolver
	{
		// AoC's level cap.
		public const uint MaxLevel = 80;

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

		// Experience required to advance FROM `level` to level+1. Linear ramp (level 1->2 = 100, 2->3 = 200, ...).
		public static uint XpForNextLevel(uint level)
		{
			var l = level == 0
				? 1u
				: level;
			return 100u * l;
		}

		// Running-total experience needed to have REACHED `level` (sum of every XpForNextLevel below it). The
		// NewLevelIIR level-up summary carries cumulative XP (retail's field 2/3 are the this-level/next-level
		// thresholds), so this projects our within-level curve onto that running-total form. (Retail's real
		// thresholds differ - our curve is the provisional #24 ramp - but they stay self-consistent with the
		// Exp/MaxExp stat push.) Cumulative(1)=0, (2)=100, (3)=300, ...
		public static uint CumulativeXpForLevel(uint level)
		{
			var l = level == 0
				? 1u
				: level;
			return 100u * (l - 1) * l / 2;
		}
	}
}
