using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	// An authoritative OPEN-WORLD ambient spawn GROUP, decoded from the client RDB "NPC roam/movement" layout (type
	// 1010506 = NpcSpawnLayout, #332/#363). One row per spawn group per playfield: the roam AREA (an AABB) the group's
	// creatures wander, the population count, and - once resolved - the creature TYPE. This is the primitive an ambient
	// spawner consumes: spawn `MaxCount` of `NpcTemplateId`, wandering within [BoundsMin, BoundsMax]. The individual
	// captured spawn points are kept as child NpcSpawnAreaPoint anchors (fidelity / the exact retail spots).
	//
	// Kept SEPARATE from the capture-derived NpcSpawn set: NpcSpawnArea = the RDB-authoritative ambient population
	// (roaming groups), NpcSpawn = exact hand-placed / named / scripted entities. The long-term plan (#363) is that a
	// captured NpcSpawn confirmed to belong to an area graduates OUT of NpcSpawn and is handled by its area instead.
	//
	// 1010506 carries WHERE + how many, never WHICH creature (that is server-authoritative). So NpcTemplateId is
	// NULLABLE and inferred by the resolver (name / containment / id-propagation) then confirmed by hand; ResolvedBy
	// records the provenance so a machine guess is distinguishable from a human-verified assignment.
	public class NpcSpawnArea
	{
		public int Id { get; set; }

		// The playfield (the RDB record id == the playfield id == Map.Id).
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The group's 0-based ordinal within the playfield record - part of the stable key, since a group NAME can
		// repeat within a map. (MapId, GroupOrdinal) is stable across re-imports (the client RDB is fixed content).
		public int GroupOrdinal { get; set; }

		[MaxLength(128)]
		public string GroupName { get; set; } = null!;

		// The grouping CATEGORY the id is scoped by: Community / Race / Superfamily / Name / "no category".
		[MaxLength(32)]
		public string Category { get; set; } = null!;

		// The raw group id string: a numeric faction/family id for Community/Race/Superfamily, or the creature
		// display-name + 4-char tag for a "Name" group (e.g. "Shark (H8AV)").
		[MaxLength(128)]
		public string GroupKey { get; set; } = null!;

		// The parsed numeric community/faction/family id when GroupKey is numeric; null for "Name"/"no category" groups.
		public uint? CommunityId { get; set; }

		// The roam AREA (AABB) the group's creatures wander - the meaningful primitive (the RDB "roam paths" are just
		// space-filling wander samples of this box; see docs/rdb/npc-spawn-layout-1010506.md).
		public float BoundsMinX { get; set; }
		public float BoundsMinY { get; set; }
		public float BoundsMinZ { get; set; }
		public float BoundsMaxX { get; set; }
		public float BoundsMaxY { get; set; }
		public float BoundsMaxZ { get; set; }

		// The group's population = the number of primary spawn points (~one creature per point). The count an ambient
		// spawner should keep alive in the area.
		public int MaxCount { get; set; }

		// The creature TYPE this area spawns, or null when unresolved (WHICH is not in the RDB). FK to the NpcTemplate
		// catalog; SetNull so removing a type doesn't delete the area (it just reverts to unresolved).
		public uint? NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate? NpcTemplate { get; set; }

		// How NpcTemplateId was assigned (provenance): None until resolved; Name/Containment/IdPropagation for the
		// resolver's confidence tiers; Manual once confirmed by hand.
		public SpawnResolutionSource ResolvedBy { get; set; }

		// The primary spawn-point anchors of this group (the exact retail spots); cascade-delete with the area.
		public virtual ICollection<NpcSpawnAreaPoint> Points { get; set; } = new List<NpcSpawnAreaPoint>();
	}
}
