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

		public virtual ICollection<WorldObject> Objects { get; set; }
		public virtual ICollection<RespawnPoint> RespawnPoints { get; set; }
		public virtual ICollection<NpcSpawn> NpcSpawns { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }
	}
}
