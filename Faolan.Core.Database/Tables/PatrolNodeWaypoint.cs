using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;

namespace Faolan.Core.Database.Tables
{
	// One waypoint of a PatrolNode's PathMoveTask (mode 1) - a big-endian vec3 the NPC walks to, in walk order
	// (Ordinal). Cascade-deletes with its PatrolNode. Split from PatrolWaypoint (which is the legacy flat per-path
	// list) so multi-segment patrols keep each segment's waypoints under their own node (#316).
	public class PatrolNodeWaypoint : BaseEntity // TODO: Make PatrolNodeId_Ordinal the compound key
	{
		public uint PatrolNodeId { get; set; }

		[ForeignKey(nameof(PatrolNodeId))]
		public virtual PatrolNode PatrolNode { get; set; } = null!;

		public int Ordinal { get; set; }

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
	}
}
