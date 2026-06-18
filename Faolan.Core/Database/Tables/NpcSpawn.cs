using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A captured NPC entity spawn (FA015807) on a map. Keyed for dedup by (MapId, Position) - the stable spawn
	// point; the entity handle is session-scoped. Imported from captures by Faolan.Importer (NpcSpawnExtractor).
	//
	// `Data` is the still-raw captured FA015807 body (interim, like the world burst). The structured columns
	// below are LIFTED from its ProtoCoreCharacterIIR by the dissection (docs/archive/spawn-dissection-plan.md Phase 4):
	// transform (position + rotation) and the NPC identity/appearance (TemplateId/Race/Sex/Level/...). They make
	// the identity first-class so the server can build the spawn from data; `Data` shrinks once the pre-IIR
	// float block + post-IIR scaffold are also lifted into scaffold rows (the same blocks the player spawn keeps).
	// Transform (PositionX/Y/Z + the RotationX/Y/Z/W quaternion + the Position/Rotation accessors) is inherited
	// from Base3DEntity, shared with the player Character and the world objects.
	public class NpcSpawn : Base3DEntity
	{
		public bool HasEquipment { get; set; }
		public byte[] Data { get; set; }
		public string Name { get; set; }
		public uint CapturedHandle { get; set; }

		// Faction/community lifted from NpcStats.community: 0 = neutral/friendly, > 0 = a hostile faction. Drives NPC
		// aggro hostility (server-architecture-roadmap).
		public uint Community { get; set; }
		public uint HeadMesh { get; set; }

		// Real per-NPC max health (player-facing) lifted from the spawn's CharacterStats.health - the combat HP vs the
		// level-scaled placeholder. 0 = the spawn carried none.
		public uint Health { get; set; }
		public uint Level { get; set; }
		public uint MapId { get; set; }
		public uint MonsterData { get; set; }
		public uint MonsterScale { get; set; }
		public uint Race { get; set; }
		public uint Sex { get; set; }

		// Identity/appearance, lifted from the CharacterIIR (ProtoCoreCharacterIIR). TemplateId (NpcStats.f4) is
		// the stable per-type identity; MonsterScale carries the per-instance size jitter.
		public uint TemplateId { get; set; }

		// The humanoid appearance / morph table lifted from the captured post-IIR (#185) - empty for animals, which
		// carry no such table. When present, NpcSpawnBuilder generates the post-IIR from these rows instead of
		// keeping the captured blob's (the from-DB lift of the bald-fix's verbatim post-IIR, #243).
		public virtual ICollection<NpcSpawnMorph> Morphs { get; set; } = new List<NpcSpawnMorph>();

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }
	}
}
