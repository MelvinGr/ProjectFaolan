using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using Faolan.Core.Database.Interfaces;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	// A map has no position/rotation of its own (it IS the coordinate space), so it inherits BaseEntity, not
	// Base3DEntity. The "default spawn" for a map is derived from its respawn points (the AlwaysKnown starter, else
	// the first point) via IDatabaseRepository.GetDefaultSpawn - see DoTeleport / RespawnPlayer.
	public class Map : BaseEntity
	{
		// Tortage Beach (0xFAA/4010) - the universal fallback world. Its row and world burst are always
		// seeded, so it is the safe default wherever a character needs a map (creation, import, world entry).
		public const uint TortageBeachId = 0x00000FAA;

		// The default resurrection anchor advertised on the login 0x2024 (= DispatchValidResurrectionSpots) when a map
		// defines no real RespawnPoint. A TRANSIENT carrier (never persisted; Id 0 = no DB point, not a real spawn id)
		// positioned at the Tortage Beach new-player spawn. Maps with their own RespawnPoints advertise their real
		// RespawnPoint.Id instead (the id RespawnMe echoes back).
		public static readonly RespawnPoint NewPlayerSpawnSpawnPoint = new()
		{
			Id = 0,
			MapId = TortageBeachId,

			// The Tortage Beach new-player intro spawn (map 4010), bit-exact (exact IEEE-754 bits) from the create
			// world-entry MapChange / 2nd 0x2024 ReadyForPlayScreen (dikketest & randochar create dumps). Single source of
			// truth for the freshly-created character's start location - reused by SetCreationSpawn (DatabaseRepository) and
			// as the default resurrection anchor (DefaultResurrection).
			Position = new Vector3(BitConverter.Int32BitsToSingle(0x43A14000), // 322.5
				BitConverter.Int32BitsToSingle(0x43114FB3), // ~145.311
				BitConverter.Int32BitsToSingle(0x4430399A)), // ~704.9

			AlwaysKnown = true,
			Name = "Tortage Beach"
		};

		// The map's loot pool (#273): the items its captured bags carried, that kills on this map roll random drops from.
		public virtual ICollection<MapLootEntry> LootEntries { get; set; } = [];
		public virtual ICollection<NpcSpawn> NpcSpawns { get; set; } = [];
		public virtual ICollection<RespawnPoint> RespawnPoints { get; set; } = [];

		public virtual ICollection<WorldObject> Objects { get; set; } = [];

		// How the live world instances this map (#268). Defaults to Shared so every existing map keeps its single
		// global zone; the assignment policy only mints per-character/per-party copies for maps flagged otherwise.
		public MapInstanceMode InstanceMode { get; set; } = MapInstanceMode.Shared;

		// word3 = the player instance when instanced (beach), else 0 (shared/mainland). NB this is the WIRE-layer
		// counterpart of InstanceMode (#268): same concept (is this playfield instanced?) but a different layer -
		// InstanceMode drives the server's zone-COPY policy, this only sets the client-facing PlayfieldFIIR word.
		// They should fold into one (word3 = InstanceMode != Shared ? instance : 0) once the beach/Tortage maps are
		// modelled as instanced in InstanceMode; kept separate for now so this stays a wire change (no zone-copy side effect).
		public bool PlayfieldInstanced { get; set; } = true;

		// Max players per Shared instance before the policy spins up another copy. 0 = uncapped (and ignored for
		// Solo/Group, which are sized by character/party).
		public int PopulationCap { get; set; }

		// This map's POPULATION is per CLASS: the same playfield holds a different NPC set depending on the entering
		// character's class, so its spawns carry NpcSpawn.ClassId and only the matching ones are loaded into a copy.
		// The city armories are the case (Conarch/Khemi/Tarantia); a class-specific map is necessarily InstanceMode.Solo,
		// because two classes cannot share one zone copy of it.
		//
		// ⚠️ Only the Conarch Armory (5007) is capture-proven - see NpcSpawn.ClassId. Khemi (5001) and Tarantia (5013)
		// are the same fixture in the other two cities and are flagged on that basis; neither has any import at all, so
		// they behave as "not imported" for every class either way and the flag costs nothing if the inference is wrong.
		public bool ClassSpecific { get; set; }

		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// The per-map PlayfieldFIIR (0x5D85BFC7 MapChange) rule fields (#308). This packet is the playfield the client
		// enters, and the client keys per-area rules on it (mount permission FIXED via PlayfieldFlags). The values are
		// NOT universal - they differ beach vs mainland; sourced per-map from the capture. Defaults (set by the
		// migration) are the beach/Tortage values so un-sourced maps behave as before; Old Tarantia (mainland) carries
		// the "shared world" set. See WorldEntryGeneratedRecords.BuildMapChange / CreateEditorRecordBuilder.BuildPlayfieldFiir.
		// ⚠️ FieldA and FieldC are PER-MAP with no exceptions - across all 151 captured MapChange records every one for
		// a given map carries the same pair, which is what identifies them as playfield properties rather than
		// per-entry state (#451). Both were hardcoded before that (0 and 7), so six of the twelve captured maps -
		// 1010, 2010, 2040, 4022, 5007 and 11040 - were being sent a wrong value.
		//
		// In the decoded structures (docs/subsystems/teleport-changemap.md): FieldA is the PlayfieldProxy's u32[2] -
		// the slot between the map id (u32[1]) and the character instance (u32[3]) - and FieldC is the THIRD of the
		// four trailing words PlayfieldFIIR's own reader takes (FUN_008f36e0 -> obj+0x88), the one before the flags.
		public uint PlayfieldFieldA { get; set; } // PlayfieldProxy u32[2] (0 on most maps, 2 on 2040/4022/5007)
		public uint PlayfieldCount { get; set; } = 2; // the PlayfieldProxy "count" word (beach 2, Old Tarantia 1)
		// ⚠️ SUSPECT - unlike its neighbours this is almost certainly NOT a map property, so do not source new values
		// for it and do not trust the ones here (#451). The corpus says a per-map value cannot be right: across the 151
		// captured MapChange records fieldB takes 0/1/2/3, and **10 of the 12 captured maps carry more than one of
		// them** - map 4010 alone shows all four (0 x27, 3 x8, 2 x8, 1 x4). The two that look constant (4000, 4022)
		// just have the fewest samples. That is the exact opposite of the test that identified FieldA/FieldC as
		// playfield properties, where every record for a given map agreed.
		//
		// It is not an obvious sequence either: of the 39 captures carrying more than one MapChange, ordering them by
		// frame gives 0,1,2,… in exactly ONE - captures begin at 2 or 3, repeat a value, and go 3 -> 0 - so a
		// per-session counter is ruled out too. Every one of the 151 is s2c, so direction does not explain it.
		// Undecoded; it is per-ENTRY state of some kind, and this column is the wrong shape for it.
		//
		// Left in place because it is what we send and changing it is a live wire change on the map-change path; 529
		// of the 530 rows carry the default 3 (Old Tarantia is 0), so today it is effectively still the constant it
		// was before it became a column.
		public uint PlayfieldFieldB { get; set; } = 3; // trailer word before the FFFF pair - see the warning above
		public uint PlayfieldFieldC { get; set; } = 7; // the word before the flags (7 on most maps, 0 on 1010/2010/2040/5007, 6 on 11040)
		public uint PlayfieldFlags { get; set; } // the final PlayfieldFIIR flag word (beach 0, Old Tarantia 1 - the mount-rule bit)

		// The map's real playfield-instance token (#305) - the value the client scopes the character to when it loads
		// this playfield. The client keys per-area behaviour on it (e.g. mount permission: a wrong token makes a
		// mountable zone read as no-mount). Sourced from the map's capture (the despawn/snapshot token, e.g. Old
		// Tarantia = 0x00507927). 0 = unknown → the world-entry falls back to WorldState.DefaultPlayfieldToken (beach).
		public uint PlayfieldToken { get; set; }
	}

	// How the live world creates copies (instances) of a map (#268). Retail's playfield-instance token is the
	// instance id; this column tells the assignment policy how many copies to mint and how to route players to them.
	public enum MapInstanceMode
	{
		// One shared instance for everyone (capped by PopulationCap; 0 = uncapped). The default open-world behaviour
		// and the historical model (a single global Zone per map).
		Shared = 0,

		// One instance per character - a solo/destiny playfield (e.g. the Tortage beach). No one else is ever placed
		// in your copy.
		Solo = 1,

		// One instance per party (dungeons): party members route to the same copy, a new party mints a new one.
		Group = 2
	}
}
