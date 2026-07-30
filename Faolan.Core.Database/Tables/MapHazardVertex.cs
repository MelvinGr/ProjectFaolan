using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One vertex of a MapHazard's boundary polygon, in the order the client's RDB lists it (#504). Only (x, z): the
	// volume is a PRISM, and its vertical extent is the parent's MinY/MaxY rather than a per-vertex height.
	//
	// Natural composite key (HazardId, Sequence) - the parent's derived id plus this vertex's ordinal, which is the
	// standard shape for a child of a re-imported tree (cf. ConversationScreenParameter). Sequence is not decoration:
	// a polygon is only meaningful in order, so it is part of the identity, not an afterthought.
	[PrimaryKey(nameof(HazardId), nameof(Sequence))]
	public class MapHazardVertex
	{
		public uint HazardId { get; set; }

		[ForeignKey(nameof(HazardId))]
		public virtual MapHazard Hazard { get; set; } = null!;

		// 0-based position in the ring. The polygon is implicitly CLOSED (the last vertex joins the first); the RDB does
		// not repeat the first point and neither do we.
		public int Sequence { get; set; }

		public float X { get; set; }
		public float Z { get; set; }
	}
}
