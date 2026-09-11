using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Two rules about what the server tells a player about their own pools, both measured off the corpus.
	//
	// 1. A push carries what moved. On the player's own handle retail's `StatUpdateIIR` is a single-stat block far
	//    more often than not - `[0x01FA]` x1171, `[0x001B]` x211, `[0x01F8]` x121 against only x12 for the
	//    three-current form, which is the shape it uses about other entities (x4,676). Sending all three currents on
	//    every push does not match that.
	//
	// 2. Health regen is bounded by a share of the MAX pool. Retail's own-handle `RegenIIR` ticks at a median 1.01s
	//    carrying a median 3.90% of max health per second (p25 3.07%, p75 4.63%). That is identifiable rather than an
	//    artifact of one pool size: the captured pools span 17.2x and the share-of-pool reading is the tighter of the
	//    two candidate rules (cv 0.49 against 0.75 for a flat HP/s).
	public class VitalsPushShapeTests
	{
		// A level-4 character: 202 max health, the case that exposed this. The flat `HealthFlatRate` alone is 10% of
		// that pool per second, so the cap is what stands between a starter and a bar that refills in nine seconds.
		private const uint LowLevelPool = 202;

		[Fact]
		public void TheFirstPushStatesEveryCurrent()
		{
			var vitals = Vitals();

			var block = vitals.ToChangedCurrentsBlock();

			Assert.Equal(3, block.Records.Count);
		}

		// The one that matters in a fight: HP regen is halted in combat but stamina's is not, so the regen tick keeps
		// firing every second. Before this, each of those ticks restated `HealthCurrent` - announcing a health change
		// that had not happened, once a second, for the whole fight.
		[Fact]
		public void ATickWhereOnlyStaminaMovedDoesNotRestateHealth()
		{
			var vitals = Vitals();
			vitals.ToChangedCurrentsBlock(); // the entry push

			vitals.Stamina -= 5;
			var block = vitals.ToChangedCurrentsBlock();

			Assert.Single(block.Records);
			Assert.Equal(CharacterStatId.StaminaCurrent, block.Records[0].StatId);
		}

		[Fact]
		public void APushWithNothingMovedIsEmpty()
		{
			var vitals = Vitals();
			vitals.ToChangedCurrentsBlock();

			Assert.Empty(vitals.ToChangedCurrentsBlock().Records);
		}

		// The client's copy is reseeded by the world-entry snapshot, not by a push, so our record of what it holds is
		// stale across an entry - and a current that happens to match would otherwise never be restated.
		[Fact]
		public void ForgettingTheLastPushRestatesEverything()
		{
			var vitals = Vitals();
			vitals.ToChangedCurrentsBlock();

			vitals.ForgetPushedCurrents();

			Assert.Equal(3, vitals.ToChangedCurrentsBlock().Records.Count);
		}

		// The regression this exists for: a flat rate does not scale down. `HealthFlatRate` is 20 HP/s read off the
		// client's own stat 278, which is 10% of a level-4's pool every second.
		[Fact]
		public void HealthRegenOnAStarterPoolIsHeldToRetailsShareOfTheMaxPool()
		{
			const uint constitution = 7, strength = 7;

			var rate = RegenRates.HealthPerSecond(constitution, strength, LowLevelPool);

			Assert.True(rate / LowLevelPool <= RegenRates.HealthRegenMaxFractionPerSecond,
				$"{rate:F1} HP/s is {100 * rate / LowLevelPool:F1}% of a {LowLevelPool} pool per second; retail's measured ceiling is "
				+ $"{100 * RegenRates.HealthRegenMaxFractionPerSecond:F2}%");
		}

		// ...and the cap must not touch the band the corpus does cover. A level-80 Necromancer computes ~106 HP/s on a
		// 2,371 pool = 4.5%, just under the ceiling, against the 92 HP/s the corpus measures for it - so the attribute
		// formula is left exactly as it was verified.
		[Fact]
		public void TheCapDoesNotBiteWhereTheCorpusVerifiedTheFormula()
		{
			const uint pool = 2371, constitution = 375, strength = 200;

			var capped = RegenRates.HealthPerSecond(constitution, strength, pool);
			var uncapped = RegenRates.HealthPerSecond(constitution, strength);

			Assert.Equal(uncapped, capped, 3);
		}

		private static EntityVitals Vitals()
		{
			return new EntityVitals
			{
				Health = LowLevelPool,
				MaxHealth = LowLevelPool,
				Stamina = 100,
				MaxStamina = 100,
				Mana = 50,
				MaxMana = 50
			};
		}
	}
}
