namespace Faolan.Core.Database.Tables
{
	// A captured NPC entity spawn (FA015807) on a map. Keyed for dedup by (MapId, Position) - the stable spawn
	// point; the entity handle is session-scoped. Imported from captures by Faolan.Importer (NpcSpawnExtractor).
	//
	// `Data` is the still-raw captured FA015807 body (interim, like the world burst). The structured columns
	// below are LIFTED from its ProtoCoreCharacterIIR by the dissection (docs/spawn-dissection-plan.md Phase 4):
	// transform (position + rotation) and the NPC identity/appearance (TemplateId/Race/Sex/Level/...). They make
	// the identity first-class so the server can build the spawn from data; `Data` shrinks once the pre-IIR
	// float block + post-IIR scaffold are also lifted into scaffold rows (the same blocks the player spawn keeps).
	public class NpcSpawn : BaseEntity
	{
		public byte[] Data { get; set; }
		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }
		public float RotationX { get; set; }
		public float RotationY { get; set; }
		public float RotationZ { get; set; }
		public float RotationW { get; set; }
		public string Name { get; set; }
		public uint CapturedHandle { get; set; }
		public uint MapId { get; set; }

		// Identity/appearance, lifted from the CharacterIIR (ProtoCoreCharacterIIR). TemplateId (NpcStats.f4) is
		// the stable per-type identity; MonsterScale carries the per-instance size jitter.
		public uint TemplateId { get; set; }
		public uint Race { get; set; }
		public uint Sex { get; set; }
		public uint Level { get; set; }
		public uint MonsterScale { get; set; }
		public uint MonsterData { get; set; }
		public uint HeadMesh { get; set; }
		public bool HasEquipment { get; set; }
	}
}
