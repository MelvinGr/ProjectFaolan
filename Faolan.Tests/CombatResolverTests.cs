using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// #395: melee damage is driven by the equipped weapon's min/max (RDB fields 286/285, decoded from the client's
	// GUIGetMin/MaxDamage), not a flat level value. This pins the resolver's weapon-vs-fallback behaviour.
	public class CombatResolverTests
	{
		[Fact]
		public void NoWeapon_FallsBackToTheLevelPlaceholder()
		{
			// Unarmed / no imported weapon damage == the old level curve (20 + level*5).
			Assert.Equal(20 + 10 * 5, CombatResolver.ResolveMeleeDamage(null, null, 10));
			Assert.Equal(CombatResolver.ResolveSpellDamage(0, 8), CombatResolver.ResolveMeleeDamage(null, null, 8));
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

		[Fact]
		public void Weapon_NeverDealsLessThanOne_AndLevelZeroMapsToOne()
		{
			Assert.True(CombatResolver.ResolveMeleeDamage(1, 1, 0) >= 1);
		}

		[Fact]
		public void ArmorMitigation_ReducesDamage_DiminishingAndNeverToZero()
		{
			const uint k = CombatResolver.ArmorHalfMitigation;

			Assert.Equal(100, CombatResolver.ApplyPhysicalMitigation(100, 0)); // no armor = no reduction
			Assert.Equal(50, CombatResolver.ApplyPhysicalMitigation(100, k)); // armor == K halves the hit
			Assert.True(CombatResolver.ApplyPhysicalMitigation(100, k * 9) >= 1); // heavy armor never fully negates
			Assert.True(CombatResolver.ApplyPhysicalMitigation(100, 4000) < CombatResolver.ApplyPhysicalMitigation(100, 1000)); // more armor = less damage
		}
	}
}
