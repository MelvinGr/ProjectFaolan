using System.Collections.Generic;

namespace Faolan.Core.Progression
{
	// Server-side feat POINT rules - a coarse prerequisite guard grounded in the captured retail chars (Hakotep L24,
	// NecroL80/DemoL80 L80): a character's total spent feat points must not exceed its level budget. This is the subset
	// of feat prerequisites we can enforce WITHOUT the client's RDB feat catalog; real per-feat costs and the
	// tier/parent-feat prerequisite graph need the Feat definition table (the wiki-ingestion step, #251). See
	// docs/subsystems/feats.md.
	public static class FeatRules
	{
		/// <summary>
		///     What the class PROGRESSION track's feats are called - <c>Necromancer Class Feat 001</c>,
		///     <c>Ranger Class Feat 071</c>: the eighty per-class templates that carry a character's attribute and
		///     health BASE, one per level, in blocks of ten.
		/// </summary>
		/// <remarks>
		///     ⛔ It is a name and not a tree id on purpose. The client files these under <c>TreeId</c> 9000, but so
		///     are <c>Spell Unlocking</c>, the <c>Light Weapon Combos</c> bands and <c>Pet Mastery</c> - and Pet
		///     Mastery is the one auto-trained feat outside the track that carries stat effects, so a tree-id filter
		///     would drop precisely the rows #863 exists to apply. The client's own importer for the track
		///     (<c>ClassAttributeBaseModule</c>) keys on this same name, which is what keeps the two halves from
		///     double-counting each other.
		/// </remarks>
		public const string ClassProgressionTrackMarker = " Class Feat ";

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

		/// <summary>
		///     What a feat costs in points: <b>one point per rank</b>, so holding the 0-based rank <c>r</c> baked into
		///     the wire id's low two digits costs <c>r + 1</c>. <paramref name="free" /> holds the wire ids that cost
		///     NOTHING - the AUTO-TRAINED feats the client hands over by itself.
		/// </summary>
		/// <remarks>
		///     ⭐ <b>The captured characters settle this exactly</b> (#862). Summing <c>rank + 1</c> over each one's
		///     trained feats reproduces its level budget to the point:
		///     <list type="table">
		///         <item><description>NecroL80 - the level-80 Necromancer - 25 feats, <b>79</b> points, budget 79</description></item>
		///         <item><description>DemoL80 - the level-80 Demonologist - 25 feats, <b>79</b> points, budget 79</description></item>
		///         <item><description>Hakotep, level 24 Herald of Xotli - 6 feats, <b>16</b> points, budget 16</description></item>
		///     </list>
		///     Three characters at two different levels, each spending its budget to the last point. The previous
		///     model - <c>max(1, rank)</c>, which charged nothing for the step from rank 0 to rank 1 - left all three
		///     with points unspent (60/79, 61/79, 13/16), and no client table holds a per-feat cost that would
		///     explain the difference: RDB <b>1000099</b> ("feat point cost info") exists but covers only the
		///     fourteen level-20/80 MASTERY trees (all 2,880 of its rows, none of them a feat any captured character
		///     holds), and the ordinary trees have no cost row at all.
		///     <para />
		///     ⚠️ Ranks run 0..9 - the client's own bound (<c>FeatIdMap.MaxRank</c>, from <c>FUN_007850c0</c>'s
		///     <c>base*100 + 10</c> ceiling). Above it the low digits index a family rather than naming a rank
		///     ("Guardian Class Feat 071"), and every one of those is an auto-trained class-progression row, so it is
		///     free rather than a 71-point feat.
		///     <para />
		///     ⛔ The <paramref name="free" /> exemption stays, but it should no longer have anything to do: an
		///     auto-trained feat is not put in a character's list at all any more (#862), because retail does not put
		///     one on the wire and <c>FeatHolder::TrainFeat</c> refuses one outright. It remains as the guard for a
		///     character whose rows predate that fix.
		/// </remarks>
		public static int PointCost(uint featId, IReadOnlySet<uint>? free = null)
		{
			if (free != null && free.Contains(featId))
				return 0;

			var rank = featId % 100;

			return rank >= NonRankLowDigits ? 0 : (int)rank + 1;
		}

		// The client's own ceiling on a feat RANK (FeatIdMap.MaxRank): at or above it the id's low digits are a
		// family index, not a rank. Restated here because Faolan.Core does not reference the database project.
		private const uint NonRankLowDigits = 10;

		public static int TotalCost(IEnumerable<uint> featIds, IReadOnlySet<uint>? free = null)
		{
			var sum = 0;
			foreach (var id in featIds)
				sum += PointCost(id, free);

			return sum;
		}

		// Whether a character at `level` may hold `featIds` (existing + would-be-trained) without overspending its
		// feat-point budget. `free` are the automatic feats, which cost nothing - see PointCost.
		public static bool WithinBudget(uint level, IEnumerable<uint> featIds, IReadOnlySet<uint>? free = null)
		{
			return TotalCost(featIds, free) <= PointBudget(level);
		}
	}
}
