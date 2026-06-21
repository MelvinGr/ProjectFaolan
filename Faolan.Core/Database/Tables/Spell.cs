using System;
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

		// Backfilled from the RDB spell-data record (type 1040005) by RdbSpellImporter (#236):
		public ushort CastTime { get; set; }    // milliseconds (RDB field 0x126 * 10)
		public float Range { get; set; }         // target range in metres (RDB field 0x11F / 100)
		public float DamageRating { get; set; }  // spell damage rating percent (RDB field 0x15C / 100)

		public ushort ManaCost { get; set; }

		// Min/Max damage are NOT stored in the RDB spell-data base record - the client COMPUTES them per rank/level
		// from the spell's damage rating + the caster's stats, so they cannot be backfilled (RdbSpellImporter leaves
		// them 0). Use DamageRating + level scaling instead. Kept so the legacy column isn't dropped from existing
		// rows. (#236)
		[Obsolete("Rank/level-computed client-side; not in the RDB record and not backfillable. Use DamageRating.")]
		public ushort MaxDamage { get; set; }

		[Obsolete("Rank/level-computed client-side; not in the RDB record and not backfillable. Use DamageRating.")]
		public ushort MinDamage { get; set; }
	}
}
