using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One waypoint of a PatrolNode's PathMoveTask (mode 1) - a big-endian vec3 the NPC walks to, in walk order
	// (Ordinal). Cascade-deletes with its PatrolNode. Split from PatrolWaypoint (which is the legacy flat per-path
	// list) so multi-segment patrols keep each segment's waypoints under their own node (#316).
	// Keyed by the natural composite (PatrolNodeId, Ordinal) - configured in DatabaseContext.OnModelCreating -
	// rather than a surrogate Id: a node has exactly one waypoint per walk position.
	[PrimaryKey(nameof(PatrolPathId), nameof(OrdinalPath), nameof(Ordinal))]
	public class PatrolNodeWaypoint : IPositionProvider
	{
		public uint PatrolPathId { get; set; }

		[MaxLength(64)]
		public string OrdinalPath { get; set; } = "";

		[ForeignKey($"{nameof(PatrolPathId)},{nameof(OrdinalPath)}")]
		public virtual PatrolNode PatrolNode { get; set; } = null!;

		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }

		public int Ordinal { get; set; }
	}
}
