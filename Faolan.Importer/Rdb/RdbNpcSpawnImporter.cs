using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports the authoritative open-world NPC spawn points from the client RDB "NPC roam/movement" layout (type
	// 1010506 = NpcSpawnLayout, #332). These records are LOCAL (in the NN.rdbdata files) and their id == the playfield
	// id; only maps present in the DB Maps catalog are imported (FK target). Empty records (scripted/instanced
	// playfields, groupCount == 0) contribute nothing. The set is fully derived from fixed client content, so this is
	// a clean rebuild each run. Stores only the PRIMARY spawn points (the group's placement anchors); the per-group
	// AABB + roam paths the parser also decodes are not persisted (movement data isn't consumed yet).
	public static class RdbNpcSpawnImporter
	{
		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var ourMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();

			// Clean rebuild: the set is fully derived from the (fixed) client RDB, so a re-run replaces it wholesale.
			db.RdbSpawnPoints.RemoveRange(db.RdbSpawnPoints);
			await db.SaveChangesAsync();

			int maps = 0, groups = 0, points = 0, emptyMaps = 0, unreadable = 0;
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

				var mapPoints = 0;
				for (var gi = 0; gi < layout.Groups.Count; gi++)
				{
					var grp = layout.Groups[gi];
					for (var pi = 0; pi < grp.SpawnPoints.Count; pi++)
					{
						var pt = grp.SpawnPoints[pi];
						db.RdbSpawnPoints.Add(new RdbSpawnPoint
						{
							MapId = e.Id,
							GroupOrdinal = gi,
							PointIndex = pi,
							GroupName = grp.Name,
							Category = grp.Category,
							GroupKey = grp.Id,
							CommunityId = grp.CommunityId,
							X = pt.X,
							Y = pt.Y,
							Z = pt.Z
						});
						mapPoints++;
					}

					groups++;
				}

				if (mapPoints > 0)
				{
					maps++;
					points += mapPoints;
				}
				else
					emptyMaps++;
			}

			await db.SaveChangesAsync();
			return new Result(maps, groups, points, emptyMaps, unreadable);
		}

		public readonly record struct Result(int Maps, int Groups, int Points, int EmptyMaps, int Unreadable);
	}
}
