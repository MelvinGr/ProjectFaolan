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
		// Levels that grant TWO feat points instead of one. Straight out of the client's own FeatPointsTable - RDB
		// type 1000044 id 1, the resource FeatClassTable_c::LoadStaticData reads - which is 71 entries of
		// [level 10..80][points][tsPoints].
		private static readonly uint[] DoublePointLevels = [20, 30, 40, 50, 60, 70, 75, 80];

		// Feat points earned by level. Was `level - 9` (1 per level from 10), giving 71 at level 80; the client's table
		// gives **79** - eight levels grant two. The old value was "verified" against the captured characters, but
		// every one of them fit with headroom, so the budget never actually bound and the error could not show up.
		public static int PointBudget(uint level)
		{
			if (level < 10)
				return 0;

			var points = (int)(level - 9);
			foreach (var doubled in DoublePointLevels)
			{
				if (level >= doubled)
					points++;
			}

			return points;
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
