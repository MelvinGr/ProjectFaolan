using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;

namespace Faolan.Core.Database.Tables
{
	// One ordered waypoint of a PatrolPath - a captured big-endian vec3 the NPC walks to. Ordinal preserves the
	// walk order (the order the waypoints appear in the MoveTask body). Cascade-deletes with its PatrolPath.
	public class PatrolWaypoint : BaseEntity
	{
		public uint PatrolPathId { get; set; }

		[ForeignKey(nameof(PatrolPathId))]
		public virtual PatrolPath PatrolPath { get; set; }

		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }

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

		public int Ordinal { get; set; }
	}
}
