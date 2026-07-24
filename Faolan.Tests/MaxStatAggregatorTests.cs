using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Server-side gear-stat aggregation (#3): the MaxStatAggregator sums a character's equipped items' StatModifiers by
	// stat, converting each raw-wire Amount to its DISPLAY value (÷scale). The DATA (which stat, how much) is the
	// decode-validated item stat-modifier region (docs/rdb/item-template-stats.md); this pins the summing + scaling.
	public class MaxStatAggregatorTests
	{
		[Fact]
		public void AppliesTheWireScalePerStat_VitalsX100_AttributesX10()
		{
			var equipped = new[]
			{
				Item(1, (CharacterStatId.HealthMax, 6600), (CharacterStatId.Constitution, 300)), // +66 HP, +30 Con
				Item(2, (CharacterStatId.HealthMax, 15000)) // +150 HP
			};

			var totals = MaxStatAggregator.AggregateGearBonuses(equipped);

			Assert.Equal(216, totals[CharacterStatId.HealthMax]); // (6600 + 15000) / 100
			Assert.Equal(30, totals[CharacterStatId.Constitution]); // 300 / 10
		}

		[Fact]
		public void EmptyEquipment_YieldsNoBonuses()
		{
			var totals = MaxStatAggregator.AggregateGearBonuses([]);

			Assert.Empty(totals);
			Assert.False(totals.ContainsKey(CharacterStatId.Strength)); // an unworn stat is absent (a caller reads it as 0)
		}

		[Fact]
		public void SumsSameStatAcrossItems_AndKeepsStatsSeparate()
		{
			// Raw wire amounts: Str is ×10 (20 -> +2, 30 -> +3), Dex ×10 (10 -> +1).
			var equipped = new[]
			{
				Item(1, (CharacterStatId.Strength, 20)), Item(2, (CharacterStatId.Strength, 30)), // two +Str pieces stack
				Item(3, (CharacterStatId.Dexterity, 10))
			};

			var totals = MaxStatAggregator.AggregateGearBonuses(equipped);

			Assert.Equal(5, totals[CharacterStatId.Strength]); // (20 + 30) / 10
			Assert.Equal(1, totals[CharacterStatId.Dexterity]); // 10 / 10
		}

		private static Item Item(uint id, params (CharacterStatId Stat, int WireAmount)[] mods)
		{
			var item = new Item
			{
				Id = id
			};
			foreach (var (stat, amount) in mods)
				item.StatModifiers.Add(new ItemStatModifier
				{
					ItemId = id,
					StatId = stat,
					Amount = amount
				});
			return item;
		}
	}
}
