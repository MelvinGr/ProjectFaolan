using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One PRIMARY spawn-point anchor of an NpcSpawnArea (the exact retail spot a creature spawns at, decoded from RDB
	// 1010506, #332/#363). A child of NpcSpawnArea - the per-group facts (bounds, count, creature) live once on the
	// area; this table is only the geometry. Kept for spawn-position fidelity; an ambient spawner may instead scatter
	// within the area's bounds, in which case these become optional.
	// Compound key (NpcSpawnAreaId, PointIndex). No surrogate Id: a point has no identity beyond its slot within its
	// area, and the pair is the natural key (stable across re-imports).
	[PrimaryKey(nameof(NpcSpawnAreaId), nameof(PointIndex))]
	public class NpcSpawnAreaPoint
	{
		[ForeignKey(nameof(NpcSpawnAreaId))]
		public virtual NpcSpawnArea Area { get; set; } = null!;

		public uint NpcSpawnAreaId { get; set; }

		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }

		// The 0-based index of this point within its group - stable across re-imports.
		public int PointIndex { get; set; }

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
