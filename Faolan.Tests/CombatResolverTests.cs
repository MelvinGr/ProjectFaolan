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
	}
}
