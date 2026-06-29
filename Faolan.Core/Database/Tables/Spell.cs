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

		// Buff/effect duration in milliseconds. Imported from RDB field 0x008, which is in CENTISECONDS (×10ms - the
		// same unit as the cast-time field; calibrated in-client: Rend 700->7s, Butcher 400->4s, Exhausted 10000->100s),
		// so it is stored here ×10 as real ms. 0 = no self-duration on this record (an instant spell, or an ability
		// whose timed effect lives on a separate EffectSpell record). Drives server-side buff expiry: when a self-buff
		// with a known duration elapses, the world tick sends the retail end-twin removal (rank byte -> 1, empty
		// payload) so the icon disappears at 0 instead of sticking (#274).
		public uint DurationMs { get; set; }

		// Damage-over-time per tick on HealthCurrent, extracted from captured StatDamageIIR (0xE9F24184) records keyed
		// by base spell id (the +41 signed delta; its affected stat at +33 is 0x1B HealthCurrent). Negative = a bleed
		// debuff (Rend -8, 0x342F23 -5); 0 = not a DoT. The world tick applies this to the target's vitals + sends the
		// StatDamageIIR while the debuff is active (#274). DotType is the +45 damage-school flag (1/4), carried so the
		// rebuilt packet matches retail.
		public int DotDelta { get; set; }
		public uint DotType { get; set; }

		public ushort ManaCost { get; set; }

		// Recast / cooldown in SECONDS. Backfilled from the ellatha combos list (the one bulk source that carries it)
		// by EllathaSpellLevelImporter for combo rows. Magic-spell cooldown is NOT in any bulk source (proven not in
		// the RDB record, and not on ellatha's spell list), so it stays 0 for spells - capture key ones by hand from
		// retail tooltips (Captures/spell_cooldowns_observed.tsv). Seconds (not ms like CastTime) to match the source
		// and fit values like Death's Terror 90s in a ushort. (#78)
		public ushort Cooldown { get; set; }

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
