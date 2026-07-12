using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// An NPC vendor's shop window, imported from captured DispatchShopData (0x2033) packets
	// (Captures/scripts/decode_shopdata.py for the wire grammar). Keyed by the vendor NPC TEMPLATE id (#244) - shop ids
	// and NPC handles are session-volatile, and the internal NAME is not unique (25 templates share "Halen van Dummy",
	// so keying by name merged distinct vendors and leaked a shop onto non-vendor same-named NPCs like Atraxus).
	[Index(nameof(NpcTemplateId), IsUnique = true)]
	public class Shop : BaseEntity
	{
		// The shop container type inside the content's ProtoInventoryGame.inventory_id (the bag is 104).
		public const uint ContainerType = 111;

		// The shop ID32 type word on the wire (0x0000C75B).
		public const uint WireType = 0x0000C75B;

		public virtual ICollection<ShopItem> Items { get; set; } = [];

		// The vendor NPC TYPE this shop belongs to (the stable per-type key).
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))] // Maybe to NpcSpawn?
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The vendor's internal name at capture time - display/logging only (NOT unique; see the class note).
		[MaxLength(64)]
		public string VendorName { get; set; } = null!;

		// Window capacity (ProtoInventoryGame.max_size; observed 10/20/40/80/160).
		public uint MaxSize { get; set; }
	}
}
