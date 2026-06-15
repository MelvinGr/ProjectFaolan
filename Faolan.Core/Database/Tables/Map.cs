using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

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

		// The universal default resurrection anchor: Tortage Beach's ResurrectionAIPoint1, captured verbatim from the
		// Strie 4010 login (idx 236). It is advertised on the login 0x2024 ReadyForPlayScreen (= DispatchValidResurrectionSpots)
		// when a map defines no real RespawnPoint, and by the new-character create flow. NB the spawnId 784236 is retail's
		// id, NOT a row in our DB - this is a placeholder anchor only; maps with their own RespawnPoints advertise the real
		// RespawnPoint.Id instead (the id RespawnMe echoes back).
		public const uint TortageBeachResurrectionSpawnId = 0x000BF76C;

		// TODO: THIS SHOULD BE THE NEW PLYER SPAWN POINT (NO ID, NOT A DB OBJECT), NOT Tortage Beach's ResurrectionAIPoint1
		public static RespawnPoint DefaultResurrection()
		{
			return new RespawnPoint
			{
				Id = TortageBeachResurrectionSpawnId,
				MapId = TortageBeachId,
				PositionX = 336.309f,
				PositionY = 148.967f,
				PositionZ = 660.968f,
				AlwaysKnown = true,
				Name = "Tortage Beach"
			};
		}

		public virtual ICollection<WorldObject> Objects { get; set; }
		public virtual ICollection<RespawnPoint> RespawnPoints { get; set; }
		public virtual ICollection<NpcSpawn> NpcSpawns { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }
	}
}
