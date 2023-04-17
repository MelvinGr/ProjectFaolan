using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	public class Map : Base3DEntity
	{
		public virtual ICollection<MapObject> Objects { get; set; }
		public virtual ICollection<Npc> Npcs { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }
	}
}
