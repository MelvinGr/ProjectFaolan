using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	public class Npc : Base3DEntity
	{
		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		public virtual NpcBody Body { get; set; }
		public virtual NpcEquipment Equipment { get; set; }
		public string Extras { get; set; }

		[MaxLength(255)]
		public string Name { get; set; }

		public uint Action { get; set; }
		public uint Fraction { get; set; }
		public uint GetExp { get; set; }
		public uint Health { get; set; }
		public uint Mana { get; set; }

		public uint MapId { get; set; }
		public uint Stamina { get; set; }
		public ushort Level { get; set; }
	}
}
