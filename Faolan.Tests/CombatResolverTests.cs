using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// #395: melee damage is driven by the equipped weapon's min/max (RDB fields 286/285, decoded from the client's
	// GUIGetMin/MaxDamage), not a flat level value. This pins the resolver's weapon-vs-fallback behaviour.
	public class CombatResolverTests
	{
		[Fact]
		public void ArmorMitigation_ReducesDamage_DiminishingAndNeverToZero()
		{
			const uint k = CombatResolver.ArmorHalfMitigation;

			Assert.Equal(100, CombatResolver.ApplyPhysicalMitigation(100, 0)); // no armor = no reduction
			Assert.Equal(50, CombatResolver.ApplyPhysicalMitigation(100, k)); // armor == K halves the hit
			Assert.True(CombatResolver.ApplyPhysicalMitigation(100, k * 9) >= 1); // heavy armor never fully negates
			Assert.True(CombatResolver.ApplyPhysicalMitigation(100, 4000) < CombatResolver.ApplyPhysicalMitigation(100, 1000)); // more armor = less damage
		}

		[Fact]
		public void CombatRating_AddsAFlatMeleeBonus()
		{
			var baseDmg = CombatResolver.ResolveMeleeDamage(1000, 5000, 5);
			var geared = CombatResolver.ResolveMeleeDamage(1000, 5000, 5, 50, 200); // +200 Combat Rating

			Assert.Equal(baseDmg + 200 / CombatResolver.CombatRatingDamageDivisor, geared);
			Assert.Equal(baseDmg, CombatResolver.ResolveMeleeDamage(1000, 5000, 5)); // no rating = no bonus
		}

		[Fact]
		public void Crit_ChanceRisesWithCriticalRating_IsCapped_AndDamageScalesWithCritDamageRating()
		{
			Assert.Equal(CombatResolver.BaseCritChancePercent, CombatResolver.CritChancePercent(0)); // no gear = base chance
			Assert.True(CombatResolver.CritChancePercent(1000) > CombatResolver.CritChancePercent(100)); // more Critical Rating = more crits
			Assert.Equal(CombatResolver.MaxCritChancePercent, CombatResolver.CritChancePercent(1_000_000)); // capped

			// A roll below the chance crits; at/above does not.
			Assert.True(CombatResolver.IsCriticalHit(0, CombatResolver.BaseCritChancePercent - 1));
			Assert.False(CombatResolver.IsCriticalHit(0, CombatResolver.BaseCritChancePercent));

			Assert.Equal(150, CombatResolver.ApplyCritDamage(100, 0)); // base 1.5x with no Critical Damage Rating
			Assert.Equal(245, CombatResolver.ApplyCritDamage(100, 95)); // +95 Critical Damage Rating -> 2.45x
			Assert.True(CombatResolver.ApplyCritDamage(0, 0) >= 1); // floored at 1
		}

		[Fact]
		public void MagicMitigation_UsesProtection_ItsOwnHalfPoint()
		{
			const uint k = CombatResolver.ProtectionHalfMitigation;
			Assert.Equal(100, CombatResolver.ApplyMagicMitigation(100, 0)); // no Protection = no reduction
			Assert.Equal(50, CombatResolver.ApplyMagicMitigation(100, k)); // Protection == K halves the hit
			Assert.True(CombatResolver.ApplyMagicMitigation(100, k * 9) >= 1); // never fully negates
		}

		[Fact]
		public void NoWeapon_FallsBackToTheLevelPlaceholder()
		{
			// Unarmed / no imported weapon damage == the old level curve (20 + level*5).
			Assert.Equal(20 + 10 * 5, CombatResolver.ResolveMeleeDamage(null, null, 10));
			Assert.Equal(CombatResolver.ResolveSpellDamage(0, 8), CombatResolver.ResolveMeleeDamage(null, null, 8));
		}

		[Fact]
		public void SpellDamage_AddsMagicDamageFlat()
		{
			var baseDmg = CombatResolver.ResolveSpellDamage(123, 10);
			var geared = CombatResolver.ResolveSpellDamage(123, 10, 52); // +52 Magic Damage (a Royal Signet's worth)

			Assert.Equal(baseDmg + 52, geared);
			Assert.Equal(baseDmg, CombatResolver.ResolveSpellDamage(123, 10)); // no gear = base
		}

		// #697: the percent step the spell-line feat boost and the combo feat bonuses share, so the two cannot drift
		// on rounding or on the floor rule. The unit is hundredths of a percent - 3000 is +30%.
		[Theory]
		[InlineData(100, 0, 100)] // no bonus is the identity, not a x1 round-trip
		[InlineData(100, 3000, 130)]
		[InlineData(100, 10000, 200)]
		[InlineData(100, -5000, 50)]
		public void PercentBonus_ScalesAFinishedNumber(int damage, int percentHundredths, int expected)
		{
			Assert.Equal(expected, CombatResolver.ApplyPercentBonus(damage, percentHundredths));
		}

		// A debuff-shaped negative floors at 1: an attack that stops dealing damage entirely would read as a miss,
		// which is a different thing from a heavily debuffed hit.
		[Fact]
		public void PercentBonus_NeverTakesADamagingHitToZero()
		{
			Assert.Equal(1, CombatResolver.ApplyPercentBonus(100, -10000));
			Assert.Equal(1, CombatResolver.ApplyPercentBonus(100, -50000));
		}

		// The client's own rule, recovered from FUN_01118ce0 and confirmed independently by RDB field 847 -
		// docs/rdb/spell-scaling.md. Wisdom for every caster; Intelligence for the three classes the client
		// special-cases; and the rate is TKW_MagicalDpsAbilityModifier = 0.6, truncated (the client's CVTTSS2SI).
		[Theory]
		[InlineData(ConanClass.Necromancer, CharacterStatId.Intelligence)]
		[InlineData(ConanClass.Demonologist, CharacterStatId.Intelligence)]
		[InlineData(ConanClass.HeraldOfXotli, CharacterStatId.Intelligence)]
		[InlineData(ConanClass.PriestOfMitra, CharacterStatId.Wisdom)]
		[InlineData(ConanClass.TempestOfSet, CharacterStatId.Wisdom)]
		[InlineData(ConanClass.DarkTemplar, CharacterStatId.Wisdom)]
		[InlineData(ConanClass.Assassin, CharacterStatId.Wisdom)]
		public void MagicDamage_ScalesOffTheClassAttribute_AtTheClientsTweakedRate(ConanClass @class, CharacterStatId expected)
		{
			Assert.Equal(expected, CombatResolver.MagicDamageAttribute(@class));

			// Intelligence 110, Wisdom 40 - deliberately unequal, so the two branches cannot both pass by accident.
			var attributes = new VitalAttributes(Strength: 20, Constitution: 30, Intelligence: 110, Wisdom: 40, Dexterity: 50);
			var score = expected == CharacterStatId.Intelligence ? 110u : 40u;

			Assert.Equal(score * 600 / 1000, CombatResolver.ResolveMagicDamage(0, @class, attributes));
			// The worn-gear rating is added FLAT on top of the attribute term, not scaled by it.
			Assert.Equal(52 + score * 600 / 1000, CombatResolver.ResolveMagicDamage(52, @class, attributes));
		}

		// RDB field 847 is a per-spell OVERRIDE of the class rule - the six Demonologist spells that scale off Wisdom
		// where their class scales off Intelligence. 0 means "no override", NOT "no scaling".
		[Fact]
		public void MagicDamage_SpellScalingAttribute_OverridesTheClassRule()
		{
			var attributes = new VitalAttributes(Strength: 200, Constitution: 30, Intelligence: 110, Wisdom: 40, Dexterity: 50);

			Assert.Equal(CharacterStatId.Intelligence, CombatResolver.MagicDamageAttribute(ConanClass.Demonologist));
			Assert.Equal(CharacterStatId.Wisdom,
				CombatResolver.MagicDamageAttribute(ConanClass.Demonologist, (uint)CharacterStatId.Wisdom));

			Assert.Equal(110u * 600 / 1000, CombatResolver.ResolveMagicDamage(0, ConanClass.Demonologist, attributes));
			Assert.Equal(40u * 600 / 1000,
				CombatResolver.ResolveMagicDamage(0, ConanClass.Demonologist, attributes, (uint)CharacterStatId.Wisdom));
			// The Conqueror's five Strength-scaled records: an attribute outside the caster pair still resolves.
			Assert.Equal(200u * 600 / 1000,
				CombatResolver.ResolveMagicDamage(0, ConanClass.Conqueror, attributes, (uint)CharacterStatId.Strength));
		}

		[Fact]
		public void Weapon_NeverDealsLessThanOne_AndLevelZeroMapsToOne()
		{
			Assert.True(CombatResolver.ResolveMeleeDamage(1, 1, 0) >= 1);
		}

		[Fact]
		public void Weapon_RollInterpolatesMinToMax()
		{
			// roll 0 -> min end, 100 -> max end, 50 -> midpoint (the default).
			var atMin = CombatResolver.ResolveMeleeDamage(1000, 5000, 1, 0);
			var atMax = CombatResolver.ResolveMeleeDamage(1000, 5000, 1, 100);
			var atMid = CombatResolver.ResolveMeleeDamage(1000, 5000, 1);

			Assert.Equal(1000 / CombatResolver.WeaponDamageScale + 1, atMin);
			Assert.Equal(5000 / CombatResolver.WeaponDamageScale + 1, atMax);
			Assert.Equal(CombatResolver.ResolveMeleeDamage(1000, 5000, 1), atMid); // default == midpoint
			Assert.True(atMin < atMax);
		}

		[Fact]
		public void Weapon_UsesScaledAveragePlusLevel_SoBetterWeaponsHitHarder()
		{
			// Acolyte's Dagger 257/1802 -> avg 1029 /100 = 10, + level 1 = 11.
			var dagger = CombatResolver.ResolveMeleeDamage(257, 1802, 1);
			// Bloodsilver Greatsword 2393/13559 -> avg 7976 /100 = 79, + level 1 = 80.
			var greatsword = CombatResolver.ResolveMeleeDamage(2393, 13559, 1);

			Assert.Equal((257 + 1802) / 2 / CombatResolver.WeaponDamageScale + 1, dagger);
			Assert.Equal((2393 + 13559) / 2 / CombatResolver.WeaponDamageScale + 1, greatsword);
			Assert.True(greatsword > dagger); // the whole point: the weapon drives the damage
		}

		// The CLIENT'S OWN mitigation curve, pinned to its GameCode/ArmorMitigation trace (2026-07-26). NecroL80: level 80,
		// gear armour 2017, raw Strength 710 (displayed 71). Both trace points are reproduced, which is what makes this a
		// verification rather than a plausible formula:
		//   with ApprenticeLevel unset (the bug): adjusted -168246 -> Final Mitigation 0.938747
		//   with ApprenticeLevel = level (fixed):  adjusted    2157 -> Final Mitigation 0.0982419
		// TWO characters, independently: TotalArmorValue = armour + AttributeBonus, where the bonus is
		// ((rawStrength - 10) / 10) * 2.0 = 140 for both (each has raw Strength 710 / displayed 71).
		[Theory]
		[InlineData(2017u, 0.0982419)] // NecroL80      - client: TotalArmorValue 2157, Final Mitigation 0.0982419
		[InlineData(1903u, 0.0930497)] // DemoL80      - client: TotalArmorValue 2043, Final Mitigation 0.0930497
		public void MitigationFraction_ReproducesTheClientsOwnTrace(uint armour, double expected)
		{
			Assert.Equal(expected, CombatResolver.MitigationFraction(armour, 80, 710), 6);
		}

		[Fact]
		public void MitigationFraction_IsLevelNormalised_SoTheSameArmourMitigatesLessAsYouLevel()
		{
			// This is the property the old `K / (armor + K)` constant could not express at all.
			var atL20 = CombatResolver.MitigationFraction(2017, 20, 710);
			var atL80 = CombatResolver.MitigationFraction(2017, 80, 710);

			Assert.True(atL20 > atL80, $"L20 {atL20:F4} should mitigate more than L80 {atL80:F4}");
		}

		[Fact]
		public void MitigationFraction_SwitchesToTheAsymptoticBranchOnceArmourPassesTheLevelTarget()
		{
			// target = level * 137.225; below it the client uses (adjusted/target) * 0.5, at/above it
			// adjusted/(target+adjusted) - which is continuous at the boundary (both give 0.5).
			const uint level = 10;
			var target = (uint)(level * CombatResolver.TargetArmorPerLevel);

			Assert.Equal(0.5, CombatResolver.MitigationFraction(target, level), 3);
			Assert.True(CombatResolver.MitigationFraction(target * 4, level) > 0.5);
			Assert.True(CombatResolver.MitigationFraction(target * 4, level) < CombatResolver.MaxMitigationFraction + 1e-9);
		}

		[Fact]
		public void ApplyPhysicalMitigation_WithNoLevel_KeepsTheOldCurve()
		{
			// Callers that do not know the defender's level (NPC-on-NPC, tests) must not silently get 0 mitigation.
			Assert.Equal(50, CombatResolver.ApplyPhysicalMitigation(100, CombatResolver.ArmorHalfMitigation));
		}

		[Fact]
		public void ApplyPhysicalMitigation_NeverFullyNegatesAHit()
		{
			Assert.True(CombatResolver.ApplyPhysicalMitigation(10, 999999, 80, 5000) >= 1);
		}

		[Fact]
		public void MitigationFraction_ZeroArmourOrZeroLevel_IsNoMitigation()
		{
			Assert.Equal(0, CombatResolver.MitigationFraction(0, 80, 710));
			Assert.Equal(0, CombatResolver.MitigationFraction(2017, 0, 710));
		}

		// The MAGICAL twin, pinned to the two magical lookups in the same client trace. NecroL80, level 80, so the target
		// is 80 * 46.0625 = 3685; the client logs no intermediates on this branch, so these were solved from the final
		// fraction and cross-checked against the logged AdjustedRoll - and both `adjusted` values land on EXACTLY the
		// Protection scores from her character sheet, which is what confirms the branch and the target.
		[Theory]
		[InlineData(80u, 342u, 0.0464043, 90.7191)] // NecroL80  L80 - Protection Cold / Electrical / Fire
		[InlineData(80u, 55u, 0.00746269, 98.5075)] // NecroL80  L80 - Protection Holy / Unholy
		[InlineData(80u, 270u, 0.036635, 92.673)] // DemoL80  L80 - a second character, different Protection
		[InlineData(80u, 119u, 0.0161465, 96.7707)] // DemoL80  L80
		// LEVEL 23 - the case that actually tests the level NORMALISATION, since the target is level * 46.0625
		// (1059.4375 here, not 3685). The four L80 rows above cannot distinguish a per-level target from a constant.
		[InlineData(23u, 13u, 0.0061355, 98.7729)] // AssassinL23 L23 assassin
		// NB AssassinL23's other protection group solves to 19.5 - FRACTIONAL - so it cannot be expressed here: the client's
		// protection is not necessarily integral (roll 98.1594 -> final 0.009203 -> adjusted 19.500). Our uint-based
		// API therefore rounds; a defender with a half-point of protection mitigates a hair less than the client says.
		public void ProtectionMitigationFraction_ReproducesTheClientsMagicalLookups(uint level, uint protection, double expectedFraction, double expectedAdjustedRoll)
		{
			var fraction = CombatResolver.ProtectionMitigationFraction(protection, level);

			Assert.Equal(expectedFraction, fraction, 5);
			// the client's AdjustedRoll is 100 - (adjusted / target * 100), which the same numbers must reproduce
			// 4 decimals, matching the precision the client PRINTS: the exact value is 98.507462686..., logged as 98.5075.
			Assert.Equal(expectedAdjustedRoll, 100.0 - protection / (level * CombatResolver.TargetProtectionPerLevel) * 100.0, 4);
		}

		[Fact]
		public void ProtectionMitigatesHarderThanArmourAtEqualRating_BecauseItsPerLevelTargetIsLower()
		{
			// 46.0625 vs 137.225: the same rating is worth ~3x as much against magic as against melee.
			Assert.True(CombatResolver.ProtectionMitigationFraction(500, 80) > CombatResolver.MitigationFraction(500, 80));
		}

		[Fact]
		public void ApplyMagicMitigation_WithNoLevel_KeepsTheOldCurve()
		{
			Assert.Equal(50, CombatResolver.ApplyMagicMitigation(100, CombatResolver.ProtectionHalfMitigation));
		}

		// #629/#630: the mana a cast costs is the client's own formula over RDB field 132, not a level placeholder,
		// and which of its two modes applies is STATED by bit 2 of the record's flags word rather than guessed from
		// the magnitude. The numbers are the shipped catalog's - Ice Strike (Rank 6) carries 9 with the bit set, Dark
		// Knowledge (Rank 4) 42200 with it clear - so a regression shows up as a real spell costing the wrong thing.
		[Theory]
		[InlineData(0, 6523, true, 0)] // no field / a literal 0 = free, which is retail's own reading
		[InlineData(9, 6523, true, 587)] // Ice Strike (Rank 6): 9% of the pool
		[InlineData(5, 6523, true, 326)] // Pestilential Blast (Rank 6)
		[InlineData(100, 6523, true, 6523)] // the whole pool
		[InlineData(42200, 6523, false, 422)] // Dark Knowledge (Rank 4): flat, in wire hundredths
		[InlineData(12800, 900, false, 128)] // ...its rank 2, and a flat cost does NOT scale with the pool
		[InlineData(12800, 6523, false, 128)]
		// The 215 records where the flag and the old `<= 100 is a percent` heuristic disagree are all of this shape:
		// a tiny FLAT cost the magnitude rule read as a percentage. 1 means 0.01 mana, not 1% of the pool.
		[InlineData(1, 6523, false, 0)]
		public void SpellManaCost_ReadsFieldInTheModeTheRecordStates(int field, uint maxMana, bool isPercent, uint expected)
		{
			Assert.Equal(expected, CombatResolver.ResolveSpellManaCost((ushort)field, maxMana, isPercent));
		}

		[Fact]
		public void SpellManaCost_PercentModeScalesWithTheCasterSoOneSpellCostsMoreAsThePoolGrows()
		{
			// The whole point of the percent mode: Ice Strike is 9% of the pool at every level, so it costs a
			// levelling caster progressively more mana without the catalog carrying a per-level number.
			Assert.Equal(43u, CombatResolver.ResolveSpellManaCost(9, 485, true));
			Assert.Equal(234u, CombatResolver.ResolveSpellManaCost(9, 2604, true));
			Assert.Equal(587u, CombatResolver.ResolveSpellManaCost(9, 6523, true));
		}
	}
}
