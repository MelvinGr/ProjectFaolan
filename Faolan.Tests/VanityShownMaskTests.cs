using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The vanity ("costume") hide filter is a bitmask, and the bit index is the toggle's wire `slot` arg = the base
	// equip slot id. It is emitted verbatim in the spawn's PreFrame, so a drift in the bit mapping shows up in-client as
	// the wrong piece of armour hidden.
	//
	// Capture-confirmed by the vanity login dumps: showing Head (slot 2) sets bit 2, Chest (slot 5) sets bit 5, and the
	// "toggle vanity global" master is slot 31 -> bit 31.
	//
	// A worn item is an item instance whose Slot column is the wire slot id, so there is no slot-to-column mapping
	// between the two to get wrong. The mask is not a property of any item either: it lives on Character.
	public class VanityShownMaskTests
	{
		[Fact]
		public void SetVanityFilter_SetsCapturedMaskBits()
		{
			var c = new Character();

			Assert.True(c.SetVanityFilter((uint)CharacterEquipmentSlot.Head, true)); // slot 2 -> bit 2
			Assert.Equal(1u << 2, c.VanityShownMask);

			Assert.True(c.SetVanityFilter((uint)CharacterEquipmentSlot.Chest, true)); // slot 5 -> bit 5 (additive)
			Assert.Equal((1u << 2) | (1u << 5), c.VanityShownMask);

			Assert.True(c.SetVanityFilter((uint)CharacterEquipmentSlot.Head, false)); // clearing one leaves the other
			Assert.Equal(1u << 5, c.VanityShownMask);

			Assert.True(c.SetVanityFilter(Character.VanityFilterMainSlot, true)); // global master -> bit 31
			Assert.Equal((1u << 5) | (1u << 31), c.VanityShownMask);

			Assert.Equal(31, Character.VanityFilterMainSlot);
			Assert.False(c.SetVanityFilter(32, true)); // out of range -> no-op
		}
	}
}
