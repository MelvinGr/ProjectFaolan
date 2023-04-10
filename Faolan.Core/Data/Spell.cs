using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Data
{
	public class Spell : BaseEntity
	{
		[MaxLength(128)]
		public string Name { get; set; }

		public ushort CastTime { get; set; }
		public uint EffectSpell { get; set; }
		public byte Level { get; set; }
		public ushort ManaCost { get; set; }
		public ushort MaxDamage { get; set; }
		public ushort MinDamage { get; set; }
	}
}
