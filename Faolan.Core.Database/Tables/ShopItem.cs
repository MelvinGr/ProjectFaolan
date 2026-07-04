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
