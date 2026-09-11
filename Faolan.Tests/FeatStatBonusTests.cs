using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Feat stat bonuses feeding the live vitals. The gate that matters here is the scale: `FeatEffect.Value` is the
	// raw wire value, the same as `ItemStatModifier.Amount`, so the two aggregators share a convention and both
	// divide by the stat's scale.
	//
	// The proof is that the same bytes are read twice. `ClassAttributeBase.Amount` and `FeatEffect.Value` are
	// both `ItemTemplateStatModifiers.Modifier.WireAmount`, off the same `<Class> Class Feat NNN` templates -
	// `Necromancer Class Feat 071` carries HealthMax 38,600 and the `ClassAttributeBases` row for (Necromancer, 80)
	// is 38,600 - and `CharacterMaxStats.Pool` adds that in hundredths and divides by 100 for +386 health, which is
	// the reading verified against every captured pool.
	//
	// These tests asserted the opposite, and that is the point worth keeping: they were written from the same
	// belief as the code, so they could only ever agree with it. `Vitality`'s top rank ramps 10,000 -> 80,000, which
	// as a display value is +80,000 health against a level-80 pool of ~6,300.
	public class FeatStatBonusTests
	{
		// `Modify` - the one kind that is a plain flat delta, and so what a fixture must carry to be summed at all
		//. Written out here rather than reached for from the aggregator, so a change to that set fails the
		// tests instead of silently agreeing with them.
		private const uint Modify = 53045;

		// A carrier level for the cases that are not about the level ramp. Any value does for a flat kind - the ramp
		// cases below pick their own.
		private const uint Level = 80;

		// The shipped rows, at the scale the wire states them: `Necromancer Class Feat 071`'s HealthMax is 38,600
		// and lands as +386 health - the same +386 `CharacterMaxStats` documents for a level-80 Necromancer, from the
		// same number in `ClassAttributeBases`. Strength x10 likewise: the class track's 700 is a display 70.
		[Fact]
		public void AggregateFeatBonuses_AppliesTheWireScale()
		{
			var effects = new[]
			{
				new FeatEffect { StatId = CharacterStatId.Strength, EffectKind = Modify, Value = 700 },
				new FeatEffect { StatId = CharacterStatId.HealthMax, EffectKind = Modify, Value = 38600 }
			};

			var totals = MaxStatAggregator.AggregateFeatBonuses(effects, Level);

			Assert.Equal(70, totals[CharacterStatId.Strength]);
			Assert.Equal(386, totals[CharacterStatId.HealthMax]);
		}

		// And the magnitudes that settle the scale with no inference at all. `Barbarian: HP (1) 10` carries
		// 26,800 and `Vitality`'s top rank ramps to 80,000; a level-80's whole pool is ~6,300, so neither can be a
		// display value. As wire values they are +268 and +800 health, which is what a health feat grants.
		[Theory]
		[InlineData(26800, 268)] // Barbarian: HP (1) 10
		[InlineData(80000, 800)] // Vitality, the ramp's high endpoint
		[InlineData(6200, 62)]   // Barbarian: HP (1) 1
		public void AggregateFeatBonuses_HealthValuesAreImpossibleReadAsDisplay(int wire, int display)
		{
			var effects = new[] { new FeatEffect { StatId = CharacterStatId.HealthMax, EffectKind = Modify, Value = wire } };

			Assert.Equal(display, MaxStatAggregator.AggregateFeatBonuses(effects, Level)[CharacterStatId.HealthMax]);
		}

		// The real shape from the catalog: "Soldier: 1HB and 1HE 1" grants +720 to two attack ratings at once, and its
		// rank 2 grants +960 - a character holding both bases stacks them.
		//
		// The stacking happens in wire units and the scale is applied once per stat, so this is
		// `(720 + 960) / 100`, not `720/100 + 960/100`. On these two rows the answers agree; on rows whose remainders
		// add up they do not, which is why the sum comes first.
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

			Assert.Equal(16, totals[CharacterStatId.AttackRatingOneHandedEdged]);
			Assert.Equal(7, totals[CharacterStatId.AttackRatingOneHandedBlunt]);
		}

		[Fact]
		public void AggregateFeatBonuses_IsEmptyForACharacterWithNoReadableEffects()
		{
			Assert.Empty(MaxStatAggregator.AggregateFeatBonuses([], Level));
		}

		// The feat region is heterogeneous and the kind is what says how to read Value, exactly as it is for the
		// spell region BuffStatEffects reads. A percent record added as flat turns "+2% Critical Rating" into +200,
		// and a `SetFlag` record - which every class feat carries as (864 Player Flags, 5) - is a flag write that two
		// feats would sum into flag 10.
		[Theory]
		[InlineData(53067u)] // ModifyAsRating - a percent, stored x100
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

		// The level-ramp family (53220 `InterpolatedModify` and its twin 53221) is not a flat delta: the row's Value is
		// only the ramp's low endpoint and the whole bonus is `lerp(Value@LowLevel, HighValue@HighLevel)` over the
		// carrier's level. The shipped `Battle Roar` row is the fixture - +887 health at 20, +17500 at 80 - and
		// the gate is that the same row is worth different amounts to different characters, which is precisely what
		// summing the low endpoint got wrong.
		//
		// The expectations are the ramp's wire value divided by HealthMax's scale of 100 - the ramp is
		// interpolated in wire units and divided once at the end, so the half-way row is `9193 / 100`, not
		// `(887/100 + 17500/100) / 2`. Rounding the endpoints first would drift across the ramp.
		[Theory]
		[InlineData(1u, 8)]      // below the ramp: clamped to the low endpoint (887 -> 8)
		[InlineData(20u, 8)]     // at the low level
		[InlineData(50u, 91)]    // half way: 887 + 0.5 x (17500 - 887) = 9193 -> 91
		[InlineData(80u, 175)]   // at the high level (17500 -> 175)
		[InlineData(200u, 175)]  // above the ramp: clamped to the high endpoint
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
		// the alternative would be dropping the bonus entirely.
		[Fact]
		public void AggregateFeatBonuses_FallsBackToTheLowEndpointWhenTheRampColumnsAreMissing()
		{
			var effects = new[] { new FeatEffect { StatId = CharacterStatId.HealthMax, EffectKind = 53220, Value = 887 } };

			Assert.Equal(8, MaxStatAggregator.AggregateFeatBonuses(effects, 80)[CharacterStatId.HealthMax]);
		}

		// 53068 `InterpolatedModifyAsRating` is the percent twin of the ramp family - its endpoints are percentages
		// x100, so it is no more summable than the plain percent kind 53067 that sits in the theory above. Summing it
		// as flat would add its raw hundredths to a rating on the one shipped row.
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
