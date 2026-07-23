using Faolan.GameServer;
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

		[Theory, InlineData(1u, 0), InlineData(9u, 0), InlineData(10u, 1), InlineData(24u, 15), InlineData(80u, 71)]
		// AoC: 1 feat point per level from 10 -> 71 at 80
		public void PointBudget_IsOnePerLevelFromTen(uint level, int expected)
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
	}
}
