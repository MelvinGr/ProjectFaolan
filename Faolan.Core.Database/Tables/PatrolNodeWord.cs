using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One 32-bit word of a PatrolNode's fixed numeric array, in wire order (Ordinal). Cascade-deletes with its
	// PatrolNode, and is keyed by the natural composite (PatrolNodeId, Ordinal) exactly like [[PatrolNodeWaypoint]]:
	// a node has one word per position.
	//
	// This replaces the packed `WordsData` blob (#410). Which array a row belongs to follows from the node's Mode -
	// mode 1 Path stores the 3 middle trailer words, modes 2 Rotate / 10 SetPos the 7-word Vec7, mode 5 Ballistic 7,
	// mode 7 Formation 6 - so the count is the mode's, not free-form; PatrolNodeMapper re-packs them big-endian and
	// MoveTaskBuilder re-emits the byte-exact record (PatrolNodeRoundTripTests over every committed patrol).
	//
	// Value stays RAW u32 BITS rather than a float: only some of these slots are floats, and the record has to
	// round-trip bit-for-bit. What the corpus shows so far: the mode-1 triple is [small int, dir, dir] (the ints run
	// 0..41, the dirs are floats), and Vec7 reads as a position + a unit quaternion - e.g. (1147.29, 199.54,
	// 1758.06) + (0, -0.204, 0, 0.979). Naming those slots is follow-up work on #410; modes 5/7 have no rows in the
	// corpus at all, so their words stay unnamed on purpose.
	[PrimaryKey(nameof(PatrolPathId), nameof(OrdinalPath), nameof(Ordinal))]
	public class PatrolNodeWord
	{
		public uint PatrolPathId { get; set; }

		[MaxLength(64)]
		public string OrdinalPath { get; set; } = "";

		[ForeignKey($"{nameof(PatrolPathId)},{nameof(OrdinalPath)}")]
		public virtual PatrolNode PatrolNode { get; set; } = null!;

		public uint Value { get; set; }

		public int Ordinal { get; set; }
	}
}
