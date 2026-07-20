namespace Faolan.Core.Enums
{
	// An item's quality/rarity tier. These are the client's 6 tooltip tiers (AgeOfConan.exe font colours, low->high:
	// mundane superior enchanted rare epic legendary). The RDB item-template property field 499 carries the tier
	// 0-INDEXED (0=Mundane .. 5=Legendary), so RdbItemImporter maps ItemQuality = field499 + 1 (#397 - storing field499
	// directly was one tier too low; e.g. a legendary max-bag carries 499=5 = Legendary, not Epic). Ordinal 1-based,
	// low tier -> high tier. NULL on an Item means the template carried no field 499 (or the item was never RDB-imported).
	public enum ItemQuality
	{
		Mundane = 1,
		Superior = 2,
		Enchanted = 3,
		Rare = 4,
		Epic = 5,
		Legendary = 6
	}
}
