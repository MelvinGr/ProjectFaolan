namespace Faolan.Core.Enums
{
	// The client's container-type ids (ProtoInventoryGame.inventory_id). Not only character containers - Loot is a
	// world container - so the set is "container kinds we address on the wire".
	//
	// Adding a member is a schema change, not a code change: CharacterInventory.Container is
	// [ConstrainToEnumValues], which generates `CK_CharacterInventory_Container: Container IN (...)` from exactly
	// these values. A new member widens that CHECK, so it needs a migration and a rebuild of the committed
	// faolan.db - budget for that before adding one.
	public enum ContainerKind : uint
	{
		Equipped = 101,
		Bag = 104,
		Bank = 105,

		// The guild bank vault. The only container whose wire `instance` is a guild id rather than a
		// character id - `DispatchGuildBankData`'s inventory_id is `{ type: 109, instance: <guild id> }`.
		// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:45:09]
		GuildBank = 109,
		Shop = 111, // a vendor's wares (ShopContentCodec) - not a character container, like Loot
		Resource = 112,

		// A mail's attached item, escrowed for the recipient. Retail
		// registers this container for the buyer the instant an auction-house purchase completes, before the mail is
		// even read - so a bought item leaves the seller immediately and waits here, which is what keeps the seller
		// from taking it back out of their own bank.
		// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:41.932 - SetItemStat container 114
		//  slot 0 for the buyer, right after BuyItem]
		Mail = 114,
		Quest = 113,
		Loot = 115
	}
}
