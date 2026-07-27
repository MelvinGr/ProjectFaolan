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
			var character = Guardian(320, 179);
			character.IsDead = true;

			var vitals = EntityVitals.FromCharacter(character, new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.HealthMax] = 328
			});

			Assert.Equal(648u, vitals.MaxHealth); // max still gear-boosted
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

			var vitals = EntityVitals.FromCharacter(Guardian(320, 179), gear);

			Assert.Equal(648u, vitals.MaxHealth); // 320 + 328
			Assert.Equal(648u, vitals.Health); // current fills to the geared max, so the bar is full
			Assert.Equal(229u, vitals.MaxStamina); // 179 + 50
		}

		[Fact]
		public void NoGear_UsesTheBaseMax()
		{
			var vitals = EntityVitals.FromCharacter(Guardian(320, 179));

			Assert.Equal(320u, vitals.MaxHealth);
			Assert.Equal(320u, vitals.Health); // seeds full
			Assert.Equal(179u, vitals.MaxStamina);
		}

		// #450: a vitals push has to carry the MAX ids, not just the current ones. The bug this pins was a
		// currents-only sender used by every path that CHANGES a max: the client only ever learns a max when one is
		// sent, so it kept the value it was seeded with at world entry and a `.level 80` rendered `4285 / 1270` -
		// current at 3.4x the max. Asserting all SIX ids is what a currents-only block fails.
		[Fact]
		public void VitalsBlock_CarriesTheMaxIdsAndNotJustTheCurrentOnes()
		{
			var vitals = EntityVitals.FromCharacter(Guardian(320, 179));
			vitals.Health = 100; // current below max, so the two ids cannot be confused for each other
			vitals.Stamina = 50;

			var ids = vitals.ToVitalsBlock().Records;

			Assert.Equal(100u * 100, ids.Single(r => r.StatId == CharacterStatId.HealthCurrent).RawValue);
			Assert.Equal(320u * 100, ids.Single(r => r.StatId == CharacterStatId.HealthMax).RawValue);
			Assert.Equal(50u * 100, ids.Single(r => r.StatId == CharacterStatId.StaminaCurrent).RawValue);
			Assert.Equal(179u * 100, ids.Single(r => r.StatId == CharacterStatId.StaminaMax).RawValue);
			Assert.Contains(ids, r => r.StatId == CharacterStatId.ManaCurrent);
			Assert.Contains(ids, r => r.StatId == CharacterStatId.ManaMax);
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
		[InlineData(CharacterStatId.FatalityChance, 1u)]        // ...while its near-namesake is x1
		[InlineData(CharacterStatId.HateModifier, 1u)]
		public void GetScale_KeepsTheInClientProvenScales(CharacterStatId stat, uint expected)
		{
			Assert.Equal(expected, stat.GetScale());
		}

		private static Character Guardian(uint hp, uint stamina)
		{
			return new Character
			{
				Class = ConanClass.Guardian,
				Level = 1,
				MaxStats = new CharacterMaxStats
				{
					Health = hp,
					Stamina = stamina,
					Mana = 0
				}
			};
		}
	}
}
