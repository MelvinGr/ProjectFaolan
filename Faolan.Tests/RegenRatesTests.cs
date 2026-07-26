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

		// AssassinL23's captured mana delta is 2470 = 0.38 × (Int 39 + Wis 26) exactly, with NOTHING added.
		//
		// The zero flat term is not an accident of this one character: the client seeds stat 280 (Non Combat Mana Regen)
		// to 2000 for a class WITH a mana base (Herald of Xotli, Necromancer - whose class template then takes it to
		// 2200) and never writes it at all for this Assassin, whose 74-point mana pool therefore regenerates on
		// trickledown alone. Both L80 mage classes' bases read 2200 (a 2000 seed plus 200 from their class template).
		[Fact]
		public void ManaPerSecond_MatchesTheCapturedRegenIirDelta()
		{
			Assert.Equal(2470, (int)(RegenRates.ManaPerSecond(39, 26) * 100));
			Assert.Equal(0.0, RegenRates.ManaFlatRate);
		}

		// The SECOND, independent confirmation of the mana branch - and the one that shows its base is class data rather
		// than universally zero. Hakotep, a level 24 Herald of Xotli: the client computes raw Int 1190 / Wis 340, i.e.
		// display 118 / 33, and its class mana base (stat 280) is 2000 = display 20.00. Retail's captured mana delta for
		// that character is 7738, and 0.38 × 151 + 20 = 77.38 hits it exactly.
		//
		// Two characters, two different class bases (0 and 20.00), both exact - so the 0.38 modifier and the
		// base-plus-trickledown shape are pinned for mana the same way they are for health and stamina.
		[Fact]
		public void ManaPerSecond_WithAClassBase_MatchesTheHeraldOfXotliCapture()
		{
			Assert.Equal(7738, (int)(RegenRates.ManaPerSecond(118, 33, 20.0) * 100));
		}

		// ⚠️ NOT the OOC rate: the level 80 casters' most common mana deltas are exactly 1% and 2% of their max mana per
		// second (NecroL80 6523 and 13046 on a 6523 pool; DemoL80 5830 on 5830), and none of their deltas inverts to a whole
		// attribute sum under any observed class base. Those belong to some other mana channel - plausibly the
		// TWK_NaturalManaRegen* in-combat twin - and must not be read as evidence about this formula.
		[Theory]
		[InlineData(6523, 6523, 1.0)]
		[InlineData(13046, 6523, 2.0)]
		[InlineData(5830, 5830, 1.0)]
		public void TheLevel80CasterManaDeltas_AreAPercentageOfMaxMana_NotThisFormula(int wireDelta, int maxManaDisplay, double percentOfMax)
		{
			Assert.Equal(percentOfMax, wireDelta / 100.0 / maxManaDisplay * 100, 2);
		}

		// The attribute scores every expectation above is built on are the CLIENT'S OWN, read back off the
		// ClientInterface channel for this same character (raw = display × 10 + 10): Str 210, Con 570, Int 400, Wis 270,
		// Dex 800. That is what makes the three matches above verifications rather than a fit through assumed inputs -
		// the inputs are retail-independent client computations, the outputs are retail's captured deltas.
		[Theory]
		[InlineData(210, 20u)]
		[InlineData(570, 56u)]
		[InlineData(400, 39u)]
		[InlineData(270, 26u)]
		[InlineData(800, 79u)]
		public void TheAttributeInputs_AreTheClientsOwnValues_AtTheWireScale(int wire, uint display)
		{
			Assert.Equal(display, (uint)((wire - 10) / 10)); // the client's (raw - 10) / 10 IS the display score
			Assert.Equal(wire, (int)(display * 10 + 10)); // and back, which is WorldEntity.AttributeWireScore
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

		// #444: the decoded class BASE, at the scale the rates consume. A level-23 Assassin's base records are Str 200 /
		// Con 170 / Dex 650 at the WIRE scale; the client adds them to its seeded 10, so the display score contributed by
		// the base is record / 10.
		//
		// ⚠️ Deliberately NOT asserting base + gear == the character sheet. For our level-23 Assassin that gives Dex
		// 65 + 16 = 81 where the sheet read 79, a 2-point gap - most likely because the sheet screenshot and the current
		// CharacterEquipment rows are from different moments, but it is NOT established, so it is written down rather
		// than encoded as an expectation. The rates themselves are pinned against retail's captured deltas below and do
		// not depend on resolving it.
		[Theory]
		[InlineData(200, 20u)] // Strength
		[InlineData(170, 17u)] // Constitution
		[InlineData(650, 65u)] // Dexterity - the Assassin's primary
		public void TheDecodedClassBase_ConvertsToDisplayUnits(int wireRecord, uint displayContribution)
		{
			Assert.Equal(displayContribution, (uint)(wireRecord / 10));
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
