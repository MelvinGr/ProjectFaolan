using System.Collections.Generic;

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
		// The stable per-type identity (NpcStats.f4 / NpcSpawn.TemplateId). Unique - one Npc row per template.
		public uint TemplateId { get; set; }

		// The creature's display name (the stable join key conversations/shops/spells use).
		public string Name { get; set; }

		// Faction/community (NpcStats.community): 0 = neutral/friendly, > 0 = a hostile faction.
		public uint Community { get; set; }

		// Type-level appearance identity (#287): the creature's race and sex code, the same across every placement of
		// this type (moved off NpcSpawn, where it was duplicated per instance). Sourced from the captured
		// CharacterStats.race / CharacterStats.sex (sex code = the Sex enum + 2: MALE=2 / FEMALE=3).
		public uint Race { get; set; }
		public uint Sex { get; set; }

		// The placements of this type across all maps.
		public virtual ICollection<NpcSpawn> Spawns { get; set; } = new List<NpcSpawn>();

		// The spells this NPC type casts (#232) - now FK'd to the type instead of joined by name string.
		public virtual ICollection<NpcSpell> Spells { get; set; } = new List<NpcSpell>();
	}
}
