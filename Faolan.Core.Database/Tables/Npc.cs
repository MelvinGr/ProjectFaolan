using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// An NPC *type* - the type-level identity shared by every placement of the same creature (#244). `NpcSpawn` is
	// the per-placement INSTANCE (transform + per-spawn appearance/equipment rolls); the type half (TemplateId, name,
	// community, and - in a later step - the type-level stat core) lives here once instead of being duplicated across
	// every spawn (e.g. Pillager 8756 is placed 85x). Keyed by `TemplateId` (NpcStats.f4, the stable per-type id).
	//
	// NB unrelated to the LEGACY `Npc`/`Npcs` table (the old C++-port static NPC defs) dropped 2026-06-11 by
	// `DropLegacyNpcTables`; this is a fresh type table reusing the now-free name. The capture-derived spawn rows are
	// `NpcSpawn` (table `NpcSpawns`).
	public class Npc : BaseEntity
	{
		// The placements of this type across all maps.
		public virtual ICollection<NpcSpawn> Spawns { get; set; } = new List<NpcSpawn>();

		// The spells this NPC type casts (#232) - now FK'd to the type instead of joined by name string.
		public virtual ICollection<NpcSpell> Spells { get; set; } = new List<NpcSpell>();

		// The type-level CharacterIIR stat core (#244), lifted off the captured body into a 1:1 NpcStatCore side table
		// (keyed by NpcId, like Character <-> CharacterMaxStats). Null until lifted; see NpcStatCore + the three-tier doc.
		// This is what lets a SUMMON-ONLY creature (pet/mount, #307) build its spawn body from the type ALONE, with a
		// caller-supplied transform and NO world placement (NpcSpawnBuilder.TryBuildFromType).
		public virtual NpcStatCore StatCore { get; set; }

		// The type-level worn equipment + appearance/morph table (#307), for a summon-only creature that has no NpcSpawn
		// placement to carry them - the per-TYPE counterpart of NpcSpawnEquipment/NpcSpawnMorph. Empty for a plain map
		// NPC type (its placements carry these). TryBuildFromType builds the spawn body from these + StatCore.
		public virtual ICollection<NpcEquipment> Equipment { get; set; } = new List<NpcEquipment>();
		public virtual ICollection<NpcMorph> Morphs { get; set; } = new List<NpcMorph>();

		// The creature's display name (the stable join key conversations/shops/spells use).
		[MaxLength(128)]
		public string Name { get; set; }

		// Community/Level/Race/Sex are type-level identity that now live on the 1:1 StatCore side table
		// (StatCore.Community / .Level / .Race / .Sex) - the old duplicate Npc columns were retired.

		// The stable per-type identity (NpcStats.f4 / NpcSpawn.TemplateId). Unique - one Npc row per template.
		public uint TemplateId { get; set; }
	}
}
