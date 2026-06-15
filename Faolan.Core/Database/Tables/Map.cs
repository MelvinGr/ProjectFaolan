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
		public static readonly RespawnPoint NewPlayerSpawnSpawnPoint = new RespawnPoint
		{
			Id = 0,
			MapId = TortageBeachId,

			// The Tortage Beach new-player intro spawn (map 4010), bit-exact (exact IEEE-754 bits) from the create
			// world-entry MapChange / 2nd 0x2024 ReadyForPlayScreen (dikketest & randochar create dumps). Single source of
			// truth for the freshly-created character's start location - reused by SetCreationSpawn (DatabaseRepository) and
			// as the default resurrection anchor (DefaultResurrection).
			Position = new Vector3(
				BitConverter.Int32BitsToSingle(0x43A14000),  // 322.5
				BitConverter.Int32BitsToSingle(0x43114FB3),  // ~145.311
				BitConverter.Int32BitsToSingle(0x4430399A)), // ~704.9

			AlwaysKnown = true,
			Name = "Tortage Beach"
		};

		public virtual ICollection<WorldObject> Objects { get; set; }
		public virtual ICollection<RespawnPoint> RespawnPoints { get; set; }
		public virtual ICollection<NpcSpawn> NpcSpawns { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }
	}
}
