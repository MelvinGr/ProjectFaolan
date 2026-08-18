using System.Collections.Generic;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// The feat-point prerequisite guard (docs/subsystems/feats.md). The budget + cost model is calibrated against the
	// captured retail chars: each one's trained feats fit within its level budget with headroom.
	public class FeatRulesTests
	{
		[Fact]
		public void CapturedChars_FitWithinTheirBudget()
		{
			// Hakotep L24 (6 feats), NecroL80 L80 (25). Real captured sets; each must be within budget.
			uint[] hakotep = [110004, 110100, 111304, 111402, 110300, 112700];
			uint[] necrol80 =
			[
				80004, 82704, 410604, 80200, 80104, 81603, 84004, 84104, 410204, 81804, 82801, 410104, 81702, 80401, 82904, 83401, 85700, 410901, 80800, 80902, 82202, 83000, 85400, 81001, 82500
			];

			Assert.True(FeatRules.WithinBudget(24, hakotep)); // 13 <= 15
			Assert.True(FeatRules.WithinBudget(80, necrol80)); // 60 <= 71
		}

		[Fact]
		public void OverBudget_IsRejected()
		{
			// A level-10 char (budget 1) can't hold two rank-1 feats (cost 2).
			Assert.False(FeatRules.WithinBudget(10, [800001u, 800101u]));
			// The same feats are fine once the level (budget) is high enough.
			Assert.True(FeatRules.WithinBudget(80, [800001u, 800101u]));
		}

		// Superseded by FeatPointBudgetTests, which checks these against the CLIENT's FeatPointsTable (RDB 1000044)
		// instead of against our own formula. This case asserted 15 at level 24 and 71 at 80 - the `level - 9` rule we
		// had invented - so it could only ever confirm the code it was written from. The real values are 16 and 79.
		[Theory, InlineData(1u, 0), InlineData(9u, 0), InlineData(10u, 1), InlineData(24u, 16), InlineData(80u, 79)]
		public void PointBudget_MatchesTheClientsFeatPointsTable(uint level, int expected)
		{
			Assert.Equal(expected, FeatRules.PointBudget(level));
		}

		[Theory, InlineData(112700u, 1), InlineData(110004u, 4), InlineData(111402u, 2)]
		// rank 0 (id % 100 == 0) still costs 1 (the unlock)
		// rank 4
		// rank 2
		public void PointCost_IsMaxOneAndRank(uint featId, int expected)
		{
			Assert.Equal(expected, FeatRules.PointCost(featId));
		}

		// ⛔ #850: an AUTOMATIC feat costs NOTHING, and that exemption is what makes the budget usable at all. The
		// automatic feats a level-80 accumulates come to 153 points for one class (77 feats), 97 for another and 89
		// for a third, against a level-80 budget of 79 - so three classes overspend the entire tree budget before
		// the player chooses anything, and every train request is rejected. Since the tree is the only path to its
		// spells, that blocks content rather than merely miscounting.
		[Fact]
		public void AnAutomaticFeat_CostsNothing()
		{
			IReadOnlySet<uint> free = new HashSet<uint> { 800110u };

			Assert.Equal(0, FeatRules.PointCost(800110u, free));
			Assert.Equal(10, FeatRules.PointCost(800110u)); // ...and still costs its rank when it is not exempt
		}

		// The realistic shape: a pile of handed-out feats that would blow the budget on their own leaves the whole
		// budget available for the tree.
		[Fact]
		public void ABudgetIsNotSpentByTheFeatsAPlayerNeverChose()
		{
			var automatic = new HashSet<uint>();
			for (var i = 0u; i < 77; i++)
				automatic.Add(800000u + i * 100 + 10); // 77 feats at rank 10 = 770 points if they were charged

			var held = new List<uint>(automatic) { 900005u }; // ...plus one the player actually trained

			Assert.False(FeatRules.WithinBudget(80, held));
			Assert.True(FeatRules.WithinBudget(80, held, automatic));
		}

		// ⚠️ And the exemption is exactly that - an exemption, not a bypass. A tree feat outside the free set is
		// still charged, so the budget still binds on what it is meant to bind on.
		[Fact]
		public void ATreeFeatIsStillCharged_EvenBesideTheExemptOnes()
		{
			IReadOnlySet<uint> free = new HashSet<uint> { 800110u };

			// One exempt feat plus 80 points of tree feats against a level-80 budget of 79.
			var held = new List<uint> { 800110u, 900040u, 900140u };

			Assert.Equal(80, FeatRules.TotalCost(held, free));
			Assert.False(FeatRules.WithinBudget(80, held, free));
		}
	}
}
