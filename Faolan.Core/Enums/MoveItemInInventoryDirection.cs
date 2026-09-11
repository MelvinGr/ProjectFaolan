namespace Faolan.Core.Enums
{
	// The TypeId of an Interact 0x09 equip move identifies the drag's window pair; Mode is the client's
	// monotonic per-session action counter, not a direction (armed+unarmed combat capture).
	// Out-of-slot tails are [srcEquipSlot][destBagCell][0...]; into-slot tails are
	// [srcBagCell][destEquipSlot][item][item][0][flags].
	public enum MoveItemInInventoryDirection
	{
		EquippedToInventory = 42010,
		InventoryToEquipped = 42011,
		EquippedToBag = 41842, // same out-of-slot grammar via the backpack window (live client)
		BagToEquipped = 41843 // same into-slot grammar via the backpack window
	}
}
