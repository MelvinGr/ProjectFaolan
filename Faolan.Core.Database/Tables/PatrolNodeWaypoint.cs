using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;

namespace Faolan.Core.Database.Tables
{
	// One waypoint of a PatrolNode's PathMoveTask (mode 1) - a big-endian vec3 the NPC walks to, in walk order
	// (Ordinal). Cascade-deletes with its PatrolNode. Split from PatrolWaypoint (which is the legacy flat per-path
	// list) so multi-segment patrols keep each segment's waypoints under their own node (#316).
	// Keyed by the natural composite (PatrolNodeId, Ordinal) - configured in DatabaseContext.OnModelCreating -
	// rather than a surrogate Id: a node has exactly one waypoint per walk position.
	public class PatrolNodeWaypoint
	{
		public uint PatrolNodeId { get; set; }

		[ForeignKey(nameof(PatrolNodeId))]
		public virtual PatrolNode PatrolNode { get; set; } = null!;

		[NotMapped]
		public Vector3 Position
		{
			get => new(PositionX, PositionY, PositionZ);
			set
			{
				PositionX = value.X;
				PositionY = value.Y;
				PositionZ = value.Z;
			}
		}

		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }

		public int Ordinal { get; set; }
	}
}
