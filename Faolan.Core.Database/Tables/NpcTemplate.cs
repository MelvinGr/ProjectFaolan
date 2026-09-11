using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	// An NPC *type* - the type-level identity shared by every placement of the same creature. `NpcSpawn` is
	// the per-placement instance (transform + per-spawn appearance/equipment rolls); the type half (TemplateId, name,
	// community, and the type-level stat core) lives here once instead of being duplicated across every spawn (e.g.
	// Pillager 8756 is placed 85x). The primary key is `TemplateId` (NpcStats.f4, the stable RDB per-type id) - a
	// natural-key catalog like Item/Spell/Map (the RDB is the source of truth, so the stable RDB id is the key, not a
	// volatile DB-generated surrogate). Children reference it by their `NpcTemplateId` FK.
	// The capture-derived spawn rows are `NpcSpawn` (table `NpcSpawns`).
	public class NpcTemplate
	{
		// The creature's death-animation behaviorId (the death BccIIR field-1) - harvested per type from captured
		// NPC deaths (Faolan.Importer NpcDeathBehaviorExtractor): 41877 = humanoid biped, 17036 = demon/large caster,
		// 215333 = quadruped/beast. Null = un-harvested type; the runtime falls back to the humanoid 41877.
		public uint? DeathBehaviorId { get; set; }

		// How long that death animation runs for this creature, in centiseconds - the death record's `ActivationTime`
		// (field 12), harvested the same way.
		//
		// It is the creature's, not the animation's, and the corpus says so cleanly: on animation 17036 the field
		// takes eight values over 52 records, and resolving every actor gives 22 creatures, 22 of which carry one
		// value and none of which carries two. The families corroborate it - Striped Boar and Wild Boar both 176,
		// Wild Cow and Wild Pig both 160, Valley Yeti 816, Moose 120 - which is not what noise looks like.
		//
		// Null = un-harvested (or a creature whose only records carried a zero); the runtime then falls back to
		// `DeathSequence.ActivationTimeFor`, the animation's modal value.
		public uint? DeathActivationTime { get; set; }

		[ConstrainToEnumValues]
		public LootContainerTemplateId LootContainerTemplateId { get; set; } = LootContainerTemplateId.Bag;

		// The stable per-type identity (NpcStats.f4 / NpcSpawn.TemplateId) - the primary key (set from the RDB by the
		// importer, never DB-generated; see DatabaseContext). One NpcTemplate row per template.
		[Key]
		public uint TemplateId { get; set; }

		// Ambient idle animations this NPC type performs while standing about (see NpcAmbientAnimation). Empty
		// for a type no capture ever caught idling - it then stands still rather than being given a guessed animation.
		public virtual ICollection<NpcAmbientAnimation> AmbientAnimations { get; set; } = [];

		// The creature types this one walks with (see NpcFollowPair). Empty for the overwhelming majority -
		// a creature with no captured formation-follow keeps its own path (or stands where it was placed).
		public virtual ICollection<NpcFollowPair> Follows { get; set; } = [];

		// Ambient chatter bark lines this NPC type periodically says (per-NPC; see NpcChatter).
		public virtual ICollection<NpcChatter> Chatter { get; set; } = [];

		// The type-level worn equipment + appearance/morph table, for a summon-only creature that has no NpcSpawn
		// placement to carry them - the per-type counterpart of NpcSpawnEquipment/NpcSpawnMorph. Empty for a plain map
		// NPC type (its placements carry these). TryBuildFromType builds the spawn body from these + StatCore.
		public virtual ICollection<NpcEquipment> Equipment { get; set; } = [];

		public virtual ICollection<NpcMorph> Morphs { get; set; } = [];

		// The placements of this type across all maps.
		public virtual ICollection<NpcSpawn> Spawns { get; set; } = [];

		// The spells this NPC type casts - now FK'd to the type instead of joined by name string.
		public virtual ICollection<NpcSpell> Spells { get; set; } = [];

		// The type-level CharacterIIR stat core, lifted off the captured body into a 1:1 NpcStatCore side table
		// (keyed by NpcTemplateId, like Character <-> CharacterMaxStats). Null until lifted; see NpcStatCore + the three-tier doc.
		// This is what lets a summon-only creature (pet/mount) build its spawn body from the type alone, with a
		// caller-supplied transform and no world placement (NpcSpawnBuilder.TryBuildFromType).
		public virtual NpcStatCore? StatCore { get; set; }

		public virtual Shop? Shop { get; set; }

		// The creature's display name (the stable join key conversations/shops/spells use).
		[MaxLength(128)]
		public string Name { get; set; } = null!;
	}
}
