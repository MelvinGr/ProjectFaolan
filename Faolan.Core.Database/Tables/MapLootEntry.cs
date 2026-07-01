using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One item observed in a map's captured loot bags (#273). The per-map loot pool: a kill on this map rolls its
	// random drops from the set of items that retail bags on the SAME map actually carried, instead of the all-items
	// random sample (GetRandomBuildableItems). Seeded by the importer's map-loot module from the captured
	// LootBagFullUpdateIIR (0xF096251E) contents, tracked against the MapChange that preceded them.
	//
	// The captured bags read as STATIC map bags (world-entry burst objects), not per-NPC death drops, so this is a
	// MAP pool, not a per-NPC table - a per-NPC association needs a kill<->bag correlation that isn't in the current
	// captures (see the issue). Dedup key is (MapId, ItemTemplateId); the row carries no quantity/weight yet.
	public class MapLootEntry : BaseEntity
	{
		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		// The item template id (matches Item.Id) seen in a bag on that map.
		public uint ItemTemplateId { get; set; }

		// The map whose captured bags carried this item.
		public uint MapId { get; set; }
	}
}
