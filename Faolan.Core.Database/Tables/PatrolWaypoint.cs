using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One ordered waypoint of a PatrolPath - a captured big-endian vec3 the NPC walks to. Ordinal preserves the
	// walk order (the order the waypoints appear in the MoveTask body). Cascade-deletes with its PatrolPath.
	public class PatrolWaypoint : BaseEntity
	{
		[ForeignKey(nameof(PatrolPathId))]
		public virtual PatrolPath PatrolPath { get; set; }

		// TODO: Vector3
		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }

		public int Ordinal { get; set; }
		public uint PatrolPathId { get; set; }
	}
}
