using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports the client's bounded-area trigger volumes (RDB 1000053) that a SUBSYSTEM ACTUALLY CONSUMES into
	// MapRegion + MapRegionVertex, and links each consumer to its region. The server has no RDB access, so geometry
	// the runtime needs has to come through here. Survey + format: docs/rdb/bounded-areas-1000053.md.
	//
	// Two consumers today, both of which replace a guessed radius with the client's real volume:
	//   - RESURRECTION DISCOVERY (#519): a `*_RezDiscovery` / `*_Discovery` region that CONTAINS one of our
	//     RespawnPoints is that point's discovery volume. 13 of them exist against a hardcoded 30-unit radius.
	//   - QUEST ReachArea REGIONS (#521): a goal whose `TrailingCategory` is 51200 carries a bounded-area reference;
	//     LDB category 51200 (`bounded_areas.bounded_area`) turns `TrailingRefId` into the region's NAME, and that
	//     names a region in the goal's playfield.
	//
	// ⚠️ ONLY WANTED REGIONS ARE IMPORTED - about 34 of the table's 8,917. Bulk-importing thousands of named volumes
	// nothing reads is data that rots, and it is the same limit RdbHazardImporter draws for lava. Everything a
	// consumer asked for and did not get is REPORTED, never skipped silently (importer_silent_skip_is_data_loss).
	//
	// Clean rebuild each run: the set is fully derived from the fixed client RDB, and the derived key means a
	// re-import reproduces the same ids, so faolan.sql does not churn.
	public static class RdbRegionImporter
	{
		// `bounded_areas.bounded_area` - the client's id -> name table for these regions, named by the category
		// descriptor table in the TDL1 language record. Plaintext (unlike the quest/dialogue categories).
		public const uint BoundedAreaNameCategory = 51200;

		// The id space above this is a VARIANT playfield numbering (0x40000000 | <playfield>) that is not a Map.Id -
		// the same set RdbHazardImporter skips.
		private const uint VariantIdBase = 0x40000000;

		// A RESURRECTION-POINT discovery volume. `Rez` is load-bearing and is the client's own discriminator: it
		// separates `BA_MountainGlade_RezDiscovery` (a respawn pad, 16x20 to 29x33 units) from `FrostFields_Discovery`
		// and `FotD_Discovery`, which are AREA discovery - the zone-codex mechanic, 157x185 and 114x74.
		//
		// ⚠️ The looser `_discovery` spelling matches BOTH, and the two area regions each contain a respawn point of
		// the SAME NAME ("Frost Fields", "Field of the Dead"), so a containment test alone happily links them and
		// widens those points' discovery from a 30-unit radius to a 157x185 box. That is a different mechanic wearing
		// a matching name, not a tighter volume - so anchor on `Rez` and leave area discovery to whoever implements it.
		private static readonly Regex DiscoveryName = new(@"rezdiscov", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBaseUrl)
		{
			using var rdb = new RdbReader(rdbDir, cdnBaseUrl);

			var ourMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var areasByMap = ReadRegions(rdb, ourMaps);
			var names = BoundedAreaNames(rdb);

			var notes = new List<string>();
			// The wanted set, keyed by (map, name) so two consumers asking for the same region share one row.
			var wanted = new Dictionary<(uint Map, string Name), BoundedAreaCollection.BoundedArea>();

			var points = await db.RespawnPoints.ToListAsync();
			var discoveryFor = MatchDiscoveryVolumes(points, areasByMap, wanted);

			var goals = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == BoundedAreaNameCategory && g.TrailingRefId > 0)
				.ToListAsync();
			var regionFor = MatchGoalRegions(goals, names, areasByMap, wanted, notes);

			// ---- Rebuild the region rows ----
			// Clear the FKs first: both are SetNull, but EF will not reorder the delete for us, and a stale link to a
			// row that is about to be removed is exactly the kind of thing that trips the SQLite FK check on rebuild.
			foreach (var p in points)
				p.DiscoveryRegionId = null;
			foreach (var g in goals)
				g.RegionId = null;
			await db.SaveChangesAsync();

			db.MapRegions.RemoveRange(db.MapRegions);
			await db.SaveChangesAsync();

			var vertices = 0;
			foreach (var ((mapId, name), area) in wanted)
			{
				var region = new MapRegion
				{
					Id = MapRegion.ComputeId(mapId, name),
					MapId = mapId,
					Name = name,
					MinY = area.MinY,
					MaxY = area.MaxY,
					MinX = area.Vertices.Min(v => v.X),
					MaxX = area.Vertices.Max(v => v.X),
					MinZ = area.Vertices.Min(v => v.Y),
					MaxZ = area.Vertices.Max(v => v.Y),
					ScryId = area.ScryId
				};

				for (var i = 0; i < area.Vertices.Count; i++)
				{
					region.Vertices.Add(new MapRegionVertex
					{
						Sequence = i,
						X = area.Vertices[i].X,
						Z = area.Vertices[i].Y
					});
				}

				db.MapRegions.Add(region);
				vertices += area.Vertices.Count;
			}

			await db.SaveChangesAsync();

			// ---- Link the consumers ----
			foreach (var (point, key) in discoveryFor)
				point.DiscoveryRegionId = MapRegion.ComputeId(key.Map, key.Name);
			foreach (var (goal, key) in regionFor)
				goal.RegionId = MapRegion.ComputeId(key.Map, key.Name);

			await db.SaveChangesAsync();
			return new Result(wanted.Count, vertices, discoveryFor.Count, regionFor.Count, notes);
		}

		// Every parseable 1000053 record for a playfield we host, as map -> the regions with a testable footprint.
		private static Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> ReadRegions(RdbReader rdb, HashSet<uint> ourMaps)
		{
			var byMap = new Dictionary<uint, List<BoundedAreaCollection.BoundedArea>>();
			foreach (var e in rdb.EntriesOfType(BoundedAreaCollection.ResourceType).Where(rdb.CanRead).OrderBy(e => e.Id))
			{
				if (e.Id >= VariantIdBase || !ourMaps.Contains(e.Id))
					continue;

				byte[] dec;
				try
				{
					dec = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (dec.Length == 0 || !BoundedAreaCollection.TryParse(dec, out var collection) || collection == null)
					continue;

				byMap[e.Id] = collection.Areas.Where(a => a.IsPolygon).ToList();
			}

			return byMap;
		}

		// LDB category 51200: the bounded-area id -> name table, from the ENGLISH pages. Language matters even for
		// these short names, and taking "whatever page came first" silently yields French.
		private static Dictionary<uint, string> BoundedAreaNames(RdbReader rdb)
		{
			var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);
			var names = new Dictionary<uint, string>();
			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(rdb.CanRead))
			{
				if (languageByPage.GetValueOrDefault(e.Id) != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (LdbStringIndex.CategoryOf(body) != BoundedAreaNameCategory)
					continue;

				foreach (var entry in LdbStringIndex.Parse(body))
					names[entry.Id] = entry.Value;
			}

			return names;
		}

		// A respawn point's discovery volume: the SMALLEST discovery-named region containing it. Smallest because the
		// pads nest inside larger audio/collision volumes that happen to share the naming, and the tightest match is
		// the one the designer drew around the point.
		private static List<(RespawnPoint Point, (uint Map, string Name) Key)> MatchDiscoveryVolumes(
			List<RespawnPoint> points,
			Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			Dictionary<(uint, string), BoundedAreaCollection.BoundedArea> wanted)
		{
			var matched = new List<(RespawnPoint, (uint, string))>();
			foreach (var p in points)
			{
				if (!areasByMap.TryGetValue(p.MapId, out var areas))
					continue;

				BoundedAreaCollection.BoundedArea? best = null;
				var bestArea = double.MaxValue;
				foreach (var a in areas)
				{
					if (!DiscoveryName.IsMatch(a.Name) || !a.Contains(p.PositionX, p.PositionZ))
						continue;

					var box = (a.Vertices.Max(v => v.X) - a.Vertices.Min(v => v.X)) * (double)(a.Vertices.Max(v => v.Y) - a.Vertices.Min(v => v.Y));
					if (box >= bestArea)
						continue;

					best = a;
					bestArea = box;
				}

				if (best == null)
					continue;

				wanted[(p.MapId, best.Name)] = best;
				matched.Add((p, (p.MapId, best.Name)));
			}

			return matched;
		}

		// A quest goal's region: LDB[51200][TrailingRefId] gives the NAME, then find it on the goal's playfield.
		// A goal with NO MapId still resolves when the name occurs on exactly one playfield we host (goal 14073's
		// `BossRoom` is only on 2027) - ambiguity is reported rather than guessed.
		private static List<(QuestGoalDefinition Goal, (uint Map, string Name) Key)> MatchGoalRegions(
			List<QuestGoalDefinition> goals,
			Dictionary<uint, string> names,
			Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			Dictionary<(uint, string), BoundedAreaCollection.BoundedArea> wanted,
			List<string> notes)
		{
			var matched = new List<(QuestGoalDefinition, (uint, string))>();
			foreach (var g in goals)
			{
				if (!names.TryGetValue(g.TrailingRefId, out var name))
				{
					notes.Add($"goal {g.GoalId}: LDB[{BoundedAreaNameCategory}][{g.TrailingRefId}] has no English entry");
					continue;
				}

				var candidates = (g.MapId is { } mid
						? areasByMap.TryGetValue(mid, out var onMap)
							? onMap.Where(a => a.Name == name).Select(a => (Map: mid, Area: a))
							: []
						: areasByMap.SelectMany(kv => kv.Value.Where(a => a.Name == name).Select(a => (Map: kv.Key, Area: a))))
					.ToList();

				if (candidates.Count == 0)
				{
					notes.Add($"goal {g.GoalId}: region '{name}' not found on {(g.MapId is { } m ? $"map {m}" : "any hosted playfield")}");
					continue;
				}

				if (candidates.Count > 1)
				{
					notes.Add($"goal {g.GoalId}: region '{name}' is ambiguous - on maps {string.Join(", ", candidates.Select(c => c.Map).Distinct())}");
					continue;
				}

				var (map, area) = candidates[0];
				wanted[(map, name)] = area;
				matched.Add((g, (map, name)));
			}

			return matched;
		}

		public readonly record struct Result(int Regions, int Vertices, int DiscoveryLinks, int GoalLinks, IReadOnlyList<string> Notes);
	}
}
