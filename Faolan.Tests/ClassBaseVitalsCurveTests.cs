using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The class BASE vitals curve, RDB 1000511 `HitsManaAndStamina_c` (Gitea #450 / #3 / #469).
	//
	// The gate is retail's own numbers: the 0x96B8DC59 stat push that follows a NewLevelIIR carries the base for
	// the level just reached, and every such push in the corpus is listed below with the capture it came from. A
	// curve fitted to L1 anchors (which is what this replaced) cannot pass this - the old growth values were out by
	// 50% on the Guardian and 70% on the Assassin.
	public class ClassBaseVitalsCurveTests
	{
		// Every captured (class, level) -> retail's own wire base, from the 0x96B8DC59 that follows the level-up.
		// Health / mana / stamina in the wire's x100 units, exactly as the packet carries them.
		[Theory]
		// Blablabla_soldier-guardian_create_random-actions-combat
		[InlineData(ConanClass.Guardian, 2, 16000, 0, 19780)]
		// Stihetfv_soldier-conqueror_login_compat_compeltefirstquest
		[InlineData(ConanClass.Conqueror, 2, 19600, 0, 19700)]
		// Strie_rogue-assassin_login_randomActions
		[InlineData(ConanClass.Assassin, 2, 12100, 0, 25300)]
		// Hoxxling_mage-heraldofxotli_login_gameplay (L2-L4) + _login_guild_invite_accept (L5)
		[InlineData(ConanClass.HeraldOfXotli, 2, 11300, 27000, 12800)]
		[InlineData(ConanClass.HeraldOfXotli, 3, 14600, 30000, 14100)]
		[InlineData(ConanClass.HeraldOfXotli, 4, 17900, 33000, 15400)]
		[InlineData(ConanClass.HeraldOfXotli, 5, 21200, 36000, 16700)]
		// Blablatest_mage-necromancer-create_casilda_quest_first + xxx_mage-necromancer_login_play_beach-to-enter-tortage_city
		[InlineData(ConanClass.Necromancer, 2, 10900, 27000, 0)]
		[InlineData(ConanClass.Necromancer, 3, 13800, 30000, 0)]
		[InlineData(ConanClass.Necromancer, 4, 16700, 33000, 0)]
		[InlineData(ConanClass.Necromancer, 5, 19600, 36000, 0)]
		// xxx_mage-demo_login_play_beach-to-enter-tortage_city
		[InlineData(ConanClass.Demonologist, 2, 10900, 27000, 0)]
		[InlineData(ConanClass.Demonologist, 3, 13800, 30000, 0)]
		[InlineData(ConanClass.Demonologist, 4, 16700, 33000, 0)]
		[InlineData(ConanClass.Demonologist, 5, 19600, 36000, 0)]
		public void ClassBase_ReproducesTheCapturedRetailLevelUpPush(ConanClass @class, ushort level, uint health, uint mana, uint stamina)
		{
			Assert.Equal((health, mana, stamina), CharacterMaxStats.ClassBaseHundredths(@class, level));
		}

		// The pool a class does NOT have stays 0 at every level, so the client keeps hiding that bar. Growth must not
		// resurrect it: a `base + growth * steps` written without the zero check would give a Necromancer stamina.
		[Theory]
		[InlineData(ConanClass.Necromancer, 80)]
		[InlineData(ConanClass.Demonologist, 80)]
		[InlineData(ConanClass.PriestOfMitra, 80)]
		[InlineData(ConanClass.TempestOfSet, 80)]
		public void ClassBase_KeepsAnAbsentPoolAtZero(ConanClass @class, ushort level)
		{
			Assert.Equal(0u, CharacterMaxStats.ClassBaseHundredths(@class, level).Stamina);
		}

		// Level 0 THROWS rather than being read as level 1. Every character row is allocated at level 1, so a 0 is a
		// corrupt row, and serving it a level-1 curve would make it indistinguishable from a new character.
		[Theory]
		[InlineData(ConanClass.Guardian)]
		[InlineData(ConanClass.Assassin)]
		public void ClassBase_LevelZeroThrows(ConanClass @class)
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => CharacterMaxStats.ClassBaseHundredths(@class, 0));
		}

		// And a class outside the twelve throws instead of borrowing another class's curve.
		[Fact]
		public void ClassBase_AnUnknownClassThrows()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => CharacterMaxStats.ClassBaseHundredths((ConanClass)0, 10));
		}

		// The base is NOT the displayed max, which is the whole reason this curve is not simply wired into
		// CharacterMaxStats. AssassinL23's captured sheet reads 1270 health; the class base at 23 is 772. The 498
		// difference is the client's own gear/feat/attribute contribution (#450), and asserting the gap here is what
		// stops someone "fixing" the discrepancy by swapping one for the other.
		[Fact]
		public void ClassBase_IsWellBelowTheCapturedSheet_ForAGearedCharacter()
		{
			var (health, _, _) = CharacterMaxStats.ClassBaseHundredths(ConanClass.Assassin, 23);

			Assert.Equal(77200u, health);
			Assert.True(health < 127000, "the class base must stay below AssassinL23's captured sheet health of 1270");
		}
	}
}
