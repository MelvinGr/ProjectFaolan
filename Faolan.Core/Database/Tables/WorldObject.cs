namespace Faolan.Core.Database.Tables
{
	// A captured static world object on a map - the decor/props (66AEDD50), interactive containers (F096251E)
	// and zone/city labels (33A56FB0) that build the world alongside the NPC spawns ([[NpcSpawn]]'s sibling).
	// Keyed for dedup by (MapId, Position) - the stable spawn point; the handle is session-scoped. Imported from
	// captures by Faolan.Importer (WorldObjectExtractor); built into a wire body by WorldObjectBuilder.
	//
	// `Data` is the still-raw captured packet (interim). The structured columns are lifted from the body: the
	// SubOpcode (the object CLASS), the transform (position + rotation), and the object message's id + name
	// (docs/spawn-dissection-plan.md Phase 6). These share the FA015807 transform grammar but have NO pre-IIR
	// block - the object message's length prefix follows the transform directly.
	public class WorldObject : BaseEntity
	{
		public byte[] Data { get; set; }
		public uint SubOpcode { get; set; }
		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }
		public float RotationX { get; set; }
		public float RotationY { get; set; }
		public float RotationZ { get; set; }
		public float RotationW { get; set; }
		public string Name { get; set; }
		public uint ObjectId { get; set; }
		public uint CapturedHandle { get; set; }
		public uint MapId { get; set; }
	}
}
