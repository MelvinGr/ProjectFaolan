using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One ware in a shop window: a standard InventoryListEntry whose ProtoAcgItem identity lives in the Items
	// table (the importer harvests the wares' quads/archetypes into Items from the same packet, so the template
	// resolves). Stat356/TailLevel are the shop-specific entry fields, kept verbatim for byte-exact rebuilds.
	// Keyed by its slot in the shop window - the identity the importer already deduped on as a unique index, now
	// promoted to the primary key. The wares are delete+reinserted on every re-harvest, so a surrogate id was
	// renumbering all 782 rows (~17 times over) and churning faolan.sql for no semantic change.
	[PrimaryKey(nameof(ShopId), nameof(Pos))]
	public class ShopItem
	{
		[ForeignKey(nameof(ItemTemplateId))]
		public virtual Item Item { get; set; } = null!;

		// The RDB item template id (= Items.Id). Hard FK to Items (DeleteBehavior.Restrict): every ware carries its
		// full ProtoAcgItem identity in the same 0x2033 packet, so the items module always harvests it - the `shops`
		// module depends on `items` to guarantee it ran first. The FK makes an un-harvested ware fail the import loudly
		// instead of silently rendering an empty shop window.
		public uint ItemTemplateId { get; set; }

		public uint ShopId { get; set; }

		[ForeignKey(nameof(ShopId))]
		public virtual Shop Shop { get; set; } = null!;

		public uint Count { get; set; }
		public uint Pos { get; set; }

		// Stat 356 is `Hash` in the client's own stat-name table (`CharacterStatId.Hash` = 0x164), and the value is a
		// printable 4-character code, not a number - all 92 distinct values decode big-endian to ASCII: `9P6F`,
		// `1OVJ`, `00XI`, `COTR`, `ZCM7`, ... That is the ACGHash token shape the wire uses elsewhere.
		//
		// It is not a per-slot value: it is many-to-many with the ware and groups them - 92 codes over 1,375
		// templates, one code (`1OVJ`) spanning 322 templates, while 571 templates carry more than one across
		// different shops. So it is neither a per-slot nonce nor the item's own identity - it names some family a
		// ware belongs to. 81 of the 110 shops use a
		// single code for their whole stock (up to 165 wares); the other 29 mix, and one 7-ware shop uses 7.
		//
		// Kept verbatim for byte-exact rebuilds either way. [V0: what ShopItem's stat-356 Hash 4CC groups]
		public uint Stat356 { get; set; }

		// The entry tail's level field (ProtoAcgItem.level; observed 1 or 40).
		public uint TailLevel { get; set; }
	}
}
