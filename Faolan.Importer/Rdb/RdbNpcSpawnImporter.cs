using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports the authoritative open-world ambient spawn GROUPS from the client RDB "NPC roam/movement" layout (type
	// 1010506 = NpcSpawnLayout, #332/#363) into NpcSpawnArea (+ child NpcSpawnAreaPoint anchors). Each group becomes one
	// area carrying its roam AABB + population count; the resolver then pre-fills the creature (NpcTemplateId) where it
	// can. Records are LOCAL and their id == the playfield id; only maps in the DB Maps catalog are imported. Empty
	// records (scripted/instanced playfields, groupCount == 0) contribute nothing. Clean rebuild each run (the set is
	// fully derived from the fixed client RDB).
	public static class RdbNpcSpawnImporter
	{
		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var ourMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();

			// A hand-verified assignment is a human's answer and must survive a re-harvest. The resolver already
			// refuses to overwrite Manual; the clean rebuild below would delete it instead, which contradicts that,
			// so carry it across by the STABLE (map, group ordinal) key and re-apply after the rows are rebuilt.
			var manual = await db
				.NpcSpawnAreas.Where(a => a.ResolvedBy == SpawnResolutionSource.Manual && a.NpcTemplateId != null)
				.Select(a => new { a.MapId, a.GroupOrdinal, a.NpcTemplateId })
				.ToListAsync();

			// Clean rebuild: child points cascade with their area, so clearing the areas clears everything.
			db.NpcSpawnAreas.RemoveRange(db.NpcSpawnAreas);
			await db.SaveChangesAsync();

			// The group's wander polylines, kept by area id for the resolver's containment test (#491). Not persisted -
			// corpus-wide they are ~740k points, which would dwarf the seed for data only the resolver reads.
			var roamPaths = new Dictionary<uint, IReadOnlyList<IReadOnlyList<Vector3>>>();

			int maps = 0, areas = 0, points = 0, emptyMaps = 0, unreadable = 0;
			foreach (var e in rdb.EntriesOfType(NpcSpawnLayout.ResourceType).Where(e => e.IsLocal && ourMaps.Contains(e.Id)))
			{
				byte[] dec;
				try
				{
					dec = rdb.ReadDecompressed(e);
				}
				catch
				{
					unreadable++;
					continue;
				}

				if (!NpcSpawnLayout.TryParse(dec, out var layout) || layout == null)
				{
					unreadable++;
					continue;
				}

				if (layout.Groups.Count == 0)
				{
					emptyMaps++;
					continue;
				}

				for (var gi = 0; gi < layout.Groups.Count; gi++)
				{
					var g = layout.Groups[gi];
					// The id is DERIVED from (map, group ordinal) - see NpcSpawnArea/DerivedKey - so a re-harvest
					// reproduces it instead of renumbering the areas and their 23,782 points.
					var area = new NpcSpawnArea
					{
						Id = NpcSpawnArea.ComputeId(e.Id, gi),
						MapId = e.Id,
						GroupOrdinal = gi,
						GroupName = g.Name,
						Category = g.Category,
						GroupKey = g.Id,
						CommunityId = g.CommunityId,
						BoundsMinX = g.BoundsMin.X,
						BoundsMinY = g.BoundsMin.Y,
						BoundsMinZ = g.BoundsMin.Z,
						BoundsMaxX = g.BoundsMax.X,
						BoundsMaxY = g.BoundsMax.Y,
						BoundsMaxZ = g.BoundsMax.Z,
						MaxCount = g.SpawnPoints.Count,
						RoamPathCount = g.RoamPaths.Count,
						ResolvedBy = SpawnResolutionSource.None
					};
					for (var pi = 0; pi < g.SpawnPoints.Count; pi++)
					{
						var pt = g.SpawnPoints[pi];
						area.Points.Add(new NpcSpawnAreaPoint
						{
							PointIndex = pi,
							Position = pt
						});
					}

					if (g.RoamPaths.Count > 0)
						roamPaths[area.Id] = g.RoamPaths;

					db.NpcSpawnAreas.Add(area);
					areas++;
					points += g.SpawnPoints.Count;
				}

				maps++;
			}

			await db.SaveChangesAsync();

			// Restore the hand-verified assignments before resolving, so the resolver sees them as Manual and leaves
			// them alone (an area that vanished from the RDB simply has nothing to restore onto).
			var restored = 0;
			foreach (var m in manual)
			{
				var area = await db.NpcSpawnAreas.FirstOrDefaultAsync(a => a.MapId == m.MapId && a.GroupOrdinal == m.GroupOrdinal);
				if (area == null)
					continue;

				area.NpcTemplateId = m.NpcTemplateId;
				area.ResolvedBy = SpawnResolutionSource.Manual;
				restored++;
			}

			if (restored > 0)
				await db.SaveChangesAsync();

			// Infer the creature (NpcTemplateId) per area where we can - name / gated containment / guarded id-prop.
			var resolved = await SpawnAreaResolver.ResolveAsync(db, roamPaths);

			return new Result(maps, areas, points, emptyMaps, unreadable, resolved);
		}

		public readonly record struct Result(int Maps, int Areas, int Points, int EmptyMaps, int Unreadable, ResolveStats Resolved);
	}
}
