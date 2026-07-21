using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	// An NPC *type* - the type-level identity shared by every placement of the same creature (#244). `NpcSpawn` is
	// the per-placement INSTANCE (transform + per-spawn appearance/equipment rolls); the type half (TemplateId, name,
	// community, and the type-level stat core) lives here once instead of being duplicated across every spawn (e.g.
	// Pillager 8756 is placed 85x). The PRIMARY KEY is `TemplateId` (NpcStats.f4, the stable RDB per-type id) - a
	// natural-key catalog like Item/Spell/Map (the RDB is the source of truth, so the stable RDB id is the key, not a
	// volatile DB-generated surrogate). Children reference it by their `NpcTemplateId` FK.
	//
	// NB the LEGACY `Npc`/`Npcs` table (the old C++-port static NPC defs) was dropped 2026-06-11 by
	// `DropLegacyNpcTables`; this fresh type table originally reused that freed `Npc` name and was renamed to
	// `NpcTemplate` (#350) to name it for what it is. The capture-derived spawn rows are `NpcSpawn` (table `NpcSpawns`).
	public class NpcTemplate
	{
		// The creature's death-animation behaviorId (the death BccIIR field-1, #362) - harvested per type from captured
		// NPC deaths (Faolan.Importer NpcDeathBehaviorExtractor): 41877 = humanoid biped, 17036 = demon/large caster,
		// 215333 = quadruped/beast. NULL = un-harvested type; the runtime falls back to the humanoid 41877.
		public uint? DeathBehaviorId { get; set; }

		[ConstrainToEnumValues]
		public LootContainerTemplateId LootContainerTemplateId { get; set; } = LootContainerTemplateId.Bag;

		// The stable per-type identity (NpcStats.f4 / NpcSpawn.TemplateId) - the PRIMARY KEY (set from the RDB by the
		// importer, never DB-generated; see DatabaseContext). One NpcTemplate row per template.
		public uint TemplateId { get; set; }

		// Ambient chatter bark lines this NPC type periodically says (per-NPC; see NpcChatter).
		public virtual ICollection<NpcChatter> Chatter { get; set; } = [];

		// The type-level worn equipment + appearance/morph table (#307), for a summon-only creature that has no NpcSpawn
		// placement to carry them - the per-TYPE counterpart of NpcSpawnEquipment/NpcSpawnMorph. Empty for a plain map
		// NPC type (its placements carry these). TryBuildFromType builds the spawn body from these + StatCore.
		public virtual ICollection<NpcEquipment> Equipment { get; set; } = [];

		public virtual ICollection<NpcMorph> Morphs { get; set; } = [];

		// The placements of this type across all maps.
		public virtual ICollection<NpcSpawn> Spawns { get; set; } = [];

		// The spells this NPC type casts (#232) - now FK'd to the type instead of joined by name string.
		public virtual ICollection<NpcSpell> Spells { get; set; } = [];

		// The type-level CharacterIIR stat core (#244), lifted off the captured body into a 1:1 NpcStatCore side table
		// (keyed by NpcTemplateId, like Character <-> CharacterMaxStats). Null until lifted; see NpcStatCore + the three-tier doc.
		// This is what lets a SUMMON-ONLY creature (pet/mount, #307) build its spawn body from the type ALONE, with a
		// caller-supplied transform and NO world placement (NpcSpawnBuilder.TryBuildFromType).
		public virtual NpcStatCore? StatCore { get; set; }

		public virtual Shop? Shop { get; set; }

		// The creature's display name (the stable join key conversations/shops/spells use).
		[MaxLength(128)]
		public string Name { get; set; } = null!;
	}
}
