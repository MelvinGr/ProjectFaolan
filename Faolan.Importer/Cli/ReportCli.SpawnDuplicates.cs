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

			CrossMapDuplicates(rows, await db.Maps.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name ?? "?"));
		}

		// ⭐ The duplicate class the section above STRUCTURALLY cannot see (#728). `StationaryPlacementResolver`
		// groups by `(MapId, NpcTemplateId, ClassId)`, so two records of one NPC filed under two map ids are never
		// compared - and a map's SUB-AREAS are separate playfield ids sharing one coordinate space, which is exactly
		// how that happens.
		//
		// ⚠️ This finds candidates, not duplicates. Two playfields can legitimately hold the same creature at the
		// same spot - Tortage day and night are two maps over one city - so the discrimination (#718's concurrency
		// test) is a separate question and this deliberately does not attempt it. What it does is put a number on
		// the class: which map PAIRS overlap, how much of the smaller map is involved, and whether the twins agree
		// on the columns a re-harvest of the same NPC would agree on.
		private static void CrossMapDuplicates(IReadOnlyList<NpcSpawn> rows, IReadOnlyDictionary<uint, string> maps)
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
