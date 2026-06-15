using System;

namespace Faolan.Core.Database.Tables
{
	// A captured static world object on a map - the decor/props (66AEDD50), interactive containers (F096251E),
	// zone/city labels (33A56FB0), destructibles (6BD56736) and climbing dynels (1FD6412B) that build the world
	// alongside the NPC spawns ([[NpcSpawn]]'s sibling). All share the object transform grammar (no pre-IIR block).
	// Keyed for dedup by (MapId, Position) - the stable spawn point; the handle is session-scoped. Imported from
	// captures by Faolan.Importer (WorldObjectExtractor); built into a wire body by WorldObjectBuilder.
	//
	// `Data` is the still-raw captured packet (interim). The structured columns are lifted from the body: the
	// SubOpcode (the object CLASS), the transform (position + rotation), and the object message's id + name
	// (docs/archive/spawn-dissection-plan.md Phase 6). These share the FA015807 transform grammar but have NO pre-IIR
	// block - the object message's length prefix follows the transform directly.
	// Transform (PositionX/Y/Z + the RotationX/Y/Z/W quaternion + the Position/Rotation accessors) is inherited
	// from Base3DEntity, shared with the player Character and the NPC spawns.
	public class WorldObject : Base3DEntity
	{
		// LEGACY verbatim captured packet. The world-entry render no longer replays it - it CONSTRUCTS the object
		// from the typed columns (identity) + KdTreeTail (geometry) + the burst's live placement (#187,
		// TryBuildOntoLivePlacement). Retained ONLY as: (a) the import source-of-truth the columns are derived from
		// (per "captures are the primary truth source"), and (b) the runtime loot-bag-template / un-lifted fallback.
		// Do NOT use for new render paths - build from the columns + KdTreeTail instead.
		[Obsolete("Legacy verbatim packet; the render builds from the typed columns + KdTreeTail (#187). Kept only as the import source + runtime loot-bag/fallback.")]
		public byte[] Data { get; set; }
		public string Name { get; set; }
		public uint CapturedHandle { get; set; }
		public uint MapId { get; set; }
		public uint ObjectId { get; set; }
		public uint SubOpcode { get; set; }

		// SimpleDynelFullUpdateIIR (0x66AEDD50) identity, lifted from the opaque Data slice into typed columns so the
		// dynel's ProtoSimpleDynelIIR message is CONSTRUCTED from the DB rather than replayed (#187/#57; reader decoded
		// in docs/iir-subclass-readers.md, codec SimpleDynelMessageCodec). Name (above) is proto field 2. The two
		// nullable columns are absent on most dynels (3 captured field-shapes), so null = "field not present" - which
		// keeps the rebuilt message byte-exact to the capture. Non-dynel object classes leave these null/default and
		// keep using the Data slice. (Kdtree is always 0 on world dynels, emitted by the builder.)
		public uint Kdtree { get; set; }               // proto field 1 (nonzero on ~32% - a kd-tree node ref)
		public string DisplayName { get; set; }       // proto field 3
		public uint CarsGroup { get; set; }            // proto field 5
		public bool IsClosed { get; set; }             // proto field 12
		public uint? QuestItemEffect { get; set; }     // proto field 6 (present on ~23%)
		public uint? WaypointType { get; set; }        // proto field 7 (present on zone-label objects)
		public uint? TemplateItemId { get; set; }      // proto field 8 (present on ~25%)

		// The bytes AFTER the identity message: the kd-tree / collision / bounds geometry (FUN_0080c620), per-object
		// but session-INDEPENDENT (static decor geometry). Stored so the world-entry build constructs the whole
		// object from the DB - identity (columns above) + this tail - instead of slicing it from the raw Data packet
		// (#187). Null on un-lifted rows (those still use Data). The transform (pos/rot) is rebuilt from Base3DEntity's
		// columns and the live playfield comes from the burst, so with this the row's Data is no longer read at render.
		public byte[] KdTreeTail { get; set; }
	}
}
