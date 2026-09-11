namespace Faolan.Core.Enums
{
	// An item's quality/rarity tier. The backing values are the client's own ids: the RDB item-template property field
	// 499, which is the 0-indexed tooltip tier (AgeOfConan.exe font colours, low->high: mundane superior enchanted rare
	// epic legendary). RdbItemImporter stores field 499 directly. Field 499 tops out at 5 = Legendary. NULL on
	// an Item means the template carried no field 499 (or the item was never RDB-imported).
	public enum ItemQuality
	{
		Mundane = 0,
		Superior = 1,
		Enchanted = 2,
		Rare = 3,
		Epic = 4,
		Legendary = 5
	}
}
