using Faolan.Core.Database.Tables;
using System.Linq;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// #400: only the ACTIVE weapon set's two slots feed the gear-stat aggregation input (StatBearingItemIds), because a
	// character can carry both a melee and a ranged set at once and counting all four would double-count. Armour +
	// jewellery always apply; the weapon set is selected by the drawn loadout. Slot->column map is CharacterEquipmentSlot's.
	public class CharacterEquipmentStatSlotsTests
	{
		// A character wearing one item in every stat-bearing slot, with distinct ids so each slot is identifiable.
		private static CharacterEquipment FullyGeared() => new()
		{
			// armour + jewellery
			NeckId = 1, HeadId = 2, ChestId = 3, BeltId = 4, RingLeftId = 5, RingRightId = 6, CloakId = 7,
			ShoulderId = 8, BootsId = 9, HandsId = 10, LegsId = 11, WristId = 12,
			// weapon set 1 (slots 6 / 8)
			MainHandLeftId = 100, OffHandLeftId = 101,
			// weapon set 2 (slots 20 / 21)
			MainHandRightId = 200, OffHandRightId = 201
		};

		private static readonly uint[] Armour = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

		[Fact]
		public void NoActiveSet_YieldsArmourOnly_NoWeapons()
		{
			var ids = FullyGeared().StatBearingItemIds();

			Assert.Equal(Armour, ids.ToArray());
		}

		[Fact]
		public void PrimarySet_AddsSet1Weapons_ExcludesSet2()
		{
			var ids = new System.Collections.Generic.HashSet<uint>(FullyGeared().StatBearingItemIds(WeaponSet.Primary));

			Assert.Contains(100u, ids); // set-1 MainHandLeft
			Assert.Contains(101u, ids); // set-1 OffHandLeft
			Assert.DoesNotContain(200u, ids); // set-2 main NOT counted
			Assert.DoesNotContain(201u, ids); // set-2 off NOT counted
			Assert.All(Armour, a => Assert.Contains(a, ids));
		}

		[Fact]
		public void SecondarySet_AddsSet2Weapons_ExcludesSet1()
		{
			var ids = new System.Collections.Generic.HashSet<uint>(FullyGeared().StatBearingItemIds(WeaponSet.Secondary));

			Assert.Contains(200u, ids); // set-2 MainHandRight (often a bow/crossbow)
			Assert.Contains(201u, ids); // set-2 OffHandRight (often ammo)
			Assert.DoesNotContain(100u, ids); // set-1 main NOT counted
			Assert.DoesNotContain(101u, ids); // set-1 off NOT counted
		}

		[Fact]
		public void EmptyWeaponSlots_AreSkipped()
		{
			// A character with only a set-1 weapon equipped, drawing set 1: only its one weapon id appears.
			var eq = new CharacterEquipment { ChestId = 3, MainHandLeftId = 100 };

			Assert.Equal([3u, 100u], eq.StatBearingItemIds(WeaponSet.Primary).ToArray());
			// Drawing a second set it does not own yields no weapon ids (nothing to double-count, nothing crashes).
			Assert.Equal([3u], eq.StatBearingItemIds(WeaponSet.Secondary).ToArray());
		}
	}
}
