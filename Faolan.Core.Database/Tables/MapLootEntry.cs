using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One item observed in a map's captured loot bags. The per-map loot pool: a kill on this map rolls its
	// random drops from the set of items that retail bags on the same map actually carried, instead of the all-items
	// random sample (GetRandomBuildableItems). Seeded by the importer's map-loot module from the captured
	// LootBagFullUpdateIIR (0xF096251E) contents, tracked against the MapChange that preceded them.
	//
	// The captured bags read as static map bags (world-entry burst objects), not per-NPC death drops, so this is a
	// map pool, not a per-NPC table - a per-NPC association needs a kill<->bag correlation that isn't in the current
	// captures (see the issue). Dedup key is (MapId, ItemTemplateId); the row carries no quantity/weight yet.
	// The dedup key is the row: (MapId, ItemTemplateId) carries everything, so it is the primary key - there was
	// never anything for a surrogate id to identify.
	[PrimaryKey(nameof(MapId), nameof(ItemTemplateId))]
	public class MapLootEntry
	{
		// The item template id (matches Item.Id) seen in a bag on that map.
		public uint ItemTemplateId { get; set; }

		// The map whose captured bags carried this item.
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;
	}
}
