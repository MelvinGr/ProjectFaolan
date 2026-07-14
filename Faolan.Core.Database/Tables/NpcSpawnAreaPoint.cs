using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;

namespace Faolan.Core.Database.Tables
{
	// One PRIMARY spawn-point anchor of an NpcSpawnArea (the exact retail spot a creature spawns at, decoded from RDB
	// 1010506, #332/#363). A child of NpcSpawnArea - the per-group facts (bounds, count, creature) live once on the
	// area; this table is only the geometry. Kept for spawn-position fidelity; an ambient spawner may instead scatter
	// within the area's bounds, in which case these become optional.
	public class NpcSpawnAreaPoint // TODO: Make compound key NpcSpawnAreaId_PointIndex & remove Column attributes so that they get renamed automatically by EF Core migrations
	{
		public int Id { get; set; }

		public int NpcSpawnAreaId { get; set; }

		[ForeignKey(nameof(NpcSpawnAreaId))]
		public virtual NpcSpawnArea Area { get; set; } = null!;

		// The 0-based index of this point within its group - stable across re-imports.
		public int PointIndex { get; set; }

		[Column("X")]
		public float PositionX { get; set; }
		[Column("Y")]
		public float PositionY { get; set; }
		[Column("Z")]
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
