namespace Faolan.Core.Enums
{
	// The worn-equipment slot ids as they appear on the wire (spawn InventoryEntry.pos / the inventory
	// snapshot's equipped-container slot bytes / the archive player_items column order). All 17 retail slots
	// are mapped from captures - the layout is the melee pair (6/8), the ranged pair (20/21) and gaps at
	// 4/13/19; 17 is the legacy starter-bag cell (FirstPosBag, handled separately). necrol80 wears items in every
	// slot incl. Bag (3, the equipped bag that sizes the backpack - was mislabeled "Tokens"), OffHandWeapon
	// (8, Ceremonial Dagger), RangedWeapon (20, Executioner Crossbow) and Ammo (21, Bolt Quiver).
	public enum CharacterEquipmentSlot : uint
	{
		Neck = 1,
		Head = 2,
		Bag = 3,
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
		ResourceBag = 26, // the equipped RESOURCE bag - sizes the resource container (kind 112), the sibling of Bag (3)
		                  // for the backpack. Live-confirmed: equipping a Max Resource Bag sent InventoryToEquipped
		                  // target 26. Its extra slots come from Item.ResourceBagExtraSlots (RDB field 1331), added on
		                  // top of the 48-slot base.

		// NOTE: there is apparently also a QUEST bag - the third sibling, growing the quest container (kind 113) the way
		// Bag grows the backpack and ResourceBag grows the resource container. Its equip slot id is UNKNOWN: no quest
		// bag exists in the DB to equip and read the target slot from (the resource bag's slot 26 was recovered that
		// way). Add the enum member + the CharacterEquipment slot + the quest-container sizing once one surfaces.

		// Vanity ("costume") item slots = 1000 + the base equip slot id. VanityHead (1002) and VanityChest (1005) are
		// capture-confirmed by the ToggleVanityArmorFilter slot arg (base slot 2/5); 1007/1011/1014/1018 are confirmed
		// on the wire by NecroL80's L80 FullChar snapshot, whose EQUIPPED container (kind 101) carries pos 1002, 1007,
		// 1011, 1014 and 1018 next to the worn set (faolan-account-login-necrol80-necro-lvl80). Only VanityChest (1005)
		// has yet to appear in a captured equipped container.
		VanityHead = 1002,
		VanityChest = 1005,
		VanityBelt = 1007,
		VanityCape = 1011,
		VanityBoots = 1014,
		VanityWrist = 1018
	}
}
