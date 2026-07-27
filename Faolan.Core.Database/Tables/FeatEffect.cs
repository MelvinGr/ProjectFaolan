using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stat modifier a feat applies (Gitea #251 step 2). A feat's RDB record ends in a count-framed list of these,
	// so this is what the feat DOES - the part a server needs in order to apply it.
	//
	// Tail grammar, after the name:
	//   [u32 2][u32 14][u32 ListFrame(n)] then n x 40-byte elements:
	//     [u32 53045][u32 0][u32 4][u32 0][u32 1][u32 0][u32 2][u32 9][u32 StatId][u32 Value]
	// The eight leading words are identical on every element of every feat inspected, so they are a fixed descriptor
	// (a modifier KIND, presumably) rather than per-effect data, and are not carried as columns until one varies.
	//
	// ⚠️ What is NOT here, and is not in the RDB at all: TIER, PREREQUISITES and COST. Diffing records across four
	// different trees moves only the id, the block count and the name - no header word tracks tree depth. That is
	// consistent with the client's own `"**Error: GDB and RDB data for feat "`: it JOINS server-side GDB data with
	// the RDB record, and the tree's shape lives on the GDB side. See #251.
	[PrimaryKey(nameof(FeatId), nameof(Sequence))]
	public class FeatEffect
	{
		public uint FeatId { get; set; }

		[ForeignKey(nameof(FeatId))]
		public virtual Feat Feat { get; set; } = null!;

		// Wire position within the feat's effect list, so the list regenerates in the order the client stores it.
		public int Sequence { get; set; }

		// The stat this modifies - the same id space as ItemStatModifier.StatId (0x324 Strength, 0x001 HealthMax and
		// the rest all appear on both).
		//
		// ⚠️ NO [ConstrainToEnumValues], unlike ItemStatModifier. That attribute emits a CHECK against the enum's
		// DECLARED members, and CharacterStatId is a catalog of the stats we have NAMED, not of the stat space: items
		// touch 17 ids and every one is declared, while feats touch 158 and 127 are not. The CHECK would reject four
		// fifths of these rows on insert. Typed as the enum anyway, because the value IS one - the names are just
		// incomplete, and a caller still wants GetScale() and a CharacterStatId-keyed dictionary without casting.
		public CharacterStatId StatId { get; set; }

		// The modifier magnitude, in DISPLAY units.
		//
		// ⚠️ NOT the same convention as ItemStatModifier.Amount, which is the RAW WIRE value and must be divided by
		// CharacterStatId.GetScale(). These are already divided: Strength (scale 10) carries 1..9 here, which as raw
		// wire would be +0.1 Strength, and HealthMax (scale 100) carries 100..700, which as raw wire would be +1 HP.
		// Applying the gear convention to these would silently under-grant every feat by 10x or 100x.
		public uint Value { get; set; }
	}
}
