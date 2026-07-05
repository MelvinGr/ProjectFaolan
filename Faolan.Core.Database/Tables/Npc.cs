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

		// The type-level CharacterIIR stat CORE source (CharacterStats / NpcStats / Bcc) - the SAME kind of captured
		// FA015807 body NpcSpawn.Data holds, but lifted to the TYPE (#244) so a creature can be spawned from its type
		// ALONE, with a caller-supplied transform and NO world placement. This is what a summoned PET needs (#307):
		// SummonPetForPlayer builds the spawn from a creature's type at the player's position. Populated once per type
		// (from a representative NpcSpawn.Data, or - for summon-only creatures like the Deathless Acolyte pet - imported
		// straight from a pet-summon capture). Null for types not yet lifted (those still spawn via NpcSpawn.Data).
		public byte[] Data { get; set; }

		// The creature's display name (the stable join key conversations/shops/spells use).
		[MaxLength(128)]
		public string Name { get; set; }

		// Faction/community (NpcStats.community): 0 = neutral/friendly, > 0 = a hostile faction.
		public uint Community { get; set; }

		// Type-level level (drives the pet's max-health when spawned from the type). 0 = derive from Data / unknown.
		public uint Level { get; set; }

		// Deliberately raw uint, NOT the player Race/Sex enums, despite the names - a different domain: this is a
		// CREATURE model-race code (observed 0..98, far beyond the 3 playable races Aquilonian/Cimmerian/Stygian), so
		// the 3-value Race enum (and its [ConstrainToEnumValues] check) would reject most values. Don't "fix" the type.
		public uint Race { get; set; }

		// Wire-encoded creature sex: 2 = MALE, 3 = FEMALE (= the Sex enum + 2), plus 0 for none. NOT the player Sex
		// enum (0/1), so it stays uint - see the Race note above.
		public uint Sex { get; set; }

		// The stable per-type identity (NpcStats.f4 / NpcSpawn.TemplateId). Unique - one Npc row per template.
		public uint TemplateId { get; set; }
	}
}
