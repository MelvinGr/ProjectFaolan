using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	public class Map : Base3DEntity
	{
		// Tortage Beach (0xFAA/4010) - the universal fallback world. Its row and world burst are always
		// seeded, so it is the safe default wherever a character needs a map (creation, import, world entry).
		public const uint TortageBeachId = 0x00000FAA;

		public virtual ICollection<WorldObject> Objects { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }
	}
}
