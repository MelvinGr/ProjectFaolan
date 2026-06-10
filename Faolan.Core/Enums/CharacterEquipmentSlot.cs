namespace Faolan.Core.Enums
{
	// The worn-equipment slot ids as they appear on the wire (spawn InventoryEntry.pos / the inventory
	// snapshot's equipped-container slot bytes / the archive player_items column order). All 17 retail slots
	// are mapped from captures - the layout is the melee pair (6/8), the ranged pair (20/21) and gaps at
	// 4/13/19; 17 is the bag (FirstPosBag, handled separately). necrol80 wears items in every slot incl.
	// Tokens (3), OffHandWeapon (8, Ceremonial Dagger), RangedWeapon (20, Executioner Crossbow) and
	// Ammo (21, Bolt Quiver).
	public enum CharacterEquipmentSlot : uint
	{
		Neck = 1,
		Head = 2,
		Tokens = 3,
		Chest = 5,
		MainWeapon = 6, // main_hand_left (melee main hand)
		Belt = 7,
		OffHandWeapon = 8, // off_hand_left (melee off hand / shield)
		LeftRing = 9,
		RightRing = 10,
		Cape = 11,
		Shoulder = 12,
		Boots = 14,
		Hands = 15,
		Legs = 16,
		Wrist = 18,
		RangedWeapon = 20, // main_hand_right (bow/crossbow/thrown)
		Ammo = 21, // off_hand_right (quiver/bolts)

		// prob. all the same + 1000 still have to check
		VanityHead = 1002,
		VanityBelt = 1007,
		VanityCape = 1011,
		VanityBoots = 1014,
		VanityWrist = 1018
	}
}
