using System.Collections.Generic;
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
