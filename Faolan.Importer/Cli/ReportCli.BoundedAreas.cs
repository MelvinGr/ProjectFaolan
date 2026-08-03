using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report bounded-areas` - the survey of the client's trigger-volume table (RDB 1000053, #513).
	//
	// With no `--report-arg` it prints the CENSUS: how many regions fall in each naming bucket and over how many
	// playfields. That table is the one in docs/rdb/bounded-areas-1000053.md, and it is generated here rather than kept
	// by hand so it is recomputed from the real parser (BoundedAreaCollection) and the real classifier
	// (BoundedAreaClassifier) instead of from a one-off script nobody can re-run.
	//
	// With `--report-arg <bucket>` it lists every region in that bucket, and - where we already hold data the bucket can
	// be checked against - the CROSS-CHECK. That is the point of the report: #513's safe buckets are the ones that can be
	// corroborated before they are imported, and the hazard families are the standing example of what happens without it.
	public static partial class ReportCli
	{
		// The id space above this is a VARIANT playfield numbering (0x40000000 | <playfield>) that is not a Map.Id - the
		// same set RdbHazardImporter skips. Counted separately so the census describes real playfields.
		private const uint BoundedAreaVariantIdBase = 0x40000000;

		private static async Task BoundedAreas(string rdbDir, string? bucketSlug)
		{
			var records = ReadBoundedAreas(rdbDir);
			var real = records.Where(r => r.MapId < BoundedAreaVariantIdBase).ToList();
			var variant = records.Count - real.Count;

			if (string.IsNullOrWhiteSpace(bucketSlug))
			{
				BoundedAreaCensus(real, variant);
				return;
			}

			var bucket = BoundedAreaClassifier.FromSlug(bucketSlug.Trim());
			if (bucket == null)
			{
				Console.WriteLine($"bounded-areas: unknown bucket '{bucketSlug}'. Known: {string.Join(", ", BoundedAreaClassifier.Order.Select(BoundedAreaClassifier.Slug))}");
				return;
			}

			await BoundedAreaDetail(real, bucket.Value);
		}

		// Every readable 1000053 record, parsed. A record that will not parse is REPORTED rather than dropped silently -
		// a census that quietly lost a playfield is worse than no census (the importer_silent_skip lesson).
		private static List<(uint MapId, IReadOnlyList<BoundedAreaCollection.BoundedArea> Areas)> ReadBoundedAreas(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			var records = new List<(uint, IReadOnlyList<BoundedAreaCollection.BoundedArea>)>();
			var unreadable = 0;

			foreach (var e in rdb.EntriesOfType(BoundedAreaCollection.ResourceType).Where(rdb.CanRead).OrderBy(e => e.Id))
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

				if (dec.Length == 0 || !BoundedAreaCollection.TryParse(dec, out var collection) || collection == null)
				{
					unreadable++;
					continue;
				}

				records.Add((e.Id, collection.Areas));
			}

			if (unreadable > 0)
				Console.WriteLine($"(⚠️ {unreadable} record(s) unreadable - a CDN miss or a truncated payload; the counts below exclude them)");

			return records;
		}

		private static void BoundedAreaCensus(List<(uint MapId, IReadOnlyList<BoundedAreaCollection.BoundedArea> Areas)> records, int variantRecords)
		{
			var areas = new Dictionary<BoundedAreaBucket, int>();
			var maps = new Dictionary<BoundedAreaBucket, HashSet<uint>>();
			var degenerate = 0;

			foreach (var (mapId, list) in records)
			foreach (var area in list)
			{
				var bucket = BoundedAreaClassifier.Classify(area.Name);
				areas[bucket] = areas.GetValueOrDefault(bucket) + 1;
				if (!maps.TryGetValue(bucket, out var set))
					maps[bucket] = set = [];
				set.Add(mapId);
				if (!area.IsPolygon)
					degenerate++;
			}

			var total = areas.Values.Sum();
			Console.WriteLine($"=== bounded-area census (RDB 1000053): {total} regions over {records.Count} playfield records ===");
			Console.WriteLine($"{"bucket",-20} {"areas",6} {"maps",5}");
			foreach (var bucket in BoundedAreaClassifier.Order)
				Console.WriteLine($"{BoundedAreaClassifier.Slug(bucket),-20} {areas.GetValueOrDefault(bucket),6} {maps.GetValueOrDefault(bucket, []).Count,5}");

			Console.WriteLine();
			Console.WriteLine($"{degenerate} region(s) carry fewer than 3 vertices (no testable footprint).");
			Console.WriteLine($"{variantRecords} record(s) sit in the variant id space (0x40000000-based) and are NOT Map.Ids - excluded above.");
			Console.WriteLine("⚠️ The buckets are a NAMING heuristic (BoundedAreaClassifier), not evidence. See #513 before importing one.");
		}

		private static async Task BoundedAreaDetail(List<(uint MapId, IReadOnlyList<BoundedAreaCollection.BoundedArea> Areas)> records, BoundedAreaBucket bucket)
		{
			await using var db = OpenDb();
			var mapNames = await db.Maps.ToDictionaryAsync(m => m.Id, m => m.Name ?? "");
			var (probes, probeKind) = await CrossCheckProbes(db, bucket);

			Console.WriteLine($"=== bounded areas in bucket '{BoundedAreaClassifier.Slug(bucket)}' ===");
			int shown = 0, withProbes = 0, probesCovered = 0;
			var coveredProbes = new HashSet<(uint Map, int Index)>();

			foreach (var (mapId, list) in records)
			{
				var picked = list.Where(a => BoundedAreaClassifier.Classify(a.Name) == bucket).ToList();
				if (picked.Count == 0)
					continue;

				var known = mapNames.TryGetValue(mapId, out var nm)
					? nm
					: "(no Map row)";
				Console.WriteLine($"-- map {mapId} {known}");

				foreach (var area in picked)
				{
					shown++;
					var xs = area.Vertices.Select(v => v.X).ToList();
					var zs = area.Vertices.Select(v => v.Y).ToList();
					var box = xs.Count > 0
						? $"({xs.Min():F0},{zs.Min():F0})-({xs.Max():F0},{zs.Max():F0})"
						: "(no vertices)";
					var heights = float.IsNaN(area.MinY)
						? "none"
						: $"{area.MinY:F0}..{area.MaxY:F0}";
					Console.WriteLine($"   {area.Name,-40} v={area.Vertices.Count,4} xz={box,-30} y={heights,-12} scryid={area.ScryId}");

					if (probeKind == null || !area.IsPolygon)
						continue;

					// What we ALREADY hold that lands inside this region. The point rather than any overlap - a probe
					// whose own box merely clips the region edge is a neighbour, not a member, and the sub-camps
					// (Camp23 White Hands A/B/C/D) are exactly what this is meant to find.
					var mapProbes = probes.GetValueOrDefault(mapId, []);
					var inside = mapProbes
						.Select((p, i) => (Probe: p, Index: i))
						.Where(p => area.Contains(p.Probe.X, p.Probe.Z))
						.ToList();
					if (inside.Count == 0)
						continue;

					withProbes++;
					probesCovered += inside.Count;
					foreach (var p in inside)
						coveredProbes.Add((mapId, p.Index));

					Console.WriteLine($"      ↳ {inside.Count} {probeKind}: {string.Join(", ", inside.Select(p => p.Probe.Label))}");
				}
			}

			Console.WriteLine();
			Console.WriteLine($"{shown} region(s) in this bucket.");
			if (probeKind == null)
				return;

			// BOTH directions. "How many regions cover something we hold" says whether the bucket is corroborated;
			// "how many things we hold no region covers" says whether importing it would ADD anything - and that second
			// number is the one that decides an import (#513). A bucket that only ever re-describes rows we already have
			// is not worth a table.
			var totalProbes = probes.Values.Sum(v => v.Count);
			Console.WriteLine($"{withProbes} of them contain at least one {probeKind} ({probesCovered} covered); the other {shown - withProbes} contain none.");
			Console.WriteLine($"{coveredProbes.Count} of the {totalProbes} {probeKind} we already hold fall inside one of these regions; {totalProbes - coveredProbes.Count} do not.");
		}

		// The rows we ALREADY hold that a bucket can be checked against, keyed by map, plus a human name for them.
		// (null kind = nothing to check this bucket against, so the listing is geometry only.)
		//
		// This is the point of the report. #513's safe buckets are the ones that can be corroborated before import, and
		// the two that can be are the two the client ALSO ships in a purpose-built table of its own:
		//   - spawn camps    vs NpcSpawnArea  (RDB 1010506 NpcSpawnLayout - roam groups: where, how many, the points)
		//   - resurrection   vs RespawnPoint  (RDB 1000090 RESURRECTIONPOINTSEXPORT - Guid, restoration %, orientation)
		// In both cases the dedicated table carries strictly more than the trigger volume does, so the overlap is a
		// CHECK on what we imported rather than a source to import from.
		private static async Task<(Dictionary<uint, List<(string Label, float X, float Z)>> Probes, string? Kind)> CrossCheckProbes(
			Core.Database.DatabaseContext db, BoundedAreaBucket bucket)
		{
			switch (bucket)
			{
				case BoundedAreaBucket.SpawnCamp:
				{
					var groups = await db.NpcSpawnAreas.ToListAsync();
					return (groups
						.GroupBy(g => g.MapId)
						.ToDictionary(g => g.Key, g => g
							.Select(a => ($"{a.GroupName} (x{a.MaxCount}{(a.NpcTemplateId == null ? ", unresolved" : "")})",
								(a.BoundsMinX + a.BoundsMaxX) / 2f, (a.BoundsMinZ + a.BoundsMaxZ) / 2f))
							.ToList()), "1010506 spawn group(s)");
				}

				case BoundedAreaBucket.Resurrection:
				{
					var points = await db.RespawnPoints.ToListAsync();
					return (points
						.GroupBy(p => p.MapId)
						.ToDictionary(g => g.Key, g => g
							.Select(p => ($"{p.Name} (guid {p.Id}, {p.RestorationLevel:F0}%)", p.PositionX, p.PositionZ))
							.ToList()), "1000090 resurrection point(s)");
				}

				default:
					return ([], null);
			}
		}
	}
}
