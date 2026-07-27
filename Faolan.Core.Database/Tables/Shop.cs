using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// An NPC vendor's shop window, imported from captured DispatchShopData (0x2033) packets
	// (tools/capture/decode_shopdata.py for the wire grammar). Keyed by the vendor NPC TEMPLATE id (#244) - shop ids
	// and NPC handles are session-volatile, and the internal NAME is not unique (25 templates share "Halen van Dummy",
	// so keying by name merged distinct vendors and leaked a shop onto non-vendor same-named NPCs like Atraxus).
	// Keyed BY its vendor TYPE: the relationship is one-to-one (NpcTemplate.Shop), so NpcTemplateId is both the
	// foreign key and the whole identity - a surrogate id alongside it identified nothing and renumbered the shop
	// set (and its wares) on every re-harvest. It was already the declared unique index.
	[PrimaryKey(nameof(NpcTemplateId))]
	public class Shop
	{
		// The shop container type inside the content's ProtoInventoryGame.inventory_id (the bag is 104).
		public const uint ContainerType = 111; // TODO: Should just use the enum

		// The vendor NPC TYPE this shop belongs to (the stable per-type key).
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))] // Maybe to NpcSpawn?
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		public virtual ICollection<ShopItem> Items { get; set; } = [];

		// The vendor's internal name at capture time - display/logging only (NOT unique; see the class note).
		[MaxLength(64)]
		public string VendorName { get; set; } = null!;

		// Window capacity (ProtoInventoryGame.max_size; observed 10/20/40/80/160).
		public uint MaxSize { get; set; }
	}
}
