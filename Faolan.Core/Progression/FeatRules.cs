using System.Collections.Generic;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     Server-side feat point rules: a coarse prerequisite guard grounded in the captured retail characters
	///     (Hakotep L24, NecroL80/DemoL80 L80) - a character's total spent feat points must not exceed its level
	///     budget.
	/// </summary>
	/// <remarks>
	///     This is the subset of feat prerequisites enforceable without the client's RDB feat catalog; real per-feat
	///     costs and the tier/parent-feat prerequisite graph need the Feat definition table. See
	///     <c>docs/subsystems/feats.md</c>.
	/// </remarks>
	public static class FeatRules
	{
		/// <summary>
		///     What the class progression track's feats are called - <c>Necromancer Class Feat 001</c>,
		///     <c>Ranger Class Feat 071</c>: the eighty per-class templates that carry a character's attribute and
		///     health base, one per level, in blocks of ten.
		/// </summary>
		/// <remarks>
		///     It is a name and not a tree id on purpose. The client files these under <c>TreeId</c> 9000, but so are
		///     <c>Spell Unlocking</c>, the <c>Light Weapon Combos</c> bands and <c>Pet Mastery</c> - and Pet Mastery is
		///     the one auto-trained feat outside the track that carries stat effects, so a tree-id filter would drop
		///     precisely the rows the track's stat application needs. The client's own importer for the track
		///     (<c>ClassAttributeBaseModule</c>) keys on this same name, which is what keeps the two halves from
		///     double-counting each other.
		/// </remarks>
		public const string ClassProgressionTrackMarker = " Class Feat ";

		/// <summary>
		///     Levels that grant two feat points instead of one. Straight out of the client's own FeatPointsTable - RDB
		///     type 1000044 id 1, the resource <c>FeatClassTable_c::LoadStaticData</c> reads - which is 71 entries of
		///     <c>[level 10..80][points][tsPoints]</c>.
		/// </summary>
		private static readonly uint[] DoublePointLevels = [20, 30, 40, 50, 60, 70, 75, 80];

		/// <summary>
		///     Feat points earned by level, as the client's table states them: one per level from 10, plus a second at
		///     each of <see cref="DoublePointLevels" />, so level 80 has 79.
		/// </summary>
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

		/// <summary>
		///     Whether holding <paramref name="held" /> satisfies a prerequisite on <paramref name="required" />.
		/// </summary>
		/// <remarks>
		///     Not equality, and that is the whole subtlety. The prerequisite graph links adjacent ranks (80004
		///     requires 80003), but a character's wire feat list carries only the highest rank it holds per base -
		///     none of the three captured retail characters stores the intermediate ranks. Requiring the exact id
		///     would reject all of them: NecroL80 19 unsatisfied edges, DemoL80 18, Hakotep 3. Same base at an
		///     equal-or-higher rank clears all three at zero, which is what says this is the rule rather than a
		///     workaround.
		/// </remarks>
		public static bool Satisfies(uint held, uint required)
		{
			return held / 100 == required / 100 && held % 100 >= required % 100;
		}

		/// <summary>
		///     The prerequisite edges <paramref name="featIds" /> does not satisfy, as (feat, missing requirement)
		///     pairs. Empty means the set is legal.
		/// </summary>
		/// <param name="prereqs">
		///     Maps a feat to the ids it depends on (<c>FeatPrereq</c>, from the client's own feat table).
		/// </param>
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
		///     What a feat costs in points: one point per rank, so holding the 0-based rank <c>r</c> baked into the
		///     wire id's low two digits costs <c>r + 1</c>.
		/// </summary>
		/// <param name="free">
		///     The wire ids that cost nothing - the auto-trained feats the client hands over by itself.
		/// </param>
		/// <remarks>
		///     The captured characters settle this exactly. Summing <c>rank + 1</c> over each one's trained feats
		///     reproduces its level budget to the point:
		///     <list type="table">
		///         <item><description>NecroL80 - the level-80 Necromancer - 25 feats, 79 points, budget 79</description></item>
		///         <item><description>DemoL80 - the level-80 Demonologist - 25 feats, 79 points, budget 79</description></item>
		///         <item><description>Hakotep, level 24 Herald of Xotli - 6 feats, 16 points, budget 16</description></item>
		///     </list>
		///     Three characters at two different levels, each spending its budget to the last point. No client table
		///     holds a per-feat cost that would say otherwise: RDB 1000099 ("feat point cost info") exists but covers
		///     only the twelve level-20/80 alternate-advancement perk trees (all 2,880 of the placements it prices,
		///     none of them a feat any captured character holds - see <c>FeatPerkCost</c>), and the ordinary trees
		///     have no cost row at all.
		///     <para />
		///     Ranks run 0..9 - the client's own bound (<c>FeatIdMap.MaxRank</c>, from <c>FUN_007850c0</c>'s
		///     <c>base*100 + 10</c> ceiling). Above it the low digits index a family rather than naming a rank
		///     ("Guardian Class Feat 071"), and every one of those is an auto-trained class-progression row, so it is
		///     free rather than a 71-point feat.
		///     <para />
		///     The <paramref name="free" /> exemption should have nothing to do: an auto-trained feat is not put in a
		///     character's list at all, because retail does not put one on the wire and <c>FeatHolder::TrainFeat</c>
		///     refuses one outright. It remains as the guard for a character whose rows predate that.
		/// </remarks>
		public static int PointCost(uint featId, IReadOnlySet<uint>? free = null)
		{
			if (free != null && free.Contains(featId))
				return 0;

			var rank = featId % 100;

			return rank >= NonRankLowDigits ? 0 : (int)rank + 1;
		}

		/// <summary>
		///     The client's own ceiling on a feat rank (<c>FeatIdMap.MaxRank</c>): at or above it the id's low digits
		///     are a family index, not a rank. Restated here because Faolan.Core does not reference the database
		///     project.
		/// </summary>
		private const uint NonRankLowDigits = 10;

		public static int TotalCost(IEnumerable<uint> featIds, IReadOnlySet<uint>? free = null)
		{
			var sum = 0;
			foreach (var id in featIds)
				sum += PointCost(id, free);

			return sum;
		}

		/// <summary>
		///     Whether a character at <paramref name="level" /> may hold <paramref name="featIds" /> (existing plus
		///     would-be-trained) without overspending its feat-point budget.
		/// </summary>
		/// <param name="free">The automatic feats, which cost nothing - see <see cref="PointCost" />.</param>
		public static bool WithinBudget(uint level, IEnumerable<uint> featIds, IReadOnlySet<uint>? free = null)
		{
			return TotalCost(featIds, free) <= PointBudget(level);
		}
	}
}
