using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// #3: EntityVitals.FromCharacter folds the equipped-gear vital bonuses (MaxStatAggregator display totals) onto the
	// class/level base max, and seeds current == max, so a +HP piece lifts the ceiling AND the bar starts full (the
	// health-bar-fill fix). Pure over a Character + a gear-bonus dict - no DB/session.
	public class EntityVitalsGearTests
	{
		[Fact]
		public void DeadCharacter_StaysDeadEvenWithGear()
		{
			var character = Guardian();
			character.IsDead = true;

			var vitals = EntityVitals.FromCharacter(character, new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.HealthMax] = 328
			}, ClassBase());

			Assert.Equal(BaseHp() + 328, vitals.MaxHealth); // max still gear-boosted
			Assert.Equal(0u, vitals.Health); // but a dead login stays dead
		}

		[Fact]
		public void GearHealthBonus_RaisesMaxAndFillsCurrent()
		{
			var gear = new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.HealthMax] = 328, // a +HP ring/set (the #3 motivating 320 -> 648 case)
				[CharacterStatId.StaminaMax] = 50
			};

			var vitals = EntityVitals.FromCharacter(Guardian(), gear, ClassBase());

			Assert.Equal(BaseHp() + 328, vitals.MaxHealth);
			Assert.Equal(BaseHp() + 328, vitals.Health); // current fills to the geared max, so the bar is full
			Assert.Equal(BaseStamina() + 50, vitals.MaxStamina);
		}

		[Fact]
		public void NoGear_UsesTheBaseMax()
		{
			var vitals = EntityVitals.FromCharacter(Guardian(), null, ClassBase());

			Assert.Equal(BaseHp(), vitals.MaxHealth);
			Assert.Equal(BaseHp(), vitals.Health); // seeds full
			Assert.Equal(BaseStamina(), vitals.MaxStamina);
		}

		// #450: a vitals push has to carry the MAX ids, not just the current ones. The bug this pins was a
		// currents-only sender used by every path that CHANGES a max: the client only ever learns a max when one is
		// sent, so it kept the value it was seeded with at world entry and a `.level 80` rendered `4285 / 1270` -
		// current at 3.4x the max. Asserting all SIX ids is what a currents-only block fails.
		[Fact]
		public void VitalsBlock_CarriesTheMaxIdsAndNotJustTheCurrentOnes()
		{
			var vitals = EntityVitals.FromCharacter(Guardian(), null, ClassBase());
			vitals.Health = 100; // current below max, so the two ids cannot be confused for each other
			vitals.Stamina = 50;

			var ids = vitals.ToVitalsBlock().Records;

			Assert.Equal(100u * 100, ids.Single(r => r.StatId == CharacterStatId.HealthCurrent).RawValue);
			// ALL THREE max ids carry the class BASE, not our ceiling: the client tops each up with its own
			// attribute/gear/feat terms, so sending the ceiling made it top up an already-topped-up number and draw
			// 1754 where retail drew 1270 (#580). Retail's AssassinL23 push proves the split in one packet - HpBase 77200
			// beside HpCur 127000, and ManaBase 0 beside ManaCur 7400.
			var classCurve = CharacterMaxStats.ClassBaseHundredths(ConanClass.Guardian, 4);
			Assert.Equal(classCurve.Health, ids.Single(r => r.StatId == CharacterStatId.HealthMax).RawValue);
			Assert.True(classCurve.Health / 100 < vitals.MaxHealth, "the wire base must sit below the server ceiling");
			Assert.Equal(50u * 100, ids.Single(r => r.StatId == CharacterStatId.StaminaCurrent).RawValue);
			Assert.Equal(classCurve.Stamina, ids.Single(r => r.StatId == CharacterStatId.StaminaMax).RawValue);
			// A Guardian has NO mana pool, and the max id says so with a 0 - which is what keeps the client from
			// drawing a mana bar for a class that has none, even when a worn item grants mana.
			Assert.Equal(0u, ids.Single(r => r.StatId == CharacterStatId.ManaMax).RawValue);
			Assert.Contains(ids, r => r.StatId == CharacterStatId.ManaCurrent);
			Assert.Contains(ids, r => r.StatId == CharacterStatId.ManaMax);
		}

		// The WHOLE sum, end to end, against the one character we have a retail capture AND a screenshot of: AssassinL23,
		// a level-23 Assassin. Their world-entry 0x96B8DC59 carries HpCur 127000 / StamCur 114800 / ManaCur 7400, and
		// the client's sheet draws those as the maxima - so this is what the server's ceiling has to be, or the sheet
		// reads a current that is not its max (health 1314 against a 1270 max, #580).
		//
		// The inputs are the character's own rows, stated here rather than read from the DB because faolan.sql
		// excludes characters (they are per-developer play state) - the ClassAttributeBases row for Assassin level 23
		// and the ItemStatModifiers of their ACTIVE weapon set's worn items.
		[Fact]
		public void AssassinL23_AtLevel23_MatchesTheCapturedRetailPools()
		{
			var character = new Character { Class = ConanClass.Assassin, Level = 23 };
			// ClassAttributeBases (34, 23) - the RAW WIRE amounts: Str 20, Con 17, Int 26, Wis 20, Dex 65.
			var classBase = new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.Strength] = 200,
				[CharacterStatId.Constitution] = 170,
				[CharacterStatId.Intelligence] = 260,
				[CharacterStatId.Wisdom] = 200,
				[CharacterStatId.Dexterity] = 650
			};
			// The worn gear, in the DISPLAY units MaxStatAggregator returns.
			var gear = new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.HealthMax] = 50,
				[CharacterStatId.StaminaMax] = 102,
				[CharacterStatId.ManaMax] = 74,
				[CharacterStatId.Constitution] = 39,
				[CharacterStatId.Intelligence] = 13,
				[CharacterStatId.Wisdom] = 6,
				[CharacterStatId.Dexterity] = 14
			};

			var vitals = EntityVitals.FromCharacter(character, gear, classBase);

			Assert.Equal(1270u, vitals.MaxHealth);
			Assert.Equal(1148u, vitals.MaxStamina);
			// An Assassin has no mana POOL, but a worn item still grants mana and retail still carries it as a
			// current - so the pool is 74, and it is the max ID that says the class has no bar (asserted below).
			Assert.Equal(74u, vitals.MaxMana);

			// ...and the wire says what retail's packet says: the class base under each max id, the full pool under
			// each current id. ManaMax 0 beside ManaCurrent 7400 is what keeps the client from drawing a mana bar for
			// a class that has none - sending our 74 ceiling there made one appear on the first vitals push.
			var records = vitals.ToVitalsBlock().Records;
			Assert.Equal(127000u, records.Single(r => r.StatId == CharacterStatId.HealthCurrent).RawValue);
			Assert.Equal(77200u, records.Single(r => r.StatId == CharacterStatId.HealthMax).RawValue);
			Assert.Equal(114800u, records.Single(r => r.StatId == CharacterStatId.StaminaCurrent).RawValue);
			Assert.Equal(73600u, records.Single(r => r.StatId == CharacterStatId.StaminaMax).RawValue);
			Assert.Equal(7400u, records.Single(r => r.StatId == CharacterStatId.ManaCurrent).RawValue);
			Assert.Equal(0u, records.Single(r => r.StatId == CharacterStatId.ManaMax).RawValue);
		}

		// The GetScale entries that were established from a RENDERED client value, pinned so a future edit cannot
		// quietly drop one. Each number here was read off the client, not inferred: wire 428500 drew as 4285 health,
		// a 10-wire class base drew as 1 Strength, and the feat `Lotus Blades` holds 3800 on WeaponDamageMelee where
		// the tooltip reads "+38%". The ×1 default is deliberately NOT asserted - an id missing from the table is
		// unmeasured, not known-unscaled (see the remarks on CharacterStatIdExtensions).
		[Theory]
		[InlineData(CharacterStatId.HealthMax, 100u)]
		[InlineData(CharacterStatId.StaminaMax, 100u)]
		[InlineData(CharacterStatId.ManaMax, 100u)]
		[InlineData(CharacterStatId.WeaponDamageMelee, 100u)]
		[InlineData(CharacterStatId.WeaponDamageRanged, 100u)]
		[InlineData(CharacterStatId.Strength, 10u)]
		[InlineData(CharacterStatId.Dexterity, 10u)]
		[InlineData(CharacterStatId.Ferocity, 10u)]
		[InlineData(CharacterStatId.EvadeChance, 100u)]
		[InlineData(CharacterStatId.ChanceOfOffhandAttack, 100u)]
		[InlineData(CharacterStatId.Immunity, 100u)]
		[InlineData(CharacterStatId.SpellFatalityChance, 100u)]
		[InlineData(CharacterStatId.BonusProtection, 100u)]
		[InlineData(CharacterStatId.FatalityChance, 1u)]        // ...while its near-namesake is x1
		[InlineData(CharacterStatId.HateModifier, 1u)]
		public void GetScale_KeepsTheInClientProvenScales(CharacterStatId stat, uint expected)
		{
			Assert.Equal(expected, stat.GetScale());
		}

		// The base is DERIVED from (Class, Level) (#578), so a fixture picks a level rather than injecting numbers.
		// Level 4 keeps the old shape of these cases - a few hundred HP, a stamina pool, no mana.
		private static Character Guardian()
		{
			return new Character { Class = ConanClass.Guardian, Level = 4 };
		}

		// The class/level attribute base (#444) a real character carries into the world. Every class's level-1 row is
		// a flat 10 wire (= 1 display) for all five, which is what the pools' attribute term scales off.
		private static Dictionary<CharacterStatId, int> ClassBase()
		{
			return new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.Strength] = 10,
				[CharacterStatId.Constitution] = 10,
				[CharacterStatId.Intelligence] = 10,
				[CharacterStatId.Wisdom] = 10,
				[CharacterStatId.Dexterity] = 10
			};
		}

		private static uint BaseHp()
		{
			return Guardian().MaxVitals(VitalAttributes.LevelOneBase).Health;
		}

		private static uint BaseStamina()
		{
			return Guardian().MaxVitals(VitalAttributes.LevelOneBase).Stamina;
		}
	}
}
