using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// An NPC vendor's shop window, imported from captured DispatchShopData (0x2033) packets
	// (Captures/scripts/decode_shopdata.py for the wire grammar). Keyed by the vendor NPC NAME - shop ids and NPC
	// handles are session-volatile, names are stable (same convention as conversations).
	[Index(nameof(VendorName), IsUnique = true)]
	public class Shop : BaseEntity
	{
		// The shop container type inside the content's ProtoInventoryGame.inventory_id (the bag is 104).
		public const uint ContainerType = 111;

		// The shop ID32 type word on the wire (0x0000C75B).
		public const uint WireType = 0x0000C75B;

		public virtual ICollection<ShopItem> Items { get; set; }

		[MaxLength(64)]
		public string VendorName { get; set; }

		// Window capacity (ProtoInventoryGame.max_size; observed 10/20/40/80/160).
		public uint MaxSize { get; set; }
	}

	// One ware in a shop window: a standard InventoryListEntry whose ProtoAcgItem identity lives in the Items
	// table (the importer harvests the wares' quads/archetypes into Items from the same packet, so the template
	// resolves). Stat356/TailLevel are the shop-specific entry fields, kept verbatim for byte-exact rebuilds.
	[Index(nameof(ShopId), nameof(Pos), IsUnique = true)]
	public class ShopItem : BaseEntity
	{
		[ForeignKey(nameof(ShopId))]
		public virtual Shop Shop { get; set; }

		public uint Count { get; set; }

		// The RDB item template id (= Items.Id once harvested; deliberately no hard FK so a shop row survives a
		// missing item - the builder skips unresolvable slots).
		public uint ItemTemplateId { get; set; }
		public uint Pos { get; set; }

		public uint ShopId { get; set; }

		// The per-slot stat-356 value (replaces the bag's record-id stat 23; semantics unknown, varies per slot).
		public uint Stat356 { get; set; }

		// The entry tail's level field (ProtoAcgItem.level; observed 1 or 40).
		public uint TailLevel { get; set; }
	}
}
