using Faolan.GameServer.Inventory;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The equip/unequip persistence maps a CharacterEquipmentSlot to a CharacterEquipment item nav by switch; a typo
	// there would silently drop or misplace gear. This locks the mapping: set each slot, read it back, and confirm
	// it lands in the right property (and only that one).
	public class EquipmentSlotMappingTests
	{
		private static readonly CharacterEquipmentSlot[] MappedSlots =
		[
			CharacterEquipmentSlot.Neck, CharacterEquipmentSlot.Head, CharacterEquipmentSlot.Tokens, CharacterEquipmentSlot.Chest, CharacterEquipmentSlot.MainWeapon, CharacterEquipmentSlot.Belt,
			CharacterEquipmentSlot.OffHandWeapon, CharacterEquipmentSlot.LeftRing, CharacterEquipmentSlot.RightRing, CharacterEquipmentSlot.Cape, CharacterEquipmentSlot.Shoulder, CharacterEquipmentSlot.Boots,
			CharacterEquipmentSlot.Hands, CharacterEquipmentSlot.Legs, CharacterEquipmentSlot.Wrist, CharacterEquipmentSlot.RangedWeapon, CharacterEquipmentSlot.Ammo
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

			EquipmentService.SetEquipmentSlot(e, CharacterEquipmentSlot.LeftRing, ring);

			Assert.Equal(ring.Id, EquipmentService.GetEquipmentSlot(e, CharacterEquipmentSlot.LeftRing)!.Id);
			Assert.Null(EquipmentService.GetEquipmentSlot(e, CharacterEquipmentSlot.RightRing));
			Assert.Null(EquipmentService.GetEquipmentSlot(e, CharacterEquipmentSlot.Chest));
			Assert.Null(EquipmentService.GetEquipmentSlot(e, CharacterEquipmentSlot.Wrist));
		}

		[Fact]
		public void SetSlot_ReturnsFalse_ForUnmappedSlot()
		{
			var e = new CharacterEquipment();
			Assert.False(EquipmentService.SetEquipmentSlot(e, CharacterEquipmentSlot.VanityHead, new Item { Id = 1 }));
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

				Assert.True(EquipmentService.SetEquipmentSlot(e, slot, item));
				var read = EquipmentService.GetEquipmentSlot(e, slot);

				Assert.NotNull(read);
				Assert.Equal(item.Id, read!.Id);
			}
		}
	}
}
