using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
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
		}

		private static int Count(Dictionary<StationaryPlacementResolver.Verdict, List<StationaryPlacementResolver.Cluster>> byVerdict,
			StationaryPlacementResolver.Verdict verdict)
		{
			return byVerdict.GetValueOrDefault(verdict, []).Count;
		}
	}
}
