using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Data
{
	public class Map : Base3DEntity
	{
		[MaxLength(128)]
		public string Name { get; set; }

		public virtual ICollection<MapObject> Objects { get; set; }

		public virtual ICollection<Npc> Npcs { get; set; }
	}
}
