using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.PacketAnalyzer.Tests
{
	// Class/level vitals defaults. Anchored on assassinl23 (Assassin lvl 23 -> sheet Health 1270, Mana 74,
	// Stamina 1148); the rogue archetype should land close. See docs/player-stats.md.
	public class CharacterMaxStatsTests
	{
		[Fact]
		public void Defaults_RogueAtLevel23_ApproximatesAssassinL23()
		{
			var stats = CharacterMaxStats.CreateDefaults(Class.Assassin, 23);

			// 200 + 23*47 = 1281 (sheet 1270), 200 + 23*41 = 1143 (sheet 1148), 50 + 23 = 73 (sheet 74)
			Assert.Equal(1281u, stats.Health);
			Assert.Equal(1143u, stats.Stamina);
			Assert.Equal(73u, stats.Mana);
		}

		[Fact]
		public void Defaults_MageHasMoreManaThanSoldier_AtSameLevel()
		{
			var mage = CharacterMaxStats.CreateDefaults(Class.Necromancer, 20);
			var soldier = CharacterMaxStats.CreateDefaults(Class.Guardian, 20);

			Assert.True(mage.Mana > soldier.Mana);
			Assert.True(soldier.Health > mage.Health);
		}

		[Fact]
		public void Defaults_LevelZero_TreatedAsLevelOne()
		{
			var atZero = CharacterMaxStats.CreateDefaults(Class.Assassin, 0);
			var atOne = CharacterMaxStats.CreateDefaults(Class.Assassin, 1);

			Assert.Equal(atOne.Health, atZero.Health);
			Assert.Equal(atOne.Mana, atZero.Mana);
		}

		[Fact]
		public void RegenerateMaxStats_UpdatesExistingRowInPlace()
		{
			// Seeded baseline (level-1 rogue default before the class is chosen).
			var character = new Character { Level = 1 };
			character.RegenerateMaxStats();
			var seeded = character.MaxStats;
			seeded!.Id = 42; // simulate a persisted row

			// The editor commits a mage class at a higher level -> stats regenerate into the SAME row.
			character.Class = Class.Necromancer;
			character.Level = 20;
			character.RegenerateMaxStats();

			Assert.Same(seeded, character.MaxStats); // same tracked row, not a replacement
			Assert.Equal(42u, character.MaxStats!.Id);
			Assert.Equal(CharacterMaxStats.CreateDefaults(Class.Necromancer, 20).Mana, character.MaxStats.Mana);
		}

		[Fact]
		public void RegenerateMaxStats_CreatesRow_WhenMissing()
		{
			var character = new Character { Class = Class.Guardian, Level = 5 };
			Assert.Null(character.MaxStats);

			character.RegenerateMaxStats();

			Assert.NotNull(character.MaxStats);
			Assert.Equal(CharacterMaxStats.CreateDefaults(Class.Guardian, 5).Health, character.MaxStats!.Health);
		}
	}
}
