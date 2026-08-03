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
		// `Modify` - the one kind that is a plain flat delta, and so what a fixture must carry to be summed at all
		// (#597). Written out here rather than reached for from the aggregator, so a change to that set fails the
		// tests instead of silently agreeing with them.
		private const uint Modify = 53045;

		// A carrier level for the cases that are not about the level ramp. Any value does for a flat kind - the ramp
		// cases below pick their own.
		private const uint Level = 80;

		[Fact]
		public void AggregateFeatBonuses_DoesNotApplyTheWireScale()
		{
			// Strength scales x10 on the wire and HealthMax x100 - if this divided like the gear path, +3 Strength
			// would land as 0 and +500 health as 5.
			var effects = new[]
			{
				new FeatEffect { StatId = CharacterStatId.Strength, EffectKind = Modify, Value = 3 },
				new FeatEffect { StatId = CharacterStatId.HealthMax, EffectKind = Modify, Value = 500 }
			};

			var totals = MaxStatAggregator.AggregateFeatBonuses(effects, Level);

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
				new FeatEffect { StatId = CharacterStatId.AttackRatingOneHandedEdged, EffectKind = Modify, Value = 720 },
				new FeatEffect { StatId = CharacterStatId.AttackRatingOneHandedBlunt, EffectKind = Modify, Value = 720 },
				new FeatEffect { StatId = CharacterStatId.AttackRatingOneHandedEdged, EffectKind = Modify, Value = 960 }
			};

			var totals = MaxStatAggregator.AggregateFeatBonuses(effects, Level);

			Assert.Equal(1680, totals[CharacterStatId.AttackRatingOneHandedEdged]);
			Assert.Equal(720, totals[CharacterStatId.AttackRatingOneHandedBlunt]);
		}

		[Fact]
		public void AggregateFeatBonuses_IsEmptyForACharacterWithNoReadableEffects()
		{
			Assert.Empty(MaxStatAggregator.AggregateFeatBonuses([], Level));
		}

		// #597: the feat region is heterogeneous and the KIND is what says how to read Value, exactly as it is for the
		// spell region BuffStatEffects reads. A percent record added as flat turns "+2% Critical Rating" into +200,
		// and a `SetFlag` record - which every class feat carries as (864 Player Flags, 5) - is a flag write that two
		// feats would sum into flag 10.
		[Theory]
		[InlineData(53067u)] // ModifyAsRating - a PERCENT, stored x100
		[InlineData(53139u)] // SetFlag        - a flag write, not a bonus
		[InlineData(53026u)] // Set            - an assignment, not an addition
		public void AggregateFeatBonuses_IgnoresTheKindsThatAreNotAFlatDelta(uint kind)
		{
			var effects = new[]
			{
				new FeatEffect { StatId = CharacterStatId.CriticalRating, EffectKind = Modify, Value = 7 },
				new FeatEffect { StatId = CharacterStatId.CriticalRating, EffectKind = kind, Value = 200 }
			};

			Assert.Equal(7, MaxStatAggregator.AggregateFeatBonuses(effects, Level)[CharacterStatId.CriticalRating]);
		}

		// The level-ramp family (53220 `InterpolatedModify` and its twin 53221) is NOT a flat delta: the row's Value is
		// only the ramp's low endpoint and the whole bonus is `lerp(Value@LowLevel, HighValue@HighLevel)` over the
		// carrier's level (#599). The shipped `Battle Roar` row is the fixture - +887 health at 20, +17500 at 80 - and
		// the gate is that the SAME row is worth different amounts to different characters, which is precisely what
		// summing the low endpoint got wrong.
		[Theory]
		[InlineData(1u, 887)]     // below the ramp: clamped to the low endpoint
		[InlineData(20u, 887)]    // at the low level
		[InlineData(50u, 9193)]   // half way: 887 + 0.5 x (17500 - 887)
		[InlineData(80u, 17500)]  // at the high level
		[InlineData(200u, 17500)] // above the ramp: clamped to the high endpoint
		public void AggregateFeatBonuses_InterpolatesTheRampedKindsOverTheCarriersLevel(uint level, int expected)
		{
			var effects = new[]
			{
				new FeatEffect
				{
					StatId = CharacterStatId.HealthMax, EffectKind = 53220, Value = 887, HighValue = 17500, LowLevel = 20, HighLevel = 80
				}
			};

			Assert.Equal(expected, MaxStatAggregator.AggregateFeatBonuses(effects, level)[CharacterStatId.HealthMax]);
		}

		// A ramped row whose ramp columns are absent - imported before they existed, or a parse that could not read
		// all four slots - falls back to the low endpoint. That is an under-grant, which is exactly what shipped
		// before #599; the alternative would be dropping the bonus entirely.
		[Fact]
		public void AggregateFeatBonuses_FallsBackToTheLowEndpointWhenTheRampColumnsAreMissing()
		{
			var effects = new[] { new FeatEffect { StatId = CharacterStatId.HealthMax, EffectKind = 53220, Value = 887 } };

			Assert.Equal(887, MaxStatAggregator.AggregateFeatBonuses(effects, 80)[CharacterStatId.HealthMax]);
		}

		// 53068 `InterpolatedModifyAsRating` is the PERCENT twin of the ramp family - its endpoints are percentages
		// x100, so it is no more summable than the plain percent kind 53067 that sits in the theory above. It used to
		// be summed as flat, which on the one shipped row would have added its raw hundredths to a rating.
		[Fact]
		public void AggregateFeatBonuses_IgnoresTheInterpolatedPercentKind()
		{
			var effects = new[]
			{
				new FeatEffect
				{
					StatId = CharacterStatId.CriticalRating, EffectKind = 53068, Value = 200, HighValue = 500, LowLevel = 20, HighLevel = 80
				}
			};

			Assert.Empty(MaxStatAggregator.AggregateFeatBonuses(effects, 80));
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
