using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A captured NPC entity spawn (FA015807) on a map. Keyed for dedup by (MapId, Position) - the stable spawn
	// point; the entity handle is session-scoped. Imported from captures by Faolan.Importer (NpcSpawnExtractor).
	//
	// The whole spawn body is now CONSTRUCTED from typed columns (#243/#244): the transform from the columns below,
	// the pre-IIR/post-IIR scaffolds from scratch, the stat core from NpcTemplate.StatCore, equipment from NpcSpawnEquipment,
	// morphs from NpcSpawnMorph, and a no-morph spawn's post-IIR recordCount from NpcTemplate.StatCore.MinimalPostIirWord2.
	// The captured FA015807 `Data` blob has been RETIRED - every shipped spawn builds from columns (988/988), so the
	// column was dropped; the importer lifts these rows from the dump body at import time (never persisting it).
	// The structured columns below are LIFTED from the ProtoCoreCharacterIIR: NPC identity/appearance
	// (TemplateId/Race/Sex/Level/HeadMesh/...). Transform (PositionX/Y/Z + RotationX/Y/Z/W + the Position/Rotation
	// accessors) is inherited from Base3DEntity, shared with the player Character and the world objects.
	// A placement's identity is WHERE it is: (map, position). That was already the declared unique index, and it
	// is now the source of the primary key via ComputeId - the npcs module wipes and rebuilds the whole spawn set
	// on every re-harvest, so an AUTOINCREMENT id renumbered all 1086 rows (~15 times over) and dragged the
	// equipment/morph/patrol children with it. A DERIVED id rather than a composite key because the identity
	// contains floats and this is the root of a three-level tree - see DerivedKey.
	// On a CLASS-SPECIFIC map (Map.ClassSpecific - the city armories) the identity carries the class as well: the
	// same spot holds a different NPC per class, and two of them legitimately share a position.
	[Index(nameof(MapId), nameof(PositionX), nameof(PositionY), nameof(PositionZ), nameof(ClassId), IsUnique = true)]
	public class NpcSpawn : Base3DEntity, IDerivedKey
	{
		public static uint ComputeId(uint mapId, float x, float y, float z, ConanClass? classId)
		{
			// The class is appended ONLY when set, so every class-agnostic spawn (all but the armories) keys exactly
			// as it did before the column existed. Folding it in unconditionally would have renumbered ~1000 rows and
			// their equipment/morph/patrol children for zero semantic change - the churn the derived key exists to avoid.
			return classId is { } cls
				? DerivedKey.From(mapId, x, y, z, (byte)cls)
				: DerivedKey.From(mapId, x, y, z);
		}

		public uint ComputeId()
		{
			return ComputeId(MapId, PositionX, PositionY, PositionZ, ClassId);
		}

		public uint? GuildId { get; set; }
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The CLASS this placement belongs to, on a map whose population is per-class (Map.ClassSpecific). Null = the
		// normal case: the spawn is visible to every class.
		//
		// Proven on the Conarch Armory (5007), the class-specific instance behind Conarch Village's `Armory` door: the
		// two captures that enter it - Hakotep (Herald of Xotli) and NecroL80 (Necromancer) - see DISJOINT quartermaster
		// sets (Chuma/Imogen/Ofir/Sabola vs Akila/Ebo/Pancras/Zahur), with only Haji and the two dummies shared. Both
		// are the MAGE archetype, so the split is per CLASS, not per archetype. Without this column the importer merged
		// both sets onto one map and every class was served whichever capture won the election.
		public ConanClass? ClassId { get; set; }

		// #363 MEMBERSHIP of an RDB ambient spawn AREA: set when this captured spawn is confirmed to belong to a spawn
		// group the area's pool replaces (by the resolver's auto-link, or by hand via `.setnpcgroup`). `.graduatenpcgroup`
		// then just deletes the rows linked to an area (a clean FK delete, no bounds re-derivation). Null = an
		// independent placement (named/quest NPC, or a mob no area owns). SetNull on area delete, so a re-import that
		// wipes the areas simply un-links these rather than deleting them.
		public uint? NpcSpawnAreaId { get; set; }

		[ForeignKey(nameof(NpcSpawnAreaId))]
		public virtual NpcSpawnArea? NpcSpawnArea { get; set; }

		// The NPC TYPE this placement is an instance of (#244): the type identity (TemplateId, Name, Community) lives
		// on NpcTemplate now, reached via the NpcTemplate navigation. Required - a real spawn always resolves to a type (the phantom
		// rows that once lacked one were scrubbed).
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The worn equipment (CharacterIIR f23) lifted from the captured spawn (#185) - empty for unarmed NPCs.
		// When present, NpcSpawnBuilder rebuilds the spawn's inventory_entry list from these rows instead of the
		// captured Data blob's (the NPC counterpart of the player's DB-sourced equipment, PlayerSpawnEquipment).
		public virtual ICollection<NpcSpawnEquipment> Equipment { get; set; } = [];

		// Race/Sex moved to the NpcTemplate TYPE table (#287): they are type-level identity (every placement of a type shares
		// them), so they live on `NpcTemplate` now and are reached via the `NpcTemplate` navigation.

		// The humanoid appearance / morph table lifted from the captured post-IIR (#185) - empty for animals, which
		// carry no such table. When present, NpcSpawnBuilder generates the post-IIR from these rows instead of
		// keeping the captured blob's (the from-DB lift of the bald-fix's verbatim post-IIR, #243).
		public virtual ICollection<NpcSpawnMorph> Morphs { get; set; } = [];

		// The captured patrol route this spawn walks (#316) - the columnarized MoveTask task tree, empty for a
		// stationary NPC. Held DIRECTLY: the PatrolPath table that used to sit between them carried nothing but this
		// collection and the spawn's own id once it was keyed by the spawn, so it was a join with no content of its
		// own. Cascade-deletes with the spawn.
		public virtual ICollection<PatrolNode> PatrolNodes { get; set; } = [];

		public bool HasEquipment { get; set; }
		public uint ApprenticeLevel { get; set; }

		// Per-PLACEMENT stat values (#244), lifted from this spawn's capture, overriding the type-level NpcTemplate.StatCore
		// when the spawn body is built. A spawn point carries its own LEVEL (the same template can be placed at a range
		// of levels - e.g. Starved Watchdog L65-79), and the pools track that level. These are the spawn/MAX values,
		// NOT per-instance runtime state (not current HP); AoC has no scale-to-player scaling.
		//
		// The pools are heavily but NOT cleanly redundant: Life == Health*100 (the on-wire x100 vitals scale) for ~91%
		// of spawns and the five pools are usually all equal, but creatures deviate (Panther: Health 181 -> Life 18190,
		// not 18100, and its mana/stamina pools differ from Life). So they can't be byte-exactly DERIVED from Health/
		// Level with a formula - hence stored. TODO (#244 follow-up): if a per-creature curve is ever decoded, these
		// could be derived and dropped. Level/VoiceSet/mesh are genuinely per-placement.
		//
		// Present on EVERY captured spawn (all 510 types), so non-nullable. Only GuildId/HearingSkill/SightSkill are
		// sparse (set on a minority of types) and stay nullable = "unset on the wire, off the rebuilt body".
		public uint HeadMesh { get; set; }
		public uint Health { get; set; }
		public uint Level { get; set; }
		public uint Life { get; set; }
		public uint Mana { get; set; }
		public uint MaxMana { get; set; }
		public uint MaxStamina { get; set; }
		public uint MonsterData { get; set; }
		public uint MonsterScale { get; set; }
		public uint Stamina { get; set; }
		public uint VoiceSet { get; set; }
		public uint? HearingSkill { get; set; }
		public uint? SightSkill { get; set; }
		public uint? SmellingSkill { get; set; }

		// The WIRE CharacterStats.Health (×100), stored ONLY when it diverges from Life - which it almost never does
		// (wire Health == wire Life for ~all creatures, both tracking the placement level). A few (e.g. Rocknose) fix
		// wire Health to a distinct per-placement value; this captures it. Null = "wire Health == Life", use Life.
		public uint? WireHealth { get; set; }
	}
}
