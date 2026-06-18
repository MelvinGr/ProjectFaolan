using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A captured NPC entity spawn (FA015807) on a map. Keyed for dedup by (MapId, Position) - the stable spawn
	// point; the entity handle is session-scoped. Imported from captures by Faolan.Importer (NpcSpawnExtractor).
	//
	// `Data` is the captured FA015807 body. It is NO LONGER REPLAYED verbatim: NpcSpawnBuilder DECODES it and
	// reconstructs the spawn field-by-field (#185) - the transform comes from the columns below, the scaffolds
	// (pre-IIR/post-IIR) are generated from scratch, equipment from NpcSpawnEquipment, morphs from NpcSpawnMorph.
	// So `Data` now serves only as the SOURCE for the CharacterIIR stat core (CharacterStats / NpcStats / Bcc)
	// that hasn't been lifted into typed storage yet. A roster measurement (#185, 2026-06-18) showed that core is
	// fully-typed protobuf with ~23 CharacterStats + ~9 NpcStats fields that vary, mostly TYPE-level (only 27
	// distinct TemplateIds) - so its lift belongs with the Npc-type/NpcSpawn-instance split (#244), where the stat
	// block is stored once per type as typed rows. `Data` is retired there, not here.
	// The structured columns below are LIFTED from the ProtoCoreCharacterIIR: NPC identity/appearance
	// (TemplateId/Race/Sex/Level/HeadMesh/...). Transform (PositionX/Y/Z + RotationX/Y/Z/W + the Position/Rotation
	// accessors) is inherited from Base3DEntity, shared with the player Character and the world objects.
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

		// The worn equipment (CharacterIIR f23) lifted from the captured spawn (#185) - empty for unarmed NPCs.
		// When present, NpcSpawnBuilder rebuilds the spawn's inventory_entry list from these rows instead of the
		// captured Data blob's (the NPC counterpart of the player's DB-sourced equipment, PlayerSpawnEquipment).
		public virtual ICollection<NpcSpawnEquipment> Equipment { get; set; } = new List<NpcSpawnEquipment>();

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		// The NPC TYPE this placement is an instance of (#244). Nullable during the expand phase: populated by
		// deriving the type set from the spawns' TemplateId; the duplicated type columns above (Name/Community/...)
		// are retired onto Npc in a later contract step. Joined by TemplateId, not Id.
		public uint? NpcId { get; set; }

		[ForeignKey(nameof(NpcId))]
		public virtual Npc Npc { get; set; }
	}
}
