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

		[Fact]
		public void RegenerateMaxStats_CreatesRow_WhenMissing()
		{
			var character = new Character
			{
				Class = ConanClass.Guardian,
				Level = 5
			};
			Assert.Null(character.MaxStats);

			character.RegenerateMaxStats();

			Assert.NotNull(character.MaxStats);
			Assert.Equal(CharacterMaxStats.CreateDefaults(ConanClass.Guardian, 5).Health, character.MaxStats!.Health);
		}

		[Fact]
		public void RegenerateMaxStats_UpdatesExistingRowInPlace()
		{
			// The placeholder class every character row is allocated with (DatabaseRepository.CreateCharacter hands
			// out a Cimmerian barbarian), NOT an unset one - ConanClass has no zero member and there is deliberately
			// no fallback row, so a character with no class cannot produce vitals at all.
			var character = new Character
			{
				Class = ConanClass.Barbarian,
				Level = 1
			};
			character.RegenerateMaxStats();
			var seeded = character.MaxStats;

			// The editor commits a mage class at a higher level -> stats regenerate into the SAME row.
			character.Class = ConanClass.Necromancer;
			character.Level = 20;
			character.RegenerateMaxStats();

			Assert.Same(seeded, character.MaxStats); // same tracked row, not a replacement
			Assert.Equal(CharacterMaxStats.CreateDefaults(ConanClass.Necromancer, 20).Mana, character.MaxStats!.Mana);
		}
	}
}
