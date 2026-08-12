using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spawn-duplicates`: is a cluster of near-identical STATIONARY spawns one NPC harvested twice, or a
	// genuinely tight retail placement? (Gitea #718, and #765 for the concrete Scruffy case.)
	//
	// The decision itself lives in `StationaryPlacementResolver`, which is also what the `npcs` import stamps
	// `PlacementId` from - so this report cannot show a verdict the importer would not act on, and the two cannot
	// drift. Read that class for the reasoning; this prints it.
	public static partial class ReportCli
	{
		private static async Task SpawnDuplicates(IReadOnlyList<NamedCapture> captures, string? nameFilter)
		{
			await using var db = OpenDb();

			var routed = await db.PatrolNodes.Select(p => p.NpcSpawnId).Distinct().ToHashSetAsync();
			var rows = await db
				.NpcSpawns.Include(s => s.NpcTemplate)
				.AsNoTracking()
				.Where(s => s.NpcTemplate != null)
				.ToListAsync();

			var clusters = StationaryPlacementResolver.Resolve(rows, routed, captures);
			if (!string.IsNullOrEmpty(nameFilter))
				clusters = clusters.Where(c => (c.Rows[0].NpcTemplate.Name ?? "").Contains(nameFilter, StringComparison.OrdinalIgnoreCase)).ToList();

			var byVerdict = clusters.GroupBy(c => c.Verdict).ToDictionary(g => g.Key, g => g.ToList());
			var collapsible = byVerdict.GetValueOrDefault(StationaryPlacementResolver.Verdict.OneWalked, []).Sum(c => c.Rows.Count - 1);

			Console.WriteLine($"{clusters.Count} candidate cluster(s) of stationary same-template spawns within {StationaryPlacementResolver.ClusterRadius}u"
			                  + (string.IsNullOrEmpty(nameFilter) ? "" : $" matching '{nameFilter}'"));
			Console.WriteLine($"  ONE WALKED    {Count(byVerdict, StationaryPlacementResolver.Verdict.OneWalked),4} - a single HANDLE stood on two members, so they are ONE placement: "
			                  + $"{collapsible} row(s) collapse into a variant pool");
			Console.WriteLine($"  CONCURRENT    {Count(byVerdict, StationaryPlacementResolver.Verdict.Concurrent),4} - two handles in the world at once: two real placements, left alone");
			Console.WriteLine($"  SIGHTED APART {Count(byVerdict, StationaryPlacementResolver.Verdict.SightedApart),4} - sighted, but neither test fired: no evidence either way");
			Console.WriteLine($"  NEVER SIGHTED {Count(byVerdict, StationaryPlacementResolver.Verdict.NeverSighted),4} - no capture put a spawn at any of these positions");
			Console.WriteLine();

			foreach (var c in clusters.OrderBy(c => c.Verdict).ThenByDescending(c => c.Rows.Count).ThenBy(c => c.Rows[0].NpcTemplate.Name))
			{
				var name = c.Rows[0].NpcTemplate.Name ?? "?";
				var spread = c.Rows.Max(a => c.Rows.Max(b => Vector3.Distance(a.Position, b.Position)));
				Console.WriteLine($"  {c.Verdict,-13} x{c.Evidence,-3} map {c.Rows[0].MapId,-5} {(name.Length > 26 ? name[..26] : name),-26} n={c.Rows.Count} "
				                  + $"spread={spread,5:F2}u captures-seeing-one={c.CapturesSeeingOne,-3} "
				                  + $"{(c.Verdict == StationaryPlacementResolver.Verdict.OneWalked ? $"placement=0x{c.PlacementKey():X8} " : "")}"
				                  + $"ids={string.Join(",", c.Rows.Select(r => r.Id))}");
			}

			CrossMapDuplicates(rows, await db.Maps.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name ?? "?"), captures);
		}

		// ⭐ The duplicate class the section above STRUCTURALLY cannot see (#728). `StationaryPlacementResolver`
		// groups by `(MapId, NpcTemplateId, ClassId)`, so two records of one NPC filed under two map ids are never
		// compared - and a map's SUB-AREAS are separate playfield ids sharing one coordinate space, which is exactly
		// how that happens.
		//
		// ⚠️ A pair is not a defect (#728): a row records that retail streamed this NPC to a client in that playfield,
		// and near a boundary retail streams the neighbour's NPCs across it - so both rows are true and both are
		// served, each to its own map. What follows is a census of where that happens, not a list to act on. What this puts a number on is which map PAIRS overlap, how much of the smaller map is involved,
		// and whether the twins agree on the columns a re-harvest of the same NPC would agree on - then
		// `CrossMapSightings` below decides, and on the corpus it decides against the legitimate reading on two of
		// the three pairs. Tortage day/night was the example of a legitimate pair and it is NOT one: one capture
		// files the same spot under both ids.
		private static void CrossMapDuplicates(IReadOnlyList<NpcSpawn> rows, IReadOnlyDictionary<uint, string> maps, IReadOnlyList<NamedCapture> captures)
		{
			var pairs = new Dictionary<(uint A, uint B), List<(NpcSpawn A, NpcSpawn B)>>();
			foreach (var byTemplate in rows.GroupBy(r => r.NpcTemplateId))
			{
				var list = byTemplate.ToList();
				for (var i = 0; i < list.Count; i++)
				for (var j = i + 1; j < list.Count; j++)
				{
					var (a, b) = list[i].MapId < list[j].MapId ? (list[i], list[j]) : (list[j], list[i]);
					if (a.MapId == b.MapId || Vector3.Distance(a.Position, b.Position) > StationaryPlacementResolver.ClusterRadius)
						continue;

					if (!pairs.TryGetValue((a.MapId, b.MapId), out var bucket))
						pairs[(a.MapId, b.MapId)] = bucket = [];

					bucket.Add((a, b));
				}
			}

			Console.WriteLine();
			Console.WriteLine($"⭐ CROSS-MAP candidates - the same template within {StationaryPlacementResolver.ClusterRadius}u under two DIFFERENT map ids (#728),");
			Console.WriteLine("   which the clustering above cannot see because it groups by (MapId, template, class):");
			if (pairs.Count == 0)
			{
				Console.WriteLine("   none");
				return;
			}

			foreach (var ((a, b), bucket) in pairs.OrderByDescending(p => p.Value.Count))
			{
				var onA = bucket.Select(p => p.A.Id).Distinct().Count();
				var onB = bucket.Select(p => p.B.Id).Distinct().Count();
				var totalB = rows.Count(r => r.MapId == b);
				// ⭐ Level and Health are what a SECOND harvest of the same NPC reproduces; a genuinely different
				// population at the same spot is free to differ. So the agreement rate is the discriminator that
				// costs no captures - 100% reads as one placement recorded twice, anything less as two populations.
				var agree = bucket.Count(p => p.A.Level == p.B.Level && p.A.Health == p.B.Health);
				Console.WriteLine($"   map {a,-5} {Trim(maps.GetValueOrDefault(a, "?"), 28),-28} <-> {b,-5} {Trim(maps.GetValueOrDefault(b, "?"), 28),-28}"
				                  + $" pairs={bucket.Count,-4} spawns {onA}/{onB} of {totalB} on the smaller"
				                  + $"  level+health agree {agree}/{bucket.Count}"
				                  + $"  templates={bucket.Select(p => p.A.NpcTemplateId).Distinct().Count()}");
			}

			CrossMapSightings(pairs.Keys, captures, maps);
		}

		// ⭐ The DECISIVE test for a cross-map pair, and it needs no timestamps (#728). If ONE capture sighted the
		// same template at the same spot under BOTH map ids, then no second population is involved: the extractor
		// re-keyed mid-stream and one physical NPC was filed twice. If instead each side is only ever sighted by
		// captures that saw that map alone, the two are two populations and the pair is a coincidence of geometry -
		// which is what Tortage day/night should look like.
		private static void CrossMapSightings(IEnumerable<(uint A, uint B)> mapPairs, IReadOnlyList<NamedCapture> captures, IReadOnlyDictionary<uint, string> maps)
		{
			// (capture, template, rounded position) -> the map ids that capture filed it under.
			var sightings = new Dictionary<(string Capture, uint Template, int X, int Z), HashSet<uint>>();
			foreach (var capture in captures)
			foreach (var e in NpcSpawnExtractor.Extract(capture.Packets))
			{
				if (e.TemplateId == 0)
					continue;

				var key = (capture.Name, e.TemplateId, (int)Math.Round(e.X / StationaryPlacementResolver.ClusterRadius), (int)Math.Round(e.Z / StationaryPlacementResolver.ClusterRadius));
				if (!sightings.TryGetValue(key, out var seen))
					sightings[key] = seen = [];

				seen.Add(e.MapId);
			}

			Console.WriteLine();
			Console.WriteLine("   ...and whether ONE capture filed the SAME spot under BOTH ids - which is a re-key, not a second population:");
			foreach (var (a, b) in mapPairs)
			{
				var both = sightings.Where(kv => kv.Value.Contains(a) && kv.Value.Contains(b)).ToList();
				var verdict = both.Count > 0 ? "RE-KEY - one NPC filed twice" : "no capture saw it both ways - two populations, or two captures";
				Console.WriteLine($"   {a} <-> {b}  {both.Count,4} spot(s) filed under both by one capture   {verdict}");
				foreach (var kv in both.Take(4))
					Console.WriteLine($"        {kv.Key.Capture} template {kv.Key.Template} at ~({kv.Key.X * StationaryPlacementResolver.ClusterRadius}, {kv.Key.Z * StationaryPlacementResolver.ClusterRadius})");
			}

			CrossMapVotes(mapPairs, sightings, maps);
		}

		// ⭐⭐ A DIAGNOSTIC, NOT A DEFECT LIST. A cross-map pair is TWO TRUE OBSERVATIONS, and both rows are wanted
		// (#728, settled by the maintainer: *"spawns are for the map (id) they are on"*).
		//
		// A row means "retail streamed this NPC to a client standing in this playfield". Near a sub-area boundary
		// retail streams the neighbour's NPCs across it, so it genuinely sends that Wharf Rat both to a player in the
		// Common District AND to one standing in Old Tarantia looking in. Two rows record two things retail did.
		// `GetNpcSpawns(mapId)` then serves strictly by map and a player is on exactly one map, so no client ever
		// receives both and nothing is doubled - keeping both is what reproduces retail on BOTH sides of the boundary,
		// and deleting either is what would break it (an NPC popping out of existence at a district line).
		//
		// ⚠️ The one real divergence, and it is cosmetic: the two rows are independent entities in two zones, so a rat
		// killed by someone in the Common District is still standing for an observer in Old Tarantia. Retail has one
		// NPC streamed across the line. Not worth a shared-entity model.
		//
		// ⛔ The vote below is printed to show that the obvious "resolve it" rule FAILS, so nobody builds one later.
		// "A spot's true map is the one most captures agree on" answers `2000` 15 times out of 15, by 8 captures to 1
		// - against the maintainer, who had just walked it and puts them in 2020.
		//
		// The mechanism is general, which is why this is a trap and not a bad-luck sample: **an NPC near a sub-area
		// boundary is seen far more often from OUTSIDE it than from inside**, so the vote counts OBSERVERS rather than
		// placements and the majority is the population that never went in. The sighting counts say it plainly - the
		// single "losing" capture is `..._annotated_3` with 145 Wharf Rat sightings, the session that walked the
		// district, against 1-17 glancing sightings in each of the eight "winners".
		//
		// So the vote answers "where was the observer", which is not the question, and there is no question left.
		private static void CrossMapVotes(IEnumerable<(uint A, uint B)> mapPairs,
			Dictionary<(string Capture, uint Template, int X, int Z), HashSet<uint>> sightings, IReadOnlyDictionary<uint, string> maps)
		{
			Console.WriteLine();
			Console.WriteLine("   the per-spot vote across captures - ⛔ a DEAD END kept as a warning, not a resolution (#728):");
			foreach (var (a, b) in mapPairs)
			{
				// (template, spot) -> how many captures filed it under a / under b.
				var votes = new Dictionary<(uint Template, int X, int Z), (int A, int B)>();
				foreach (var (key, seen) in sightings)
				{
					if (!seen.Contains(a) && !seen.Contains(b))
						continue;

					var spot = (key.Template, key.X, key.Z);
					votes.TryGetValue(spot, out var v);
					votes[spot] = (v.A + (seen.Contains(a) ? 1 : 0), v.B + (seen.Contains(b) ? 1 : 0));
				}

				var contested = votes.Where(v => v.Value.A > 0 && v.Value.B > 0).ToList();
				var winsA = contested.Count(v => v.Value.A > v.Value.B);
				var winsB = contested.Count(v => v.Value.B > v.Value.A);
				var tied = contested.Count - winsA - winsB;
				Console.WriteLine($"   {a} <-> {b}  contested spots {contested.Count,3}:  {a} wins {winsA,3}   {b} wins {winsB,3}   tied {tied,3}"
					+ "   ⛔ counts OBSERVERS, not placements - both rows are wanted");
				foreach (var v in contested.OrderByDescending(v => Math.Abs(v.Value.A - v.Value.B)).Take(5))
					Console.WriteLine($"        template {v.Key.Template,-7} at ~({v.Key.X * StationaryPlacementResolver.ClusterRadius,5}, {v.Key.Z * StationaryPlacementResolver.ClusterRadius,5})"
						+ $"   {a}:{v.Value.A} vs {b}:{v.Value.B}");
			}
		}

		private static string Trim(string s, int max)
		{
			return s.Length > max ? s[..max] : s;
		}

		private static int Count(Dictionary<StationaryPlacementResolver.Verdict, List<StationaryPlacementResolver.Cluster>> byVerdict,
			StationaryPlacementResolver.Verdict verdict)
		{
			return byVerdict.GetValueOrDefault(verdict, []).Count;
		}
	}
}
