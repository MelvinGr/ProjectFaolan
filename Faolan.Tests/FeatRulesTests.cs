using System.Collections.Generic;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// The feat-point prerequisite guard (docs/subsystems/feats.md). The budget comes from the client's own
	// FeatPointsTable and the cost from the captured retail characters, which is what pins it: each one spends its
	// level budget exactly.
	public class FeatRulesTests
	{
		// Hakotep L24 (6 feats), NecroL80 (25), DemoL80 (25) - the real captured feat
		// lists, read off the player spawn's pre-IIR frame.
		private static readonly uint[] Hakotep = [110004, 110100, 111304, 111402, 110300, 112700];

		private static readonly uint[] NecroL80Feats =
		[
			80004, 82704, 410604, 80200, 80104, 81603, 84004, 84104, 410204, 81804, 82801, 410104, 81702, 80401, 82904, 83401, 85700, 410901, 80800, 80902, 82202, 83000, 85400, 81001, 82500
		];

		private static readonly uint[] DemoL80Feats =
		[
			102404, 103604, 103804, 102701, 104204, 103900, 102900, 105500, 102604, 102504, 104002, 103002, 103100, 102804, 104304, 103202, 105004, 105600, 103700, 104404, 104101, 103401, 103500, 104904, 410601
		];

		// The cost model is `rank + 1`, one point per rank, and this is what says so. Three captured
		// characters at two different levels each spend their budget to the last point - 79/79, 79/79, 16/16. Nothing
		// was fitted to get there: the budget is the client's own FeatPointsTable (RDB 1000044) and the feat lists are
		// the wire's.
		//
		// A `max(1, rank)` model leaves all three with points to spare (60, 61, 13), i.e. an assertion that they
		// merely fit with headroom. Headroom that never binds cannot tell a right cost from a wrong one.
		[Fact]
		public void EveryCapturedCharacter_SpendsItsBudgetExactly()
		{
			Assert.Equal(16, FeatRules.TotalCost(Hakotep));
			Assert.Equal(16, FeatRules.PointBudget(24));

			Assert.Equal(79, FeatRules.TotalCost(NecroL80Feats));
			Assert.Equal(79, FeatRules.TotalCost(DemoL80Feats));
			Assert.Equal(79, FeatRules.PointBudget(80));

			// ...and therefore fits, which is the property the guard actually enforces.
			Assert.True(FeatRules.WithinBudget(24, Hakotep));
			Assert.True(FeatRules.WithinBudget(80, NecroL80Feats));
			Assert.True(FeatRules.WithinBudget(80, DemoL80Feats));
		}

		[Fact]
		public void OverBudget_IsRejected()
		{
			// A level-10 char (budget 1) can't hold two rank-1 feats (cost 2 each, so 4).
			Assert.False(FeatRules.WithinBudget(10, [800001u, 800101u]));
			// The same feats are fine once the level (budget) is high enough.
			Assert.True(FeatRules.WithinBudget(80, [800001u, 800101u]));
		}

		// Superseded by FeatPointBudgetTests, which checks these against the client's FeatPointsTable (RDB 1000044)
		// instead of against our own formula. This case asserted 15 at level 24 and 71 at 80 - the `level - 9` rule we
		// had invented - so it could only ever confirm the code it was written from. The real values are 16 and 79.
		[Theory, InlineData(1u, 0), InlineData(9u, 0), InlineData(10u, 1), InlineData(24u, 16), InlineData(80u, 79)]
		public void PointBudget_MatchesTheClientsFeatPointsTable(uint level, int expected)
		{
			Assert.Equal(expected, FeatRules.PointBudget(level));
		}

		// One point per rank, and the rank is 0-based - so a rank-0 feat costs the single point that unlocked it and a
		// rank-4 feat costs the five that walked it up.
		[Theory]
		[InlineData(112700u, 1)] // rank 0
		[InlineData(111402u, 3)] // rank 2
		[InlineData(110004u, 5)] // rank 4
		[InlineData(800071u, 0)] // low digits past the client's rank ceiling: a family index, not a rank - and every
		public void PointCost_IsOnePointPerRank(uint featId, int expected) // one of those is auto-trained, so free
		{
			Assert.Equal(expected, FeatRules.PointCost(featId));
		}

		// An automatic feat costs nothing, and that exemption is what makes the budget usable at all. The
		// automatic feats a level-80 accumulates come to 153 points for one class (77 feats), 97 for another and 89
		// for a third, against a level-80 budget of 79 - so three classes overspend the entire tree budget before
		// the player chooses anything, and every train request is rejected. Since the tree is the only path to its
		// spells, that blocks content rather than merely miscounting.
		[Fact]
		public void AnAutomaticFeat_CostsNothing()
		{
			IReadOnlySet<uint> free = new HashSet<uint> { 800104u };

			Assert.Equal(0, FeatRules.PointCost(800104u, free));
			Assert.Equal(5, FeatRules.PointCost(800104u)); // ...and still costs its ranks when it is not exempt
		}

		// The realistic shape: a pile of handed-out feats that would blow the budget on their own leaves the whole
		// budget available for the tree.
		[Fact]
		public void ABudgetIsNotSpentByTheFeatsAPlayerNeverChose()
		{
			var automatic = new HashSet<uint>();
			for (var i = 0u; i < 77; i++)
				automatic.Add(800000u + i * 100 + 4); // 77 feats at rank 4 = 385 points if they were charged

			var held = new List<uint>(automatic) { 900005u }; // ...plus one the player actually trained

			Assert.False(FeatRules.WithinBudget(80, held));
			Assert.True(FeatRules.WithinBudget(80, held, automatic));
		}

		// And the exemption is exactly that - an exemption, not a bypass. A tree feat outside the free set is
		// still charged, so the budget still binds on what it is meant to bind on.
		[Fact]
		public void ATreeFeatIsStillCharged_EvenBesideTheExemptOnes()
		{
			IReadOnlySet<uint> free = new HashSet<uint> { 800104u };

			// One exempt feat plus 80 points of tree feats against a level-80 budget of 79.
			var held = new List<uint> { 800104u, 900009u, 900109u, 900209u, 900309u, 900409u, 900509u, 900609u, 900709u };

			Assert.Equal(80, FeatRules.TotalCost(held, free));
			Assert.False(FeatRules.WithinBudget(80, held, free));
		}
	}
}
