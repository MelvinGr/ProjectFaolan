using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     A captured NPC entity spawn (FA015807) on a map. Imported from captures by Faolan.Importer
	///     (NpcSpawnExtractor).
	/// </summary>
	/// <remarks>
	///     The whole spawn body is constructed from typed columns: the transform from the columns below, the
	///     pre-IIR/post-IIR scaffolds from scratch, the stat core from NpcTemplate.StatCore, equipment from
	///     NpcSpawnEquipment, morphs from NpcSpawnMorph, and a no-morph spawn's post-IIR recordCount from
	///     NpcTemplate.StatCore.MinimalPostIirWord2. Every shipped spawn builds from columns (988/988), so nothing
	///     of the captured FA015807 body is persisted as a blob. The structured columns below are lifted from the
	///     ProtoCoreCharacterIIR: NPC identity/appearance (TemplateId/Race/Sex/Level/HeadMesh/...). Transform
	///     (PositionX/Y/Z + RotationX/Y/Z/W + the Position/Rotation accessors) is inherited from
	///     <see cref="Base3DEntity" />, shared with the player Character and the world objects.
	///     <para>
	///         A placement's identity is where it is: (map, position). That is the unique index and, through
	///         ComputeId, the source of the primary key - the npcs module wipes and rebuilds the whole spawn set on
	///         every re-harvest, so a store-generated id would renumber every row and drag the equipment/morph/patrol
	///         children with it. A derived id rather than a composite key because the identity contains floats and
	///         this is the root of a three-level tree - see DerivedKey.
	///     </para>
	///     <para>
	///         On a class-specific map (Map.ClassSpecific - the city armories) the identity carries the class as
	///         well: the same spot holds a different NPC per class, and two of them legitimately share a position.
	///     </para>
	/// </remarks>
	[Index(nameof(MapId), nameof(PositionX), nameof(PositionY), nameof(PositionZ), nameof(ClassId), IsUnique = true)]
	public class NpcSpawn : Base3DEntity, IDerivedKey
	{
		public static uint ComputeId(uint mapId, float x, float y, float z, ConanClass? classId)
		{
			// The class is appended only when set, so every class-agnostic spawn (all but the armories) keys exactly
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

		// The class this placement belongs to, on a map whose population is per-class (Map.ClassSpecific). Null = the
		// normal case: the spawn is visible to every class.
		//
		// Proven on the Conarch Armory (5007), the class-specific instance behind Conarch Village's `Armory` door: the
		// two captures that enter it - Hakotep (Herald of Xotli) and NecroL80 (Necromancer) - see disjoint quartermaster
		// sets (Chuma/Imogen/Ofir/Sabola vs Akila/Ebo/Pancras/Zahur), with only Haji and the two dummies shared. Both
		// are the mage archetype, so the split is per class, not per archetype. Without this column the importer merged
		// both sets onto one map and every class was served whichever capture won the election.
		public ConanClass? ClassId { get; set; }

		// Membership of an RDB ambient spawn area: set when this captured spawn is confirmed to belong to a spawn
		// group the area's pool replaces (by the resolver's auto-link, or by hand via `.setnpcgroup`).
		// `.graduatenpcgroup` then just deletes the rows linked to an area (a clean FK delete, no bounds
		// re-derivation). Null = an independent placement (named/quest NPC, or a mob no area owns). SetNull on area
		// delete, so a re-import that wipes the areas simply un-links these rather than deleting them.
		public uint? NpcSpawnAreaId { get; set; }

		[ForeignKey(nameof(NpcSpawnAreaId))]
		public virtual NpcSpawnArea? NpcSpawnArea { get; set; }

		// The NPC type this placement is an instance of: the type identity (TemplateId, Name, Community) lives on
		// NpcTemplate, reached via the NpcTemplate navigation. Required - a real spawn always resolves to a type.
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The worn equipment (CharacterIIR f23) lifted from the captured spawn - empty for unarmed NPCs.
		// When present, NpcSpawnBuilder rebuilds the spawn's inventory_entry list from these rows instead of the
		// captured Data blob's (the NPC counterpart of the player's DB-sourced equipment, PlayerSpawnEquipment).
		public virtual ICollection<NpcSpawnEquipment> Equipment { get; set; } = [];

		// Race and Sex are type-level identity (every placement of a type shares them), so they live on
		// `NpcTemplate` and are reached via the `NpcTemplate` navigation.

		// The humanoid appearance / morph table lifted from the captured post-IIR - empty for animals, which
		// carry no such table. When present, NpcSpawnBuilder generates the post-IIR from these rows.
		public virtual ICollection<NpcSpawnMorph> Morphs { get; set; } = [];

		// The captured patrol route this spawn walks - the columnarized MoveTask task tree, empty for a
		// stationary NPC. Held directly, with no join table in between. Cascade-deletes with the spawn.
		public virtual ICollection<PatrolNode> PatrolNodes { get; set; } = [];

		// The placement this row is a sighting of - rows sharing one are the same NPC seen in different capture
		// sessions, not different NPCs. Null for a spawn that carries no route (a placement we cannot yet identify).
		//
		// The importer keys captured spawns on MapId + exact xyz, which is the one key guaranteed to fail for a
		// patroller: it stands somewhere different every time it is observed, so each sighting minted a placement. Old
		// Tarantia ended up with two Noblemen walking one beat a few units apart, and repo-wide 231 of 857 route-
		// carrying spawns are redundant.
		//
		// The beat identifies the placement, so this is derived from the harvested route (plus the type - two creatures
		// of different types could in principle be given the same path). Measured before relying on it: grouping the 857
		// route-carrying spawns by (length, segment count, first waypoint) and by the full polyline both yield 626
		// groups, and the cheap key never collapses more than the exact one.
		//
		// The redundant rows are not deleted, because what differs between them is exactly what retail re-rolls per
		// spawn: level, the morph table and all 7 equipment slots (two Old Tarantia Noblemen share a beat but differ in
		// 23 of 27 morphs and every worn garment). They are this placement's variant pool, and the world picks one at
		// spawn - which is what makes a re-spawned NPC look different, as retail's do.
		//
		// Safe because retail never runs two NPCs on one beat: across 116 captures and 640 distinct stored routes there
		// is not one case of two concurrent same-name NPCs both standing on the same polyline (3u tolerance). Note that
		// proximity proves nothing here - retail routinely places same-name NPCs 1.4u apart - which is why the test is
		// route occupancy rather than distance.
		public uint? PlacementId { get; set; }

		public bool HasEquipment { get; set; }
		public uint ApprenticeLevel { get; set; }

		// Per-placement stat values, lifted from this spawn's capture, overriding the type-level NpcTemplate.StatCore
		// when the spawn body is built. A spawn point carries its own level (the same template can be placed at a range
		// of levels - e.g. Starved Watchdog L65-79), and the pools track that level. These are the spawn/max values,
		// not per-instance runtime state (not current HP); AoC has no scale-to-player scaling.
		//
		// The pools are heavily but not cleanly redundant: Life == Health*100 (the on-wire x100 vitals scale) for ~91%
		// of spawns and the five pools are usually all equal, but creatures deviate (Panther: Health 181 -> Life 18190,
		// not 18100, and its mana/stamina pools differ from Life). So they cannot be byte-exactly derived from Health/
		// Level with a formula - hence stored. Level/VoiceSet/mesh are genuinely per-placement.
		//
		// Present on every captured spawn (all 510 types), so non-nullable. Only GuildId/HearingSkill/SightSkill are
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

		// The wire CharacterStats.Health (×100), stored only when it diverges from Life - which it almost never does
		// (wire Health == wire Life for ~all creatures, both tracking the placement level). A few (e.g. Rocknose) fix
		// wire Health to a distinct per-placement value; this captures it. Null = "wire Health == Life", use Life.
		public uint? WireHealth { get; set; }
	}
}
