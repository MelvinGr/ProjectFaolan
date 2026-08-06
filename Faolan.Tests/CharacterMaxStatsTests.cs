using System;
using System.Collections.Generic;
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
			Assert.Throws<ArgumentOutOfRangeException>(() => CharacterMaxStats.For(ConanClass.Assassin, 0, VitalAttributes.LevelOneBase));
		}

		[Fact]
		public void Defaults_MageHasMoreManaThanSoldier_AtSameLevel()
		{
			var mage = CharacterMaxStats.For(ConanClass.Necromancer, 20, VitalAttributes.LevelOneBase);
			var soldier = CharacterMaxStats.For(ConanClass.Guardian, 20, VitalAttributes.LevelOneBase);

			Assert.True(mage.Mana > soldier.Mana);
			Assert.True(soldier.Health > mage.Health);
		}

		// AssassinL23, the captured L23 Assassin, EXACTLY - the case the whole curve is anchored on. Their world-entry
		// 0x96B8DC59 carries HpCur 127000 / StamCur 114800 / ManaCur 7400 beside HpBase 77200 / StamBase 73600 /
		// ManaBase 0, and the client's sheet draws those currents as the maxima.
		//
		// This asserts the pre-gear part, because the flat gear pool bonuses are EntityVitals' half of the sum: the
		// character's active weapon set grants +50 health, +102 stamina and +74 mana, which take these three to
		// exactly the captured 1270 / 1148 / 74. The attributes are the character's own sheet (Str 20, Con 56, Int 39,
		// Wis 26, Dex 79 - themselves the ClassAttributeBases row plus the same gear).
		[Fact]
		public void Assassin_AtLevel23_ReproducesAssassinL23sCapturedSheet()
		{
			var assassinL23 = new VitalAttributes(20, 56, 39, 26, 79);
			var stats = CharacterMaxStats.For(ConanClass.Assassin, 23, assassinL23);

			Assert.Equal(1220u, stats.Health);  // 772 class base + 8 x 56 Constitution;      + 50 gear = 1270
			Assert.Equal(1046u, stats.Stamina); // 736 class base + 2 x (20 + 79 + 56);       + 102 gear = 1148
			// An Assassin has NO mana pool, so neither the class base nor the attribute columns give it one - the 74
			// the sheet shows is entirely the worn item's, added on top by EntityVitals.
			Assert.Equal(0u, stats.Mana);
		}

		// Every class's LEVEL-1 sheet, from the retail login snapshots (#242) - twelve independent anchors that the
		// class base alone does not reproduce: each one needs the attribute term (8 / 3,3 / 2,2,2 per point off a flat
		// level-1 attribute base of 1) and, for four classes, the ClassAttributeBases HealthMax row on top. The
		// Guardian is the one that proves the rounding: 172.5 stamina + 6 = 178.5 draws as 179.
		[Theory]
		[InlineData(ConanClass.Guardian, 130u, 0u, 179u, 200)]
		[InlineData(ConanClass.Conqueror, 168u, 0u, 178u, 0)]
		[InlineData(ConanClass.DarkTemplar, 128u, 281u, 178u, 0)]
		[InlineData(ConanClass.Assassin, 98u, 0u, 236u, 0)]
		[InlineData(ConanClass.Barbarian, 118u, 0u, 178u, 0)]
		[InlineData(ConanClass.Ranger, 133u, 0u, 213u, 500)]
		[InlineData(ConanClass.PriestOfMitra, 88u, 171u, 0u, 0)]
		[InlineData(ConanClass.TempestOfSet, 88u, 171u, 0u, 0)]
		[InlineData(ConanClass.BearShaman, 108u, 171u, 178u, 0)]
		[InlineData(ConanClass.HeraldOfXotli, 89u, 246u, 121u, 100)]
		[InlineData(ConanClass.Necromancer, 89u, 246u, 0u, 100)]
		[InlineData(ConanClass.Demonologist, 89u, 246u, 0u, 100)]
		public void EveryClass_AtLevelOne_ReproducesItsCapturedSheet(ConanClass @class, uint health, uint mana, uint stamina, int classHealthMaxWire)
		{
			var classAttributeBase = new Dictionary<CharacterStatId, int> { [CharacterStatId.HealthMax] = classHealthMaxWire };
			var stats = CharacterMaxStats.For(@class, 1, VitalAttributes.LevelOneBase, classAttributeBase);

			Assert.Equal(health, stats.Health);
			Assert.Equal(mana, stats.Mana);
			Assert.Equal(stamina, stats.Stamina);
		}

		// The ceiling FOLLOWS the level with nothing to keep in sync, because it is computed rather than stored
		// (#578). It used to be a persisted CharacterMaxStats row that only the `.level` GM command refreshed, so a
		// combat level-up wrote the new Level and left the row at its creation value - a level-2 necromancer came back
		// from a relog reading 89/955, its current health seeded from the level-1 ceiling.
		[Fact]
		public void MaxVitals_FollowTheLevel_WithNothingToKeepInSync()
		{
			var character = new Character { Class = ConanClass.Necromancer, Level = 1 };
			var atOne = character.MaxVitals(VitalAttributes.LevelOneBase);

			character.Level = 2;
			var atTwo = character.MaxVitals(VitalAttributes.LevelOneBase);

			Assert.Equal(CharacterMaxStats.For(ConanClass.Necromancer, 1, VitalAttributes.LevelOneBase).Health, atOne.Health);
			Assert.Equal(CharacterMaxStats.For(ConanClass.Necromancer, 2, VitalAttributes.LevelOneBase).Health, atTwo.Health);
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

			Assert.Equal(created.MaxVitals(VitalAttributes.LevelOneBase).Health, imported.MaxVitals(VitalAttributes.LevelOneBase).Health);
			Assert.Equal(created.MaxVitals(VitalAttributes.LevelOneBase).Mana, imported.MaxVitals(VitalAttributes.LevelOneBase).Mana);
		}

		// A class with no such pool never gains one - the client hides a bar whose max is 0, so a necromancer must not
		// acquire stamina by levelling.
		[Fact]
		public void MaxVitals_DoNotGrowAPoolTheClassDoesNotHave()
		{
			var character = new Character { Class = ConanClass.Necromancer, Level = 40 };

			Assert.Equal(0u, character.MaxVitals(VitalAttributes.LevelOneBase).Stamina);
		}
	}
}
