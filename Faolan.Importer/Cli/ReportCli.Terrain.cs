using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// bake a compact per-map terrain file (heightmap 1066611 + collision mesh 1000012, gzipped) for every map
		// in the DB that has terrain, so the GAME server can serve ground-snap + line-of-sight from these small files
		// instead of shipping the ~28 GB client RDB. Output: <out>/<mapId>.terrain (TerrainFile format). Run:
		//   dotnet run --project Faolan.Importer -- --report terrain-export [--rdb <dir>] [--terrain-out <dir>]
		private static async Task TerrainExport(string rdbDir, string outDir)
		{
			using var db = OpenDb();
			using var rdb = OpenRdb(rdbDir);
			Directory.CreateDirectory(outDir);

			var mapIds = await db.Maps.Select(m => m.Id).OrderBy(id => id).ToListAsync();

			// Which playfields share each tilemap. 1066611 is emitted per PLAYFIELD and carries that playfield's
			// own props baked into the surface, so a single map's grid is not the bare terrain: Tortage Island 4010,
			// City of Tortage 4020/4022 and the End Battle 4030 all name `tilemap ID="4040"` and are one physical
			// terrain, each read differently because each carries its own trees or buildings. The whole RDB is scanned
			// rather than just the Maps table, because a sibling that we do not host still describes the same ground.
			var siblings = TilemapSiblings(rdb);
			var baked = new HashSet<string>();
			int written = 0, skipped = 0, merged = 0;
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

				// The terrain under all of them: props only add height, so the lowest reading across the group is the
				// ground. See RdbHeightmap.TryLowerEnvelope for the measurement that settles this.
				if (heightmap.Length > 0 && siblings.TryGetValue(mapId, out var group) && group.Count > 1)
				{
					// This map's own grid goes first: it fixes the shape the envelope is written in, so a sibling
					// sampled at another resolution is skipped instead of deciding the output.
					var bodies = new List<byte[]> { heightmap };
					bodies.AddRange(group.Where(id => id != mapId).Select(id => ReadResource(rdb, RdbHeightmap.ResourceType, id)).Where(b => b.Length > 0));

					if (bodies.Count > 1 && RdbHeightmap.TryLowerEnvelope(bodies, out var envelope))
					{
						// Name a few siblings, not all of them - one Hyborian tilemap is shared by ~200 playfields and
						// printing the list buried the run's actual output.
						var others = group.Where(id => id != mapId).ToList();
						var named = string.Join(", ", others.Take(4)) + (others.Count > 4
							? $", +{others.Count - 4} more"
							: "");
						Console.WriteLine($"  map {mapId}: terrain shared with {named} - lower envelope of {bodies.Count} same-size grids.");
						heightmap = envelope;
						merged++;
					}
				}

				var extent = TilemapExtent(rdb, mapId);
				var path = Path.Combine(outDir, $"{mapId}{TerrainFile.Extension}");
				await using (var fs = File.Create(path))
					TerrainFile.Write(fs, extent, heightmap, collision);

				baked.Add(Path.GetFileName(path));
				var size = new FileInfo(path).Length;
				totalBytes += size;
				written++;
				Console.WriteLine($"  map {mapId}: heightmap {heightmap.Length / 1024} KB + collision {collision.Length / 1024} KB -> {size / 1024} KB");
			}

			// Prune what this run did not write. A.terrain file left behind for a map that has since lost its terrain
			// (or left the Maps table) is not inert: the game server loads purely by filename, so it would keep serving
			// ground-snap and line-of-sight off a stale bake with nothing to indicate it. Only *.terrain in the output
			// directory is ever touched.
			var stale = 0;
			foreach (var path in Directory.EnumerateFiles(outDir, $"*{TerrainFile.Extension}"))
			{
				if (baked.Contains(Path.GetFileName(path)))
					continue;

				Console.WriteLine($"  removing stale {Path.GetFileName(path)} - this run produced no terrain for it.");
				File.Delete(path);
				stale++;
			}

			Console.WriteLine(
				$"terrain-export: {written} maps written ({totalBytes / 1024 / 1024} MB total), {merged} merged with their tilemap siblings, {skipped} maps with no terrain, {stale} stale file(s) removed, into {outDir}.");
		}

		// playfieldId -> every playfield sharing its `<tilemap ID>`, itself included. Read from the 1070003 playfield
		// descriptors, which state the tilemap outright; a playfield whose descriptor is unreadable simply maps to
		// itself, so it bakes exactly as it did before.
		private static Dictionary<uint, List<uint>> TilemapSiblings(RdbReader rdb)
		{
			var byTilemap = new Dictionary<uint, List<uint>>();
			var tilemapOf = new Dictionary<uint, uint>();
			foreach (var e in rdb.EntriesOfType(PlayfieldDescriptionType).ToList())
			{
				if (!rdb.CanRead(e))
					continue;

				uint tilemap;
				try
				{
					var xml = Encoding.Latin1.GetString(rdb.ReadDecompressed(e));
					var m = Regex.Match(xml, "<tilemap[^>]*ID=\"(\\d+)\"");
					if (!m.Success || !uint.TryParse(m.Groups[1].Value, out tilemap))
						continue;
				}
				catch
				{
					continue;
				}

				tilemapOf[e.Id] = tilemap;
				if (!byTilemap.TryGetValue(tilemap, out var list))
					byTilemap[tilemap] = list = [];
				list.Add(e.Id);
			}

			return tilemapOf.ToDictionary(kv => kv.Key, kv => byTilemap[kv.Value].Order().ToList());
		}

		// RDB type of the playfield description XML (1070003) - the record that names a playfield's tilemap + extent.
		private const uint PlayfieldDescriptionType = 1070003;

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

		// Re-run the NpcSpawnArea creature resolver. The resolver resets its own machine assignments and
		// re-derives every tier, so this is idempotent and is how a change to the resolution rules reaches the
		// committed faolan.db without a full RDB spawn re-import. Manual assignments (`.setnpcgroup`) are never touched.
		//
		// It needs the RDB only for the groups' wander polylines, which the containment tier uses as its membership
		// test and which are deliberately not stored in the DB. Running without them would silently apply
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
			if (!rdb.TryGet(PlayfieldDescriptionType, mapId, out var e) || !rdb.CanRead(e))
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
