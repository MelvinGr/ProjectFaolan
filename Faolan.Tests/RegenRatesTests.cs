using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// #442: the out-of-combat regen rates. These are ACCEPTANCE gates, not sanity checks - every expected number here is
	// a RegenIIR (0x28D0AC99) delta retail itself sent, read straight out of the corpus, which carries the per-second
	// rate ×100. So the formulas are pinned to two decimals with no rounding slack, and a coefficient drifting by even
	// 0.01 fails.
	//
	// [V1: NecroL80_mage-necromancer_login_gameplay*.tshark.json / DemoL80_mage-demonologist_login*.tshark.json /
	//  AssassinL23_rogue-assassin_login*.tshark.json - RegenIIR bodies; attribute scores from the same characters' in-client
	//  Attributes sheet (screenshots, 2026-07-26). Coefficients: RDB 1000516 TWK_OOCHealthRegen{Con,Str}Modifier = 0.15,
	//  TWK_OOCStaminaRegen{Con,Str,Dex}Modifier = 0.15, TWK_OOCManaRegen{Int,Wis}Modifier = 0.38.]
	public class RegenRatesTests
	{
		// The captured hp deltas are 8675 (NecroL80) and 3140 (AssassinL23) - exactly ×100 of these rates.
		[Theory]
		[InlineData(375u, 70u, 8675)] // NecroL80, L80 Necromancer
		[InlineData(345u, 70u, 8225)] // DemoL80, L80 Demonologist - see the buff-term test below for why 8225
		[InlineData(56u, 20u, 3140)] // AssassinL23, L23 Assassin
		public void HealthPerSecond_MatchesTheCapturedRegenIirDelta(uint constitution, uint strength, int wireDelta)
		{
			Assert.Equal(wireDelta, (int)(RegenRates.HealthPerSecond(constitution, strength) * 100));
		}

		// DemoL80's dominant captured delta is 20353 and NecroL80's is 20803 - both are the base rate plus the SAME 12128,
		// which is what identifies DemoL80's (never-captured-bare) base as 8225 and rules out a per-character extra.
		[Fact]
		public void HealthPerSecond_TheBoostedCapturesAreTheBaseRatePlusOneSharedTerm()
		{
			var necrol80 = (int)(RegenRates.HealthPerSecond(375, 70) * 100);
			var demol80 = (int)(RegenRates.HealthPerSecond(345, 70) * 100);

			Assert.Equal(20803 - necrol80, 20353 - demol80);
		}

		// AssassinL23 is the only captured character with a stamina pool; its captured stamina delta is 3325. Dexterity joins
		// Constitution and Strength here (three tweaks at 0.15), and the flat term is 10 rather than health's 20.
		[Fact]
		public void StaminaPerSecond_MatchesTheCapturedRegenIirDelta()
		{
			Assert.Equal(3325, (int)(RegenRates.StaminaPerSecond(56, 20, 79) * 100));
		}

		// Hakotep's captured stamina deltas (3115, 3970) were never paired with a screenshot of its sheet, so they can
		// only confirm the SHAPE - and they do: both invert to a whole attribute sum, which a wrong modifier or flat term
		// would not produce.
		[Theory]
		[InlineData(3115, 141u)]
		[InlineData(3970, 198u)]
		public void StaminaPerSecond_InvertsCapturedDeltasToWholeAttributeSums(int wireDelta, uint attributeSum)
		{
			Assert.Equal(wireDelta, (int)(RegenRates.StaminaPerSecond(attributeSum, 0, 0) * 100));
		}

		// AssassinL23's captured mana delta is 2470 = 0.38 × (Int 39 + Wis 26) exactly, with NOTHING added - the one pool
		// whose flat term is zero.
		[Fact]
		public void ManaPerSecond_MatchesTheCapturedRegenIirDelta()
		{
			Assert.Equal(2470, (int)(RegenRates.ManaPerSecond(39, 26) * 100));
			Assert.Equal(0.0, RegenRates.ManaFlatRate);
		}

		// The flat terms are what a naive reading of the character sheet gets wrong: the sheet prints the stat with an
		// INTEGER divide by 100 (client FUN_016d9cb0, case 0x116), so 86.75 and 82.25 both display as their floor. Fitting
		// the displayed integers as if they were rounded yields 19.5; truncation yields 20, and only 20 reproduces the
		// captured deltas. This pins that reasoning so it cannot silently regress.
		[Fact]
		public void TheFlatTerms_AreWhatTruncationDemands_NotWhatRoundingWould()
		{
			Assert.Equal(20.0, RegenRates.HealthFlatRate);
			Assert.Equal(10.0, RegenRates.StaminaFlatRate);

			// Each captured rate must FLOOR to the integer the sheet showed (86 / 82 / 31), not round to it.
			Assert.Equal(86, (int)RegenRates.HealthPerSecond(375, 70));
			Assert.Equal(82, (int)RegenRates.HealthPerSecond(345, 70));
			Assert.Equal(31, (int)RegenRates.HealthPerSecond(56, 20));
		}

		// The rate is per SECOND (median captured RegenIIR gap = 1.00 s), so a tick's gain scales with its own elapsed
		// time and a fractional rate is not silently floored away over a longer tick.
		[Fact]
		public void Gain_ScalesWithElapsedTime_AndNeverGoesNegative()
		{
			var perSecond = RegenRates.HealthPerSecond(56, 20); // 31.40

			Assert.Equal(31u, RegenRates.Gain(perSecond, 1.0));
			Assert.Equal(157u, RegenRates.Gain(perSecond, 5.0)); // 157.0 - the fraction accumulates, not lost per tick
			Assert.Equal(0u, RegenRates.Gain(perSecond, 0));
			Assert.Equal(0u, RegenRates.Gain(0, 10));
		}

		// A character with NO attribute scores still regenerates: the flat terms alone. This is today's live behaviour for
		// an ungeared character, and the shape that keeps a pool from stalling (Gitea #444 - the class/level attribute
		// base is client-side and not yet modelled, so the scores the server knows are gear + buffs only).
		[Fact]
		public void WithNoAttributes_TheFlatTermsStillTick()
		{
			Assert.Equal(RegenRates.HealthFlatRate, RegenRates.HealthPerSecond(0, 0));
			Assert.Equal(RegenRates.StaminaFlatRate, RegenRates.StaminaPerSecond(0, 0, 0));
			Assert.Equal(0.0, RegenRates.ManaPerSecond(0, 0)); // mana has no flat term, so a no-Int character gains none
		}
	}
}
