using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// Class/level vitals defaults. Anchored on AssassinL23 (Assassin lvl 23 -> captured Health 1270, Stamina 736,
	// Mana 0 - Assassins are stamina-only per the captures, memory class_resource_mana_stamina). See
	// docs/subsystems/player-stats.md.
	public class CharacterMaxStatsTests
	{
		// Level 0 THROWS; it used to be read as level 1. Every character row is allocated at level 1
		// (DatabaseRepository.CreateCharacter) and no shipped character is below it, so a 0 is a corrupt row - and
		// quietly serving it level-1 vitals made a broken character indistinguishable from a new one. Nothing in the
		// server depended on the old behaviour: this test was its only consumer.
		[Fact]
		public void Defaults_LevelZero_Throws()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => CharacterMaxStats.CreateDefaults(ConanClass.Assassin, 0));
		}

		[Fact]
		public void Defaults_MageHasMoreManaThanSoldier_AtSameLevel()
		{
			var mage = CharacterMaxStats.CreateDefaults(ConanClass.Necromancer, 20);
			var soldier = CharacterMaxStats.CreateDefaults(ConanClass.Guardian, 20);

			Assert.True(mage.Mana > soldier.Mana);
			Assert.True(soldier.Health > mage.Health);
		}

		[Fact]
		public void Defaults_RogueAtLevel23_ApproximatesAssassinL23()
		{
			var stats = CharacterMaxStats.CreateDefaults(ConanClass.Assassin, 23);

			// L1-anchored (Strie 98 HP / 236 Stam) + growth fitted to the L23 capture (AssassinL23 1270 HP / 736 Stam):
			// 98 + 22*53 = 1264 (captured 1270), 236 + 22*23 = 742 (captured 736). Assassin is stamina-only so
			// Mana = 0 (captured AssassinL23 Mana 0).
			Assert.Equal(1264u, stats.Health);
			Assert.Equal(742u, stats.Stamina);
			Assert.Equal(0u, stats.Mana);
		}

		// The ceiling FOLLOWS the level with nothing to keep in sync, because it is computed rather than stored
		// (#578). It used to be a persisted CharacterMaxStats row that only the `.level` GM command refreshed, so a
		// combat level-up wrote the new Level and left the row at its creation value - a level-2 necromancer came back
		// from a relog reading 89/955, its current health seeded from the level-1 ceiling.
		[Fact]
		public void MaxVitals_FollowTheLevel_WithNothingToKeepInSync()
		{
			var character = new Character { Class = ConanClass.Necromancer, Level = 1 };
			var atOne = character.MaxVitals();

			character.Level = 2;
			var atTwo = character.MaxVitals();

			Assert.Equal(CharacterMaxStats.CreateDefaults(ConanClass.Necromancer, 1).Health, atOne.Health);
			Assert.Equal(CharacterMaxStats.CreateDefaults(ConanClass.Necromancer, 2).Health, atTwo.Health);
			Assert.True(atTwo.Health > atOne.Health);
		}

		// Two characters of the same class and level have the SAME ceiling regardless of where they came from. An
		// imported character used to carry the capture's own vitals, so it disagreed with a created one by ~2x at L80;
		// deriving is what makes them identical - an imported character must be no different from a created one.
		[Fact]
		public void MaxVitals_AreIdenticalForImportedAndCreatedCharacters()
		{
			var created = new Character { Class = ConanClass.Necromancer, Level = 80 };
			var imported = new Character { Class = ConanClass.Necromancer, Level = 80 };

			Assert.Equal(created.MaxVitals().Health, imported.MaxVitals().Health);
			Assert.Equal(created.MaxVitals().Mana, imported.MaxVitals().Mana);
		}

		// A class with no such pool never gains one - the client hides a bar whose max is 0, so a necromancer must not
		// acquire stamina by levelling.
		[Fact]
		public void MaxVitals_DoNotGrowAPoolTheClassDoesNotHave()
		{
			var character = new Character { Class = ConanClass.Necromancer, Level = 40 };

			Assert.Equal(0u, character.MaxVitals().Stamina);
		}
	}
}
