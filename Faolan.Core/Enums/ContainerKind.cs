namespace Faolan.Core.Enums
{
	// The client's container-type ids (ProtoInventoryGame.inventory_id). Not only character containers - Loot is a
	// world container - so the set is "container kinds we address on the wire".
	//
	// ⚠️ Adding a member is a SCHEMA change, not a code change: CharacterInventory.Container is
	// [ConstrainToEnumValues], which generates `CK_CharacterInventory_Container: Container IN (…)` from exactly
	// these values. A new member widens that CHECK, so it needs a migration and a rebuild of the committed
	// faolan.db - budget for that before adding one.
	public enum ContainerKind : uint
	{
		Equipped = 101,
		Bag = 104,
		Bank = 105,
		Shop = 111, // a vendor's wares (ShopContentCodec) - not a character container, like Loot
		Resource = 112,
		Quest = 113,
		Loot = 115
	}
}
