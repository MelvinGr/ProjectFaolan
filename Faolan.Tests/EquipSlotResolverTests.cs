using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Inventory;
using Xunit;

namespace Faolan.Tests
{
	// The equip-slot resolver, which reads the client's own item-template fields: `Placement` (the slot
	// bitmask), `DefaultPos` (the preferred one) and `OccupySlots` (everything worn consumes).
	//
	// Every mask below is a real shipped value - the comment names the item it came from - so these are the catalogue's
	// own shapes rather than invented ones. The reading was validated against all 72 template/slot pairs the corpus
	// records as actually worn (`ItemInstances` container 101); see `Item.Placement`.
	public class EquipSlotResolverTests
	{
		private static Item Template(uint placement, uint defaultPos, uint occupy = 0)
		{
			return new Item
			{
				Placement = placement,
				DefaultPos = defaultPos,
				OccupySlots = occupy
			};
		}

		private static HashSet<CharacterEquipmentSlot> Worn(params CharacterEquipmentSlot[] slots)
		{
			return [..slots];
		}

		[Fact]
		// "Ornate Gold Ring": Placement 1536 = bits 9|10. A ring goes on either hand, left first, and the corpus has
		// the same template worn at slot 9 on one character and slot 10 on another.
		public void ARingTakesEitherFinger_LeftFirst()
		{
			var ring = Template(1536, 9);

			Assert.Equal([CharacterEquipmentSlot.LeftRing, CharacterEquipmentSlot.RightRing], EquipSlotResolver.CandidateSlots(ring));
			Assert.Equal(CharacterEquipmentSlot.LeftRing, EquipSlotResolver.ResolveFreeSlot(ring, Worn()));
			Assert.Equal(CharacterEquipmentSlot.RightRing, EquipSlotResolver.ResolveFreeSlot(ring, Worn(CharacterEquipmentSlot.LeftRing)));
			Assert.Null(EquipSlotResolver.ResolveFreeSlot(ring, Worn(CharacterEquipmentSlot.LeftRing, CharacterEquipmentSlot.RightRing)));
		}

		[Fact]
		// "Ceremonial Dagger": Placement 320 = bits 6|8. A hand bit is a hand, not a slot - slots 20/21 are weapon
		// set 2 and are set-neutral - so a one-hander's four candidates are both hands of both sets.
		public void AOneHanderOffersBothHandsOfBothWeaponSets()
		{
			var dagger = Template(320, 6);

			Assert.Equal([
				CharacterEquipmentSlot.MainWeapon, CharacterEquipmentSlot.RangedWeapon,
				CharacterEquipmentSlot.OffHandWeapon, CharacterEquipmentSlot.Ammo
			], EquipSlotResolver.CandidateSlots(dagger));

			Assert.Equal(CharacterEquipmentSlot.MainWeapon, EquipSlotResolver.ResolveFreeSlot(dagger, Worn()));
			Assert.Equal(CharacterEquipmentSlot.RangedWeapon, EquipSlotResolver.ResolveFreeSlot(dagger, Worn(CharacterEquipmentSlot.MainWeapon)));
		}

		[Fact]
		// "Totemic Folio" and 44 more of category 30009 (tomes, folios, trinkets): the one-hander mask 320, but
		// `DefaultPos` 8. This is the case that proves DefaultPos has to be read - it is not the low bit of the mask.
		public void AnOffHandFocusPrefersTheOffHand_DespiteCarryingTheOneHanderMask()
		{
			var folio = Template(320, 8);

			Assert.Equal(CharacterEquipmentSlot.OffHandWeapon, EquipSlotResolver.CandidateSlots(folio).First());
			Assert.Equal(CharacterEquipmentSlot.OffHandWeapon, EquipSlotResolver.ResolveFreeSlot(folio, Worn()));
		}

		[Fact]
		// "Malefactor's Claymore": Placement 64 = bit 6 alone (main hand only) with OccupySlots 256 = bit 8 - the off
		// hand goes too. 280 shipped templates have this shape, every one in a two-handed weapon class.
		public void ATwoHanderConsumesTheOffHandOfItsOwnSet()
		{
			var claymore = Template(64, 6, 256);

			Assert.Equal([CharacterEquipmentSlot.MainWeapon, CharacterEquipmentSlot.RangedWeapon], EquipSlotResolver.CandidateSlots(claymore));
			Assert.Equal([CharacterEquipmentSlot.OffHandWeapon], EquipSlotResolver.Occupies(claymore, CharacterEquipmentSlot.MainWeapon));

			// Worn in set 2, it is set 2's off hand that goes - the extra bit is stated in set 1's numbering.
			Assert.Equal([CharacterEquipmentSlot.Ammo], EquipSlotResolver.Occupies(claymore, CharacterEquipmentSlot.RangedWeapon));

			// A shield in the off hand blocks set 1 outright; the two-hander falls through to set 2.
			Assert.Equal(CharacterEquipmentSlot.RangedWeapon, EquipSlotResolver.ResolveFreeSlot(claymore, Worn(CharacterEquipmentSlot.OffHandWeapon)));
			Assert.Null(EquipSlotResolver.ResolveFreeSlot(claymore, Worn(CharacterEquipmentSlot.OffHandWeapon, CharacterEquipmentSlot.Ammo)));
		}

		[Fact]
		// "Prison key", "Boar Pelt", "Crate of Steel" and 1,046 more: Placement 0. Not equippable is a statement the
		// template makes, and it is the single most common answer in the catalogue.
		public void PlacementZeroIsNotEquippable()
		{
			var key = Template(0, 24);

			Assert.Empty(EquipSlotResolver.CandidateSlots(key));
			Assert.Null(EquipSlotResolver.ResolveFreeSlot(key, Worn()));
		}

		[Theory]
		// One bit, one slot, for everything that is not a hand: the plain armour cases.
		[InlineData(2u, 1u, CharacterEquipmentSlot.Neck)] // "Harlot's Necklace"
		[InlineData(4u, 2u, CharacterEquipmentSlot.Head)] // "Blackwolf Cap"
		[InlineData(8u, 3u, CharacterEquipmentSlot.Bag)] // "Bag of Holding"
		[InlineData(32u, 5u, CharacterEquipmentSlot.Chest)] // "Dusklight Tunic"
		[InlineData(2048u, 11u, CharacterEquipmentSlot.Cape)] // "Drinking Cape"
		[InlineData(65536u, 16u, CharacterEquipmentSlot.Legs)] // "Fine Steelsilk Pants"
		[InlineData(262144u, 18u, CharacterEquipmentSlot.Wrist)] // "Slave Manacles"
		[InlineData(8388608u, 23u, CharacterEquipmentSlot.Carried)] // "Bundle of Wood" - the slot #916 named
		[InlineData(67108864u, 26u, CharacterEquipmentSlot.ResourceBag)] // "Artisan's Satchel"
		public void ASingleBitResolvesToThatSlot(uint placement, uint defaultPos, CharacterEquipmentSlot expected)
		{
			var item = Template(placement, defaultPos);

			Assert.Equal([expected], EquipSlotResolver.CandidateSlots(item));
			Assert.Equal(expected, EquipSlotResolver.ResolveFreeSlot(item, Worn()));
			Assert.Empty(EquipSlotResolver.Occupies(item, expected));
		}
	}
}
