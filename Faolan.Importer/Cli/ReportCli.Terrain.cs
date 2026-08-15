using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Captures;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// #284: bake a compact per-map terrain file (heightmap 1066611 + collision mesh 1000012, gzipped) for every map
		// in the DB that has terrain, so the GAME SERVER can serve ground-snap + line-of-sight from these small files
		// instead of shipping the ~28 GB client RDB. Output: <out>/<mapId>.terrain (TerrainFile format). Run:
		//   dotnet run --project Faolan.Importer -- --report terrain-export [--rdb <dir>] [--terrain-out <dir>]
		private static async Task TerrainExport(string rdbDir, string outDir)
		{
			using var db = OpenDb();
			using var rdb = OpenRdb(rdbDir);
			Directory.CreateDirectory(outDir);

			var mapIds = await db.Maps.Select(m => m.Id).OrderBy(id => id).ToListAsync();
			int written = 0, skipped = 0;
			long totalBytes = 0;
			foreach (var mapId in mapIds)
			{
				var heightmap = ReadResource(rdb, RdbHeightmap.ResourceType, mapId);
				var collision = ReadResource(rdb, RdbCollisionMesh.ResourceType, mapId);
				if (heightmap.Length == 0 && collision.Length == 0)
				{
					skipped++;
					continue;
				}

				var extent = TilemapExtent(rdb, mapId);
				var path = Path.Combine(outDir, $"{mapId}{TerrainFile.Extension}");
				await using (var fs = File.Create(path))
					TerrainFile.Write(fs, extent, heightmap, collision);

				var size = new FileInfo(path).Length;
				totalBytes += size;
				written++;
				Console.WriteLine($"  map {mapId}: heightmap {heightmap.Length / 1024} KB + collision {collision.Length / 1024} KB -> {size / 1024} KB");
			}

			Console.WriteLine($"terrain-export: {written} maps written ({totalBytes / 1024 / 1024} MB total), {skipped} maps with no terrain, into {outDir}.");
		}

		// The decompressed body of a per-map RDB resource, or empty when the map has no readable entry of that type.
		// `CanRead` rather than `IsLocal`: the heightmap/collision types are majority CDN-only (1070003 is 276 of 520),
		// and RdbReader pulls those through the mirror - gating on IsLocal dropped over half the world.
		private static byte[] ReadResource(RdbReader rdb, uint type, uint mapId)
		{
			if (!rdb.TryGet(type, mapId, out var e) || !rdb.CanRead(e))
				return [];

			try
			{
				return rdb.ReadDecompressed(e);
			}
			catch
			{
				return [];
			}
		}

		// Re-run the NpcSpawnArea creature resolver (#363/#489). The resolver resets its own machine assignments and
		// re-derives every tier, so this is idempotent and is how a change to the resolution RULES reaches the
		// committed faolan.db without a full RDB spawn re-import. Manual assignments (`.setnpcgroup`) are never touched.
		//
		// It needs the RDB only for the groups' wander polylines, which the containment tier uses as its membership
		// test (#491) and which are deliberately not stored in the DB. Running without them would silently apply
		// WEAKER rules than the importer and produce a different faolan.db, so the RDB is required rather than
		// optional. Run:
		//   dotnet run --project Faolan.Importer -- --report resolve-spawn-areas --Content:RdbDir <dir>
		private static async Task ResolveSpawnAreas(string rdbDir)
		{
			await using var db = OpenDb();
			using var rdb = OpenRdb(rdbDir);
			var roamPaths = new Dictionary<uint, IReadOnlyList<IReadOnlyList<Vector3>>>();
			foreach (var mapId in await db.Maps.Select(m => m.Id).ToListAsync())
			{
				if (!rdb.TryGet(NpcSpawnLayout.ResourceType, mapId, out var e) || !rdb.CanRead(e))
					continue;
				if (!NpcSpawnLayout.TryParse(rdb.ReadDecompressed(e), out var layout) || layout == null)
					continue;

				for (var gi = 0; gi < layout.Groups.Count; gi++)
					if (layout.Groups[gi].RoamPaths.Count > 0)
						roamPaths[NpcSpawnArea.ComputeId(mapId, gi)] = layout.Groups[gi].RoamPaths;
			}

			Console.WriteLine($"roam paths loaded for {roamPaths.Count} areas");

			async Task<string> Tally()
			{
				var rows = await db
					.NpcSpawnAreas.GroupBy(a => a.ResolvedBy)
					.Select(g => new
					{
						Tier = g.Key,
						Areas = g.Count(),
						Pop = g.Sum(a => a.MaxCount),
						Creatures = g.Select(a => a.NpcTemplateId).Distinct().Count()
					})
					.ToListAsync();
				return string.Join("\n", rows.OrderBy(r => r.Tier).Select(r => $"    {r.Tier,-14} areas={r.Areas,5}  population={r.Pop,6}  distinct creatures={r.Creatures}"));
			}

			Console.WriteLine("before:\n" + await Tally());
			var stats = await SpawnAreaResolver.ResolveAsync(db, roamPaths, SpawnAreaResolver.LoadRaceNames(rdb));
			Console.WriteLine($"resolved: name={stats.Name} containment={stats.Containment} id-propagation={stats.IdProp} race-unique={stats.RaceUnique}");
			Console.WriteLine("after:\n" + await Tally());
		}

		// The playfield's world-unit width (1070003 <tilemap Width>), for the heightmap cell size. 0 if unavailable.
		private static uint TilemapExtent(RdbReader rdb, uint mapId)
		{
			if (!rdb.TryGet(1070003, mapId, out var e) || !rdb.CanRead(e))
				return 0;

			try
			{
				var xml = Encoding.Latin1.GetString(rdb.ReadDecompressed(e));
				var m = Regex.Match(xml, "<tilemap[^>]*Width=\"(\\d+)\"");
				return m.Success
					? uint.Parse(m.Groups[1].Value)
					: 0;
			}
			catch
			{
				return 0;
			}
		}
	}
}
