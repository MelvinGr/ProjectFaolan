using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Extractors;
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

		private static async Task BoundedAreas(string rdbDir, string? bucketSlug, IReadOnlyList<CapturedBanner> captured)
		{
			var records = ReadBoundedAreas(rdbDir);
			var real = records.Where(r => r.MapId < BoundedAreaVariantIdBase).ToList();
			var variant = records.Count - real.Count;

			if (string.IsNullOrWhiteSpace(bucketSlug))
			{
				BoundedAreaCensus(real, variant);
				return;
			}

			// Not a bucket: the "zone / area names" family has no naming rule (see BoundedAreaClassifier), so the only
			// way to ask whether the client ships our area-banner regions is GEOMETRICALLY - which needs no names at
			// all. `--report-arg area-banners` runs that test.
			if (bucketSlug.Trim().Equals("area-banners", StringComparison.OrdinalIgnoreCase))
			{
				await AreaBannerCrossCheck(real, captured);
				return;
			}

			var bucket = BoundedAreaClassifier.FromSlug(bucketSlug.Trim());
			if (bucket == null)
			{
				Console.WriteLine($"bounded-areas: unknown bucket '{bucketSlug}'. Known: {string.Join(", ", BoundedAreaClassifier.Order.Select(BoundedAreaClassifier.Slug))}, area-banners");
				return;
			}

			await BoundedAreaDetail(real, bucket.Value);
		}

		// #513/#348: does the client's trigger-volume table contain the AREA BANNER regions?
		//
		// `MapArea` is capture-sourced - a hull over the positions retail actually fired each banner at - and it
		// under-covers by construction, so #513 lists "area banners" as one of the safe, high-value buckets to import
		// if the real region is in 1000053. This is the test for that, and it deliberately uses **no names**: a
		// banner's identity is an LDB string id and a region's is a place name, so matching them by text needs the
		// place-name catalog. Containment needs neither.
		//
		// The bar is not "some region contains the banner" - on any playfield the border region contains everything.
		// A sub-area trigger has to SEPARATE the sub-areas, so a region only counts as a candidate if it contains
		// this area's captured footprint AND does not swallow another area's on the same map. That is the property
		// that would make it the thing that fires the banner.
		private static async Task AreaBannerCrossCheck(List<(uint MapId, IReadOnlyList<BoundedAreaCollection.BoundedArea> Areas)> records, IReadOnlyList<CapturedBanner> captured)
		{
			await using var db = OpenDb();
			var mapNames = await db.Maps.ToDictionaryAsync(m => m.Id, m => m.Name ?? "");
			var byMap = records.ToDictionary(r => r.MapId, r => r.Areas);
			// The CAPTURED harvest, straight from the captures: this test is "does a client region contain the hull
			// retail fired the banner in", so it needs the positions, which are not stored anywhere (#680).
			var banners = captured.GroupBy(a => a.MapId).ToDictionary(g => g.Key, g => g.ToList());

			Console.WriteLine("=== area-banner cross-check: is a MapArea's region in RDB 1000053? ===");
			Console.WriteLine("A candidate must CONTAIN this banner's captured footprint and EXCLUDE every other banner on the map.");
			Console.WriteLine();

			int total = 0, anyContainer = 0, separating = 0;
			foreach (var (mapId, areas) in banners.OrderBy(b => b.Key))
			{
				Console.WriteLine($"-- map {mapId} {mapNames.GetValueOrDefault(mapId, "(no Map row)")}"
				                  + (byMap.TryGetValue(mapId, out var regions) ? $"  ({regions.Count} client regions)" : "  ⚠️ NO 1000053 record"));
				if (regions == null)
				{
					total += areas.Count;
					continue;
				}

				foreach (var a in areas.OrderBy(a => a.AreaNameId))
				{
					total++;
					var cx = a.CentreX;
					var cz = a.CentreZ;

					// Every polygon holding this banner's centre, tightest first - the tightest is the only one with a
					// claim to being a sub-area rather than the playfield. Restricted to the CROSSING-NOTIFIER scry
					// script: a region that does not tell the server the player entered it cannot be what fires a
					// server-sent banner, and that is the client's own statement rather than a guess about the name.
					var holding = regions
						.Where(r => r.IsPolygon && r.ScryId == BoundedAreaClassifier.CrossingNotifierScryId && r.Contains(cx, cz))
						.Select(r => (Region: r, Footprint: Footprint(r)))
						.OrderBy(r => r.Footprint)
						.ToList();

					if (holding.Count == 0)
					{
						Console.WriteLine($"   banner {a.AreaNameId,-10} {Hull(a),-10} -> NO client region contains it");
						continue;
					}

					anyContainer++;

					// The separation test. Another banner's centre inside the same region means it cannot be what
					// distinguishes them.
					var others = areas.Where(o => o.AreaNameId != a.AreaNameId).ToList();
					var clean = holding
						.Where(h => !others.Any(o => h.Region.Contains(o.CentreX, o.CentreZ)))
						.ToList();
					if (clean.Count > 0)
						separating++;

					var best = holding[0];
					Console.WriteLine($"   banner {a.AreaNameId,-10} {Hull(a),-10} -> {holding.Count} containing region(s); "
					                  + $"tightest '{best.Region.Name}' {best.Footprint:N0} u², bucket {BoundedAreaClassifier.Slug(BoundedAreaClassifier.Classify(best.Region.Name))}"
					                  + (clean.Count > 0 ? $"; {clean.Count} SEPARATE the banners -> '{clean[0].Region.Name}'" : "; none separate the banners"));
				}
			}

			Console.WriteLine();
			Console.WriteLine($"{total} banner region(s): {anyContainer} sit inside some client region, {separating} inside one that separates them from the map's other banners.");
			Console.WriteLine(separating == 0
				? "  -> the client does NOT ship the banner trigger volumes. MapArea's captured hull stays the only source (#348)."
				: "  -> candidates exist; check the named regions before importing (#513).");
			return;

			// A hull over ONE sighting is a point, so it fires only because of the runtime's slack - worth seeing next
			// to a match, because a point can sit inside a region it has no business claiming.
			static string Hull(CapturedBanner a)
			{
				return a.SingleSighting ? "hull=point" : $"hull={a.MaxX - a.MinX:F0}x{a.MaxZ - a.MinZ:F0}";
			}

			static double Footprint(BoundedAreaCollection.BoundedArea r)
			{
				return (r.Vertices.Max(v => v.X) - r.Vertices.Min(v => v.X)) * (double)(r.Vertices.Max(v => v.Y) - r.Vertices.Min(v => v.Y));
			}
		}

		// Every readable 1000053 record, parsed. A record that will not parse is REPORTED rather than dropped silently -
		// a census that quietly lost a playfield is worse than no census (the importer_silent_skip lesson).
		private static List<(uint MapId, IReadOnlyList<BoundedAreaCollection.BoundedArea> Areas)> ReadBoundedAreas(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);
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
				{
					return ([], null);
				}
			}
		}
	}
}
