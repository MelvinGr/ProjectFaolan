using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// An authoritative OPEN-WORLD NPC spawn point, decoded from the client RDB "NPC roam/movement" layout (type
	// 1010506) rather than from a capture (#332). Capture-INDEPENDENT: it exists for every open-world playfield the
	// client ships (empty for scripted/instanced maps - the castle, Tortage interiors). One row per PRIMARY spawn
	// point of a spawn GROUP; the stable key is (MapId, GroupOrdinal, PointIndex), stable across re-imports because the
	// client RDB is fixed content.
	//
	// `Category`/`GroupKey` describe WHAT the group is (a Community/Race/Superfamily faction id, or - for a "Name"
	// group - the creature's display-name+tag, e.g. "Shark (H8AV)"), but 1010506 does NOT carry the creature
	// TEMPLATE, so this table is placement geometry + grouping only; correlating a template to a point still needs
	// captures (the #332 match/snap follow-up). Reference data - not consumed by the live server yet.
	public class RdbSpawnPoint
	{
		public int Id { get; set; }

		// The playfield this point belongs to (the RDB record id == the playfield id == Map.Id).
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The spawn group's 0-based ordinal within the playfield record - part of the stable key, since a group NAME
		// can repeat within a map.
		public int GroupOrdinal { get; set; }

		// The 0-based primary-point index within the group - the third component of the stable (map, group, index) key.
		public int PointIndex { get; set; }

		[MaxLength(128)]
		public string GroupName { get; set; } = null!;

		// The grouping CATEGORY the id is scoped by: Community / Race / Superfamily / Name / "no category".
		[MaxLength(32)]
		public string Category { get; set; } = null!;

		// The raw group id string: a numeric faction/family id for Community/Race/Superfamily, or the creature
		// display-name+tag for a "Name" group (e.g. "Shark (H8AV)").
		[MaxLength(128)]
		public string GroupKey { get; set; } = null!;

		// The parsed numeric community/faction/family id when GroupKey is numeric; null for "Name"/"no category" groups.
		public uint? CommunityId { get; set; }

		public float X { get; set; }
		public float Y { get; set; }
		public float Z { get; set; }
	}
}
