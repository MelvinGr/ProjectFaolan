using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One vertex of a MapRegion's boundary polygon, in the order the client's RDB lists it. Only (x, z): the volume
	// is a prism, and its vertical extent is the parent's MinY/MaxY rather than a per-vertex height.
	//
	// Natural composite key (RegionId, Sequence) - the parent's derived id plus this vertex's ordinal, the standard
	// shape for a child of a re-imported tree (cf. MapHazardVertex, ConversationScreenParameter). Sequence is part
	// of the identity rather than decoration: a polygon is only meaningful in ring order, and EF gives no ordering
	// guarantee on a navigation collection, so every reader has to sort by it.
	[PrimaryKey(nameof(RegionId), nameof(Sequence))]
	public class MapRegionVertex
	{
		public uint RegionId { get; set; }

		[ForeignKey(nameof(RegionId))]
		public virtual MapRegion Region { get; set; } = null!;

		// 0-based position in the ring. The polygon is implicitly closed (the last vertex joins the first); the RDB
		// does not repeat the first point and neither do we.
		public int Sequence { get; set; }

		public float X { get; set; }
		public float Z { get; set; }
	}
}
