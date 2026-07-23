using System;
using System.Collections.Generic;

namespace Faolan.GameServer
{
	// Server-side feat POINT rules - a coarse prerequisite guard grounded in the captured retail chars (Hakotep L24,
	// NecroL80/DemoL80 L80): a character's total spent feat points must not exceed its level budget. This is the subset
	// of feat prerequisites we can enforce WITHOUT the client's RDB feat catalog; real per-feat costs and the
	// tier/parent-feat prerequisite graph need the Feat definition table (the wiki-ingestion step, #251). See
	// docs/subsystems/feats.md.
	public static class FeatRules
	{
		// Feat points earned by level: 1 per level from level 10 (AoC's rule; level 80 = 71 points). Verified against
		// the captured chars - each one's spent points fit within this budget with headroom.
		public static int PointBudget(uint level)
		{
			return level > 9
				? (int)(level - 9)
				: 0;
		}

		// A feat's point cost = max(1, encoded rank). The composite feat id bakes the rank into its low two digits
		// (featBase*100 + rank), so every feat costs at least 1 (the unlock) plus one per rank above the first. This
		// keeps rank-0 unlock feats from being "free" (which would let a char train unlimited feats).
		public static int PointCost(uint featId)
		{
			return Math.Max(1, (int)(featId % 100));
		}

		public static int TotalCost(IEnumerable<uint> featIds)
		{
			var sum = 0;
			foreach (var id in featIds)
				sum += PointCost(id);

			return sum;
		}

		// Whether a character at `level` may hold `featIds` (existing + would-be-trained) without overspending its
		// feat-point budget.
		public static bool WithinBudget(uint level, IEnumerable<uint> featIds)
		{
			return TotalCost(featIds) <= PointBudget(level);
		}
	}
}
