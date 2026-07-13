namespace Faolan.Core.Enums
{
	// An item's quality/rarity tier. The backing values are the CLIENT ids carried in the RDB item-template property
	// table at field 499 (validated against decrypted templates: basic starter clothing e.g. White Gown / Slave Sarong
	// = 1; high-end raid/vendor gear e.g. Helm of the Black Dragon / Cloak of the Silent Legion = 4; the top vet reward
	// Mythical Band of Advancement = 5). Ordinal 1-based, low tier -> high tier. Imported by RdbItemImporter; NULL on an
	// Item means the template carried no field 499 (or the item was never RDB-imported).
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
