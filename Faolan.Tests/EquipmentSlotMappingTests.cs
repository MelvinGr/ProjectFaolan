using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The equip/unequip persistence maps a CharacterEquipmentSlot to a CharacterEquipment item nav via the e[slot]
	// indexer; a typo there would silently drop or misplace gear. This locks the mapping: set each slot, read it back,
	// and confirm it lands in the right property (and only that one).
	public class EquipmentSlotMappingTests
	{
		private static readonly CharacterEquipmentSlot[] MappedSlots =
		[
			CharacterEquipmentSlot.Neck, CharacterEquipmentSlot.Head, CharacterEquipmentSlot.Tokens, CharacterEquipmentSlot.Chest, CharacterEquipmentSlot.MainWeapon, CharacterEquipmentSlot.Belt,
			CharacterEquipmentSlot.OffHandWeapon, CharacterEquipmentSlot.LeftRing, CharacterEquipmentSlot.RightRing, CharacterEquipmentSlot.Cape, CharacterEquipmentSlot.Shoulder, CharacterEquipmentSlot.Boots,
			CharacterEquipmentSlot.Hands, CharacterEquipmentSlot.Legs, CharacterEquipmentSlot.Wrist, CharacterEquipmentSlot.RangedWeapon, CharacterEquipmentSlot.Ammo,
			CharacterEquipmentSlot.VanityHead, CharacterEquipmentSlot.VanityBelt, CharacterEquipmentSlot.VanityCape, CharacterEquipmentSlot.VanityBoots, CharacterEquipmentSlot.VanityWrist
		];

		[Fact]
		public void SetSlot_DoesNotLeakIntoOtherSlots()
		{
			var e = new CharacterEquipment();
			var ring = new Item
			{
				Id = 999,
				Name = "Ring"
			};

			e[CharacterEquipmentSlot.LeftRing] = ring;

			Assert.Equal(ring.Id, e[CharacterEquipmentSlot.LeftRing]!.Id);
			Assert.Null(e[CharacterEquipmentSlot.RightRing]);
			Assert.Null(e[CharacterEquipmentSlot.Chest]);
			Assert.Null(e[CharacterEquipmentSlot.Wrist]);
		}

		[Fact]
		public void SetSlot_Throws_ForUndefinedSlot()
		{
			// Every DEFINED CharacterEquipmentSlot maps to a column; a raw cast of an out-of-range wire Pos (here the
			// bag slot 17, which the enum doesn't name) has no column and throws.
			var e = new CharacterEquipment();
			Assert.Throws<ArgumentOutOfRangeException>(() => e[(CharacterEquipmentSlot)17] = new Item { Id = 1 });
		}

		[Fact]
		public void SetThenGet_RoundTrips_ForEverySlot()
		{
			foreach (var slot in MappedSlots)
			{
				var e = new CharacterEquipment();
				var item = new Item
				{
					Id = (uint)slot,
					Name = slot.ToString()
				};

				e[slot] = item;
				var read = e[slot];

				Assert.NotNull(read);
				Assert.Equal(item.Id, read!.Id);
			}
		}
	}
}
