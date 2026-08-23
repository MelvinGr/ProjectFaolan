using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report teleport-match`: how well the client's own teleport table (RDB 1000087) lines up with the zone-line
	// `WorldObject` rows we hold (#883). This is the measurement that decides whether importing it is possible at
	// all - the table has no door NAME, so the match back to a row can only be POSITIONAL, and a positional key is
	// only usable if the two coordinate systems actually agree.
	public static partial class ReportCli
	{
		private static async Task TeleportMatch(string? rdbDir)
		{
			var rdb = string.IsNullOrEmpty(rdbDir) ? RdbDir() : rdbDir;
			using var reader = OpenRdb(rdb);
			var table = RdbTeleportExtractor.Extract(reader);
			if (table.Count == 0)
			{
				Console.WriteLine($"teleport-match: RDB {RdbTeleportExtractor.ResourceType}:{RdbTeleportExtractor.RecordId} is not readable in this RDB.");
				return;
			}

			await using var db = OpenDb();
			var doors = await db.WorldObjects
				.Where(o => WorldTeleportExtractor.DoorSubOpcodes.Contains(o.SubOpcode))
				.Select(o => new { o.Id, o.MapId, o.Name, o.PositionX, o.PositionY, o.PositionZ, o.InteractAction, o.DestinationMapId, o.DestinationX })
				.ToListAsync();

			var byMap = doors.GroupBy(d => d.MapId).ToDictionary(g => g.Key, g => g.ToList());

			Console.WriteLine($"teleport-match: RDB table has {table.Count:N0} teleports over {table.Select(t => t.SourceMapId).Distinct().Count():N0} source playfields.");
			Console.WriteLine($"  we hold {doors.Count:N0} zone-line world objects over {byMap.Count:N0} maps; {doors.Count(d => d.DestinationMapId != null):N0} already carry a destination.");
			Console.WriteLine();

			int noMapRows = 0, matched = 0;
			var dists = new List<float>();
			var agree = 0;
			var wouldGain = 0;
			var disagree = new List<string>();
			var ambiguousAt2 = 0;

			foreach (var t in table)
			{
				if (!byMap.TryGetValue(t.SourceMapId, out var candidates))
				{
					noMapRows++;
					continue;
				}

				var ranked = candidates
					.Select(d => (Door: d, Dist: MathF.Sqrt((d.PositionX - t.X) * (d.PositionX - t.X) + (d.PositionY - t.Y) * (d.PositionY - t.Y) + (d.PositionZ - t.Z) * (d.PositionZ - t.Z))))
					.OrderBy(r => r.Dist)
					.ToList();

				var best = ranked[0];
				dists.Add(best.Dist);
				if (best.Dist > 8f)
					continue;

				matched++;
				if (ranked.Count(r => r.Dist <= 2f) > 1)
					ambiguousAt2++;

				// Where we ALREADY know the destination from a capture, does the client's table agree? That is the
				// only independent check on whether the positional match picked the right door.
				if (best.Door.DestinationMapId is { } known)
				{
					// Provenance proxy: a CAPTURED link carries the arrival position; a #341 name-resolved one is
					// map-only. Worth separating, because "the capture disagrees" and "our name guess disagrees" are
					// very different claims about which side is wrong.
					var captured = best.Door.DestinationX != null;
					if (known == t.DestMapId)
						agree++;
					else if (disagree.Count < 12)
						disagree.Add($"    map {t.SourceMapId} '{best.Door.Name}' @{best.Dist:0.00}u: {(captured ? "CAPTURED" : "name-guess")} -> {known}, RDB -> {t.DestMapId}");
				}
				else
				{
					wouldGain++;
				}
			}

			var sorted = dists.OrderBy(d => d).ToList();
			Console.WriteLine($"  {noMapRows:N0} teleports are on a map with no imported world objects (the world data is incomplete).");
			Console.WriteLine($"  {sorted.Count:N0} could be measured against a door row. Nearest-door distance:");
			foreach (var (label, cut) in new (string Label, float Cut)[] { ("<= 0.5u", 0.5f), ("<= 1u", 1f), ("<= 2u", 2f), ("<= 5u", 5f), ("<= 8u", 8f), ("<= 20u", 20f) })
			{
				var n = sorted.Count(d => d <= cut);
				Console.WriteLine($"    {label,-8} {n,5}  ({100.0 * n / Math.Max(1, sorted.Count),5:0.0}%)");
			}
			if (sorted.Count > 0)
				Console.WriteLine($"    median {sorted[sorted.Count / 2]:0.0}u, max {sorted[^1]:0.0}u");

			Console.WriteLine();
			Console.WriteLine($"  matched within 8u: {matched:N0}   ({ambiguousAt2:N0} had MORE THAN ONE door within 2u - a positional key cannot separate those)");
			Console.WriteLine($"  ⭐ {wouldGain:N0} matched doors have NO destination today - that is what importing this table would add right now.");
			Console.WriteLine($"  cross-check where we already have one: {agree:N0} agree, {disagree.Count:N0} disagree.");
			foreach (var d in disagree)
				Console.WriteLine(d);
		}
	}
}
