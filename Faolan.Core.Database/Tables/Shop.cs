using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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

		public virtual ICollection<ShopItem> Items { get; set; } = [];

		[MaxLength(64)]
		public string VendorName { get; set; } = null!;

		// Window capacity (ProtoInventoryGame.max_size; observed 10/20/40/80/160).
		public uint MaxSize { get; set; }
	}
}
