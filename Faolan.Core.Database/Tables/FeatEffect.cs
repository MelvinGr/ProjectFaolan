using System.ComponentModel.DataAnnotations.Schema;
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

		// The stat this modifies. Ids are in the client's stat space (938/940 on "1HB and 1HE", 946 on "Thrown
		// Weapons"), NOT resolved to names here - LDB category 615 carries the stat names.
		public uint StatId { get; set; }

		// The modifier magnitude, as stored. Scaling is not applied: the rank series for "Thrown Weapons" reads
		// 720 / 960 / 1200 / 1410 / 1500, which is a designed progression rather than a linear one, so a divisor
		// would be a guess. Stored raw and left to the consumer.
		public uint Value { get; set; }
	}
}
