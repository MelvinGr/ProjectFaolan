using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	// A map has no position/rotation of its own (it is the coordinate space), so it inherits BaseEntity, not
	// Base3DEntity. The "default spawn" for a map is derived from its respawn points (the AlwaysKnown starter, else
	// the first point) via IDatabaseRepository.GetDefaultSpawn - see DoTeleport / RespawnPlayer.
	public class Map : BaseEntity
	{
		// Tortage Beach (0xFAA/4010) - the universal fallback world. Its row and world burst are always
		// seeded, so it is the safe default wherever a character needs a map (creation, import, world entry).
		public const uint TortageBeachId = 0x00000FAA;

		// The default resurrection anchor advertised on the login 0x2024 (= DispatchValidResurrectionSpots) when a map
		// defines no real RespawnPoint. A transient carrier (never persisted; Id 0 = no DB point, not a real spawn id)
		// positioned at the Tortage Beach new-player spawn. Maps with their own RespawnPoints advertise their real
		// RespawnPoint.Id instead (the id RespawnMe echoes back).
		public static readonly RespawnPoint NewPlayerSpawnSpawnPoint = new RespawnPoint
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

		// The map's loot pool: the items its captured bags carried, that kills on this map roll random drops from.
		public virtual ICollection<MapLootEntry> LootEntries { get; set; } = [];
		public virtual ICollection<NpcSpawn> NpcSpawns { get; set; } = [];
		public virtual ICollection<RespawnPoint> RespawnPoints { get; set; } = [];

		public virtual ICollection<WorldObject> Objects { get; set; } = [];

		// How the live world instances this map. Defaults to Shared so every existing map keeps its single
		// global zone; the assignment policy only mints per-character/per-party copies for maps flagged otherwise.
		public MapInstanceMode InstanceMode { get; set; } = MapInstanceMode.Shared;

		// word3 = the player instance when instanced (beach), else 0 (shared/mainland). This is the wire-layer
		// counterpart of InstanceMode: the same concept (is this playfield instanced?) at a different layer -
		// InstanceMode drives the server's zone-copy policy, this only sets the client-facing PlayfieldFIIR word.
		// They can fold into one (word3 = InstanceMode != Shared ? instance : 0) once the beach/Tortage maps are
		// modelled as instanced in InstanceMode; kept separate so this stays a wire value with no zone-copy effect.
		public bool PlayfieldInstanced { get; set; } = true;

		// Max players per Shared instance before the policy spins up another copy. 0 = uncapped (and ignored for
		// Solo/Group, which are sized by character/party).
		public int PopulationCap { get; set; }

		// This map's population is per class: the same playfield holds a different NPC set depending on the entering
		// character's class, so its spawns carry NpcSpawn.ClassId and only the matching ones are loaded into a copy.
		// The city armories are the case (Conarch/Khemi/Tarantia); a class-specific map is necessarily InstanceMode.Solo,
		// because two classes cannot share one zone copy of it.
		//
		// Only the Conarch Armory (5007) is capture-proven - see NpcSpawn.ClassId. Khemi (5001) and Tarantia (5013)
		// are the same fixture in the other two cities and are flagged on that basis; neither has any import at all, so
		// they behave as "not imported" for every class either way and the flag costs nothing if the inference is wrong.
		public bool ClassSpecific { get; set; }

		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// Entering this map through a zone-line door raises retail's "Do you want to travel to <map>?"
		// confirmation box (DispatchScryShowMessageBox) first, and this is the per-string LDB key of the `<localized>`
		// element that box carries. Empty = the door teleports straight through, which is what half the captured doors
		// do.
		//
		// It is scoped to the destination map rather than to the door, because that is what the corpus groups by:
		// the 17 harvested door walk-throughs split cleanly by destination with no door disagreeing with another door
		// to the same map (both doors into Tarantia Noble District confirm and carry the same key; both directions of
		// the Spider Caverns / Old Tarantia / King Conan's Castle lines confirm inbound only). The key is a
		// destination-map-name-string key, which is the same statement from the other side. What the corpus cannot
		// rule out is a map that confirms from one door and not another - none is observed, and per-door storage would
		// have cost a column on 141 doors to express it.
		//
		// The box's other two fields are re-derivable and so are not columns: its `id` is this map's Id and its
		// category is always 520, the client's map-name category. 520 is plaintext, so the client never decrypts with
		// this key - it is carried to reproduce retail's bytes, not because the text needs it.
		public virtual ProtoDecryptionKey TravelConfirmKey { get; set; } = new ProtoDecryptionKey();

		// The per-map PlayfieldFIIR (0x5D85BFC7 MapChange) rule fields. This packet is the playfield the client
		// enters, and the client keys per-area rules on it (mount permission fixed via PlayfieldFlags). The values are
		// not universal - they differ beach vs mainland; sourced per-map from the capture. The defaults are the
		// beach/Tortage values, so an un-sourced map behaves as the beach does; Old Tarantia (mainland) carries the
		// "shared world" set. See WorldEntryGeneratedRecords.BuildMapChange.
		//
		// FieldA and FieldC are per-map with no exceptions - across all 151 captured MapChange records every one for
		// a given map carries the same pair, which is what identifies them as playfield properties rather than
		// per-entry state.
		//
		// In the decoded structures (docs/subsystems/teleport-changemap.md): FieldA is the PlayfieldProxy's u32[2] -
		// the slot between the map id (u32[1]) and the character instance (u32[3]) - and FieldC is the third of the
		// four trailing words PlayfieldFIIR's own reader takes (FUN_008f36e0 -> obj+0x88), the one before the flags.
		public uint PlayfieldFieldA { get; set; } // PlayfieldProxy u32[2] (0 on most maps, 2 on 2040/4022/5007)
		public uint PlayfieldCount { get; set; } = 2; // the PlayfieldProxy "count" word (beach 2, Old Tarantia 1)
		// Suspect - unlike its neighbours this is almost certainly not a map property, so do not source new values
		// for it and do not trust the ones here. It is the scene blob's last word, which is the company it keeps -
		// not `PlayfieldFieldA`/`PlayfieldFieldC`, which are per-map playfield properties. The corpus says a per-map
		// value cannot be right: across the 151 captured MapChange records it takes 0/1/2/3, and 10 of the 12 captured
		// maps carry more than one of them - map 4010 alone shows all four (0 x27, 3 x8, 2 x8, 1 x4). The two that
		// look constant (4000, 4022) just have the fewest samples. That is the exact opposite of the test that
		// identifies FieldA/FieldC as playfield properties, where every record for a given map agrees.
		//
		// It is not an obvious sequence either: of the 39 captures carrying more than one MapChange, ordering them by
		// frame gives 0,1,2,... in exactly one - captures begin at 2 or 3, repeat a value, and go 3 -> 0 - so a
		// per-session counter is ruled out too. Every one of the 151 is s2c, so direction does not explain it.
		//
		// Measured over all 132 dumps, which hold 218 of these bodies rather than 151, it is far more stable than
		// "per-entry" suggests: within one capture and one map it is constant in 174 of 187 groups. So it is
		// per-session-per-map state, not per-packet noise.
		//
		// Still not pinned, and these are ruled out by measurement, so do not re-walk them:
		//   * a counter mod 4 - 0 of the 49 multi-MapChange captures step +1;
		//   * arrival ordinal - the nth MapChange of a capture shows the same 0/1/2/3 mix at every n;
		//   * time of day or weather, the reading its position suggests (it is the last word of a scene blob whose
		//     other members are nearClip/farClip). Correlated against the GameTimeIIR nearest each of 215 records,
		//     all four values span the whole 0-24h day with medians 7.2/9.4/10.6/11.3;
		//   * the instance-owner shape - a lean, not a rule: personal instances carry 0 in 29 of 32, shared in 78 of
		//     138. Suggestive that 0 means "no extra scene selection", nothing more.
		//
		// The distribution: 0 on 135 of 218, and 1/2/3 near-equal at 27/27/29. Maps 1020 and 4020 never carry 0;
		// 1004, 1092, 2003, 2020, 2090, 2094, 4000, 4022, 4579 and 5009 carry only 0.
		// [V0: what the PlayfieldFIIR scene blob's last word selects]
		//
		// Left in place because it is what the map-change path sends and changing it is a live wire change; 529 of the
		// 530 rows carry the default 3 (Old Tarantia is 0), so it is effectively still a constant.
		public uint PlayfieldBlobLastWord { get; set; } = 3; // the scene blob's last word - see the warning above
		public uint PlayfieldFieldC { get; set; } = 7; // the word before the flags (7 on most maps, 0 on 1010/2010/2040/5007, 6 on 11040)
		public uint PlayfieldFlags { get; set; } // the final PlayfieldFIIR flag word (beach 0, Old Tarantia 1 - the mount-rule bit)

		// The map's real playfield-instance token - the value the client scopes the character to when it loads
		// this playfield. The client keys per-area behaviour on it (e.g. mount permission: a wrong token makes a
		// mountable zone read as no-mount). Sourced from the map's capture (the despawn/snapshot token, e.g. Old
		// Tarantia = 0x00507927). 0 = unknown the world-entry falls back to WorldState.DefaultPlayfieldToken (beach).
		public uint PlayfieldToken { get; set; }
	}

	// How the live world creates copies (instances) of a map. Retail's playfield-instance token is the
	// instance id; this column tells the assignment policy how many copies to mint and how to route players to them.
	public enum MapInstanceMode
	{
		// One shared instance for everyone (capped by PopulationCap; 0 = uncapped). The default open-world behaviour
		// - a single global Zone per map.
		Shared = 0,

		// One instance per character - a solo/destiny playfield (e.g. the Tortage beach). No one else is ever placed
		// in your copy.
		Solo = 1,

		// One instance per party (dungeons): party members route to the same copy, a new party mints a new one.
		Group = 2
	}
}
