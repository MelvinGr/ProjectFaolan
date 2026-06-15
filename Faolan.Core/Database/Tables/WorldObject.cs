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
		public uint? TemplateItemId { get; set; }      // proto field 8 (present on ~25%)
	}
}
