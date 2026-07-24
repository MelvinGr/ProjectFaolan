using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;

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

		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// The per-map PlayfieldFIIR (0x5D85BFC7 MapChange) rule fields (#308). This packet is the playfield the client
		// enters, and the client keys per-area rules on it (mount permission FIXED via PlayfieldFlags). The values are
		// NOT universal - they differ beach vs mainland; sourced per-map from the capture. Defaults (set by the
		// migration) are the beach/Tortage values so un-sourced maps behave as before; Old Tarantia (mainland) carries
		// the "shared world" set. See WorldEntryGeneratedRecords.BuildMapChange / CreateEditorRecordBuilder.BuildPlayfieldFiir.
		public uint PlayfieldCount { get; set; } = 2; // the PlayfieldProxy "count" word (beach 2, Old Tarantia 1)
		public uint PlayfieldFieldB { get; set; } = 3; // trailer word before the FFFF pair (beach 0x3, Old Tarantia 0x0)
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
