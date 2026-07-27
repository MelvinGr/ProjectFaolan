using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Feat stat bonuses feeding the live vitals (#251). The gate that matters here is the SCALE: FeatEffect.Value is a
	// DISPLAY value while ItemStatModifier.Amount is the raw wire value, so the two aggregators must NOT share a
	// convention. Getting that wrong under-grants every feat by 10x (attributes) or 100x (vitals) and would look like
	// "feats do nothing much" rather than like a bug.
	public class FeatStatBonusTests
	{
		[Fact]
		public void AggregateFeatBonuses_DoesNotApplyTheWireScale()
		{
			// Strength scales x10 on the wire and HealthMax x100 - if this divided like the gear path, +3 Strength
			// would land as 0 and +500 health as 5.
			var effects = new[]
			{
				new FeatEffect { StatId = CharacterStatId.Strength, Value = 3 },
				new FeatEffect { StatId = CharacterStatId.HealthMax, Value = 500 }
			};

			var totals = MaxStatAggregator.AggregateFeatBonuses(effects);

			Assert.Equal(3, totals[CharacterStatId.Strength]);
			Assert.Equal(500, totals[CharacterStatId.HealthMax]);
		}

		// The real shape from the catalog: "Soldier: 1HB and 1HE 1" grants +720 to two attack ratings at once, and its
		// rank 2 grants +960 - a character holding both bases stacks them.
		[Fact]
		public void AggregateFeatBonuses_SumsRepeatedStatsAcrossFeats()
		{
			var effects = new[]
			{
				new FeatEffect { StatId = CharacterStatId.AttackRatingOneHandedEdged, Value = 720 },
				new FeatEffect { StatId = CharacterStatId.AttackRatingOneHandedBlunt, Value = 720 },
				new FeatEffect { StatId = CharacterStatId.AttackRatingOneHandedEdged, Value = 960 }
			};

			var totals = MaxStatAggregator.AggregateFeatBonuses(effects);

			Assert.Equal(1680, totals[CharacterStatId.AttackRatingOneHandedEdged]);
			Assert.Equal(720, totals[CharacterStatId.AttackRatingOneHandedBlunt]);
		}

		[Fact]
		public void AggregateFeatBonuses_IsEmptyForACharacterWithNoReadableEffects()
		{
			Assert.Empty(MaxStatAggregator.AggregateFeatBonuses([]));
		}

		// Gear and feats are separate sources of one additive total - the simulation takes a single table.
		[Fact]
		public void Combine_SumsTheSourcesPerStat()
		{
			var gear = new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.Strength] = 5,
				[CharacterStatId.Armor] = 100
			};
			var feats = new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.Strength] = 3,
				[CharacterStatId.HealthMax] = 500
			};

			var totals = MaxStatAggregator.Combine(gear, feats);

			Assert.Equal(8, totals[CharacterStatId.Strength]);
			Assert.Equal(100, totals[CharacterStatId.Armor]);
			Assert.Equal(500, totals[CharacterStatId.HealthMax]);
		}
	}
}
