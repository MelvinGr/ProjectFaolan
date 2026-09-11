using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One prerequisite edge of the feat tree: `WireFeatId` cannot be trained until `RequiresWireFeatId` is. Together
	// these rows are the tree's shape.
	//
	// Source: columns 2/3/4 of the RDB 1000042 feat-table entry (the same resource FeatIdMap comes from), so a feat
	// carries up to three prerequisites. 99-100% of the non-zero values are valid wire feat ids and the rank chains
	// resolve exactly - "Equip: One Handed Edged Weapons 2" requires "... 1", "3" requires "2", and so on.
	//
	// Both columns are wire ids, not catalog ids. Join through FeatIdMap to reach a Feat. That is also why there is
	// no FK on RequiresWireFeatId: a small tail of values (25 of 5269 in column 2) are not entries of the table, and
	// rejecting the whole import over them would trade the 99% for nothing.
	[PrimaryKey(nameof(WireFeatId), nameof(Sequence))]
	public class FeatPrereq
	{
		// The feat that has the prerequisite.
		public uint WireFeatId { get; set; }

		[ForeignKey(nameof(WireFeatId))]
		public virtual FeatIdMap Feat { get; set; } = null!;

		// Which of the entry's three prerequisite columns this came from (0, 1, 2), preserved so the row set
		// regenerates in the client's order rather than an arbitrary one.
		public int Sequence { get; set; }

		// The feat that must be trained first - a wire id, resolvable through FeatIdMap.
		public uint RequiresWireFeatId { get; set; }
	}
}
