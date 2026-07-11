using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One ware in a shop window: a standard InventoryListEntry whose ProtoAcgItem identity lives in the Items
	// table (the importer harvests the wares' quads/archetypes into Items from the same packet, so the template
	// resolves). Stat356/TailLevel are the shop-specific entry fields, kept verbatim for byte-exact rebuilds.
	[Index(nameof(ShopId), nameof(Pos), IsUnique = true)]
	public class ShopItem : BaseEntity
	{
		// The RDB item template id (= Items.Id). Hard FK to Items (DeleteBehavior.Restrict): every ware carries its
		// full ProtoAcgItem identity in the same 0x2033 packet, so the items module always harvests it - the `shops`
		// module depends on `items` to guarantee it ran first. The FK makes an un-harvested ware fail the import LOUDLY
		// instead of silently rendering an empty shop window.
		public uint ItemTemplateId { get; set; }

		[ForeignKey(nameof(ItemTemplateId))]
		public virtual Item Item { get; set; }

		public uint ShopId { get; set; }

		[ForeignKey(nameof(ShopId))]
		public virtual Shop Shop { get; set; }

		public uint Count { get; set; }
		public uint Pos { get; set; }

		// The per-slot stat-356 value (replaces the bag's record-id stat 23; semantics unknown, varies per slot).
		public uint Stat356 { get; set; }

		// The entry tail's level field (ProtoAcgItem.level; observed 1 or 40).
		public uint TailLevel { get; set; }
	}
}
