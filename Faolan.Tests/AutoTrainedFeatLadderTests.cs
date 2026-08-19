using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// The auto-trained feat ladder (#863). The one property worth gating is that the rule is a PICK and not a sum:
	// the client's ladders state a character's whole score at each rung, so summing the rungs a level-80 has passed
	// turns a skill of 900 into 4500.
	//
	// The shapes below are the real ones out of the shipped table, named where they came from - a rule argued from
	// invented data is a rule that agrees with itself.
	public class AutoTrainedFeatLadderTests
	{
		private const uint Modify = 53045;

		private static AutoTrainedFeatLadder.Rung Rung(uint level, uint entryId, CharacterStatId stat, int value)
		{
			return new AutoTrainedFeatLadder.Rung(level, entryId, new FeatEffect { StatId = stat, EffectKind = Modify, Value = value });
		}

		// `Climbing Skill` -> `Basic Climbing` -> `Advanced Climbing` -> `Expert Climbing`: four differently NAMED
		// feats, one stat (819 Max Climbing), one ladder reading 100/200/…/900 across levels 1..78. A level-80 has
		// passed every rung and the client's answer is 900.
		[Fact]
		public void Select_TakesTheHighestRungReached_NotTheSum()
		{
			var reached = new[]
			{
				Rung(1, 10, CharacterStatId.MaxClimbing, 100),
				Rung(10, 11, CharacterStatId.MaxClimbing, 200),
				Rung(23, 12, CharacterStatId.MaxClimbing, 300),
				Rung(78, 13, CharacterStatId.MaxClimbing, 900)
			};

			var picked = AutoTrainedFeatLadder.Select(reached);

			Assert.Equal(900, Assert.Single(picked).Value);
		}

		// ⭐ `Pet Mastery` hands out TWO ranks at the same level (ranks 7 and 8 both arrive at 20), so the level alone
		// does not order them. The client's own row id does - 715380..715387 ascend with rank - and rank 8 is the one
		// that applies.
		[Fact]
		public void Select_BreaksALevelTieOnTheClientsRowId()
		{
			var reached = new[]
			{
				Rung(20, 715386, CharacterStatId.PetPoints, 7),
				Rung(20, 715387, CharacterStatId.PetPoints, 8)
			};

			Assert.Equal(8, Assert.Single(AutoTrainedFeatLadder.Select(reached)).Value);
		}

		// Each stat runs its own ladder and they do not interfere: `Pet Mastery` rank 8 grants Pet Points 8 AND a Pet
		// Damage Coefficient of -4400, and the winner has to be picked per stat, not once for the whole set.
		[Fact]
		public void Select_PicksPerStat()
		{
			var reached = new[]
			{
				Rung(5, 715380, CharacterStatId.PetPoints, 1),
				Rung(5, 715380, CharacterStatId.PetDamageCoefficient, -550),
				Rung(20, 715387, CharacterStatId.PetPoints, 8),
				Rung(20, 715387, CharacterStatId.PetDamageCoefficient, -4400),
				Rung(1, 613812, CharacterStatId.NonCombatHealthRegen, 2000)
			};

			var totals = MaxStatAggregatorTotals(reached);

			Assert.Equal(8, totals[CharacterStatId.PetPoints]);
			Assert.Equal(-4400, totals[CharacterStatId.PetDamageCoefficient]);
			Assert.Equal(2000, totals[CharacterStatId.NonCombatHealthRegen]);
		}

		// One feat may state the same stat several times - `Fury of the Slave` carries Crossbow Range Modifier 200 as
		// three separate GameAction elements. Those are one feat's effect list and stack exactly as a trained feat's
		// does; only the RUNGS are exclusive.
		[Fact]
		public void Select_KeepsEveryRowOfTheWinningRung()
		{
			var reached = new[]
			{
				Rung(1, 613812, CharacterStatId.CrossbowRangeModifier, 200),
				Rung(1, 613812, CharacterStatId.CrossbowRangeModifier, 200),
				Rung(1, 613812, CharacterStatId.CrossbowRangeModifier, 200)
			};

			Assert.Equal(600, MaxStatAggregatorTotals(reached)[CharacterStatId.CrossbowRangeModifier]);
		}

		// ⛔ The exclusion the whole derivation rests on: the class PROGRESSION track is applied through
		// ClassAttributeBases, from the same client templates, so it must never come through here as well. A tree-id
		// filter cannot express it - `Pet Mastery` shares tree 9000 with the track - which is why the marker is a
		// name and why that name is stated once.
		[Fact]
		public void ClassProgressionTrackMarker_MatchesTheTrackAndNothingElse()
		{
			Assert.Contains(FeatRules.ClassProgressionTrackMarker, "Necromancer Class Feat 001");
			Assert.Contains(FeatRules.ClassProgressionTrackMarker, "Ranger Class Feat 071");
			Assert.DoesNotContain(FeatRules.ClassProgressionTrackMarker, "Pet Mastery");
			Assert.DoesNotContain(FeatRules.ClassProgressionTrackMarker, "Fury of the Slave");
			Assert.DoesNotContain(FeatRules.ClassProgressionTrackMarker, "Expert Climbing");
			Assert.DoesNotContain(FeatRules.ClassProgressionTrackMarker, "Mage");
		}

		private static System.Collections.Generic.IReadOnlyDictionary<CharacterStatId, int> MaxStatAggregatorTotals(AutoTrainedFeatLadder.Rung[] reached)
		{
			return Faolan.GameServer.Combat.MaxStatAggregator.AggregateFeatBonuses(AutoTrainedFeatLadder.Select(reached), 80);
		}
	}
}
