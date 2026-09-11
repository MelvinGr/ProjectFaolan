using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// The feat-point budget, against the client's own FeatPointsTable (RDB 1000044 id 1: 71 entries of
	// [level][points][tsPoints]). A `level - 9` budget is right for most levels and wrong at the eight that grant two
	// points - an error the captured characters cannot surface, because all of them fit with headroom.
	public class FeatPointBudgetTests
	{
		[Theory]
		[InlineData(1u, 0)]   // below 10 there is no budget at all
		[InlineData(9u, 0)]
		[InlineData(10u, 1)]  // the first point
		[InlineData(19u, 10)] // still 1/level up to here
		[InlineData(20u, 12)] // 20 is the first double level: 11 + 1
		[InlineData(40u, 34)] // 31 + three doubles (20, 30, 40)
		[InlineData(80u, 79)] // the cap: 71 + all eight doubles
		public void PointBudget_MatchesTheClientsTable(uint level, int expected)
		{
			Assert.Equal(expected, FeatRules.PointBudget(level));
		}

		// The budget must never go backwards as a character levels - a regression here would retroactively invalidate
		// a legally trained feat set.
		[Fact]
		public void PointBudget_IsMonotonic()
		{
			var previous = 0;
			for (var level = 1u; level <= 80; level++)
			{
				var budget = FeatRules.PointBudget(level);
				Assert.True(budget >= previous, $"budget dropped at level {level}: {previous} -> {budget}");
				previous = budget;
			}
		}
	}
}
