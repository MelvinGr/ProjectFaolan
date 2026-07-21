using Faolan.Core.Database.Tables;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Server-side gear-stat aggregation (#3): the MaxStatAggregator sums a character's equipped items' primary-attribute
	// bonus columns (Item.*Bonus) by wire stat id. The DATA (which stat, how much) is the decode-first-validated
	// primary-attribute modifier (RDB field 972/971); this pins the summing.
	public class MaxStatAggregatorTests
	{
		private const uint Str = 804, Dex = 814;

		[Fact]
		public void EmptyEquipment_YieldsNoBonuses()
		{
			var totals = MaxStatAggregator.AggregateGearBonuses([]);

			Assert.Empty(totals);
			Assert.False(totals.ContainsKey(Str)); // an unworn stat is absent (a caller reads it as 0)
		}

		[Fact]
		public void SumsSameStatAcrossItems_AndKeepsStatsSeparate()
		{
			var equipped = new[]
			{
				new Item
				{
					Id = 1,
					StrengthBonus = 2
				},
				new Item
				{
					Id = 2,
					StrengthBonus = 3
				}, // two +Str pieces stack
				new Item
				{
					Id = 3,
					DexterityBonus = 1
				}
			};

			var totals = MaxStatAggregator.AggregateGearBonuses(equipped);

			Assert.Equal(5, totals[Str]); // 2 + 3
			Assert.Equal(1, totals[Dex]);
		}
	}
}
