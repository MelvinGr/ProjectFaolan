using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	public class Spell : BaseEntity
	{
		public byte Level { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }

		public uint EffectSpell { get; set; }

		public ushort CastTime { get; set; }
		public ushort ManaCost { get; set; }
		public ushort MaxDamage { get; set; }
		public ushort MinDamage { get; set; }
	}
}
