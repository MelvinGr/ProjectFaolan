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
		// Whether holding `held` satisfies a prerequisite on `required`.
		//
		// NOT equality, and that is the whole subtlety. The prerequisite graph links ADJACENT RANKS (80004 requires
		// 80003), but a character's wire feat list carries only the HIGHEST rank it holds per base - none of the three
		// captured retail characters stores the intermediate ranks. Requiring the exact id would reject all of them:
		// NecroL80 19 unsatisfied edges, DemoL80 18, Hakotep 3. Same base at an equal-or-higher rank clears all three at
		// zero, which is what says this is the rule rather than a workaround.
		public static bool Satisfies(uint held, uint required)
		{
			return held / 100 == required / 100 && held % 100 >= required % 100;
		}

		// The prerequisite edges `featIds` does NOT satisfy, as (feat, missing requirement) pairs. Empty means the set
		// is legal. `prereqs` maps a feat to the ids it depends on (FeatPrereq, from the client's own feat table).
		public static IReadOnlyList<(uint Feat, uint Requires)> MissingPrerequisites(
			IReadOnlyCollection<uint> featIds, IReadOnlyDictionary<uint, uint[]> prereqs)
		{
			var missing = new List<(uint, uint)>();
			foreach (var feat in featIds)
			{
				if (!prereqs.TryGetValue(feat, out var required))
					continue;

				foreach (var requirement in required)
				{
					var satisfied = false;
					foreach (var held in featIds)
					{
						if (Satisfies(held, requirement))
						{
							satisfied = true;
							break;
						}
					}

					if (!satisfied)
						missing.Add((feat, requirement));
				}
			}

			return missing;
		}

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
