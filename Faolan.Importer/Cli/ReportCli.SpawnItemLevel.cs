using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spawn-item-level` (#755): what LEVEL a character SPAWN puts on each worn item, measured against the
	// `Items.Level` column, over the capture DUMPS.
	//
	// `PlayerSpawnEquipment.BuildEntry` hardcodes `Level = 1` on every worn item it emits. The snapshot entry's
	// `level` was measured and turned out to be exactly `Items.Level` (8,479 of 8,479), which is why that emitter now
	// reads the column - but that result does NOT transfer here: the spawn's worn equipment is a different message
	// (`ProtoCoreCharacterIIR.InventoryEntry` inside FA015807, not `ProtoInventoryGameEntry`), written by a different
	// part of retail's server, and assuming the two agree is exactly the kind of unmeasured carry-over #755 exists to
	// avoid.
	//
	// ⚠️ The importer already HARVESTS this field (`ItemHashHarvester.ExtractFromSpawnEquipment` records
	// `acg.Level`), so it looks measured - but `Record` only ever fills a MISSING level and never overwrites one, so
	// a spawn that disagreed with the snapshot would be silently discarded rather than reported. That is why the
	// number has to come from a sweep rather than from the column.
	public static partial class ReportCli
	{
		private static void SpawnItemLevel(IReadOnlyList<NamedCapture> captures, string? outPath)
		{
			using var db = OpenDb();
			var levels = db.Items.AsNoTracking().ToDictionary(i => i.Id, i => (uint)i.Level);
			var names = db.Items.AsNoTracking().ToDictionary(i => i.Id, i => i.Name ?? "");

			// (template, spawn level) -> how many entries said so, so a template that is not constant shows up as two
			// rows rather than as whichever sighting happened to be last.
			var seen = new Dictionary<(uint Template, uint Level), int>();
			var spawns = 0;
			var entries = 0;

			foreach (var capture in captures)
			foreach (var packet in capture.Packets)
			{
				var body = packet.Bytes;
				if (body.Length < 12)
					continue;

				var headerLength = (int)body.ReadU32Be(8);
				var bodyStart = 12 + headerLength;
				if (bodyStart >= body.Length)
					continue;

				if (!SpawnBodyReader.TryReadCharacterMessage(body[bodyStart..], out var read))
					continue;

				spawns++;
				foreach (var entry in read.Message.InventoryEntries)
				{
					var acg = entry.Item;
					if (acg == null || acg.Template1 == 0)
						continue;

					entries++;
					var key = (acg.Template1, (uint)acg.Level);
					seen[key] = seen.GetValueOrDefault(key) + 1;
				}
			}

			Console.WriteLine($"spawn-item-level: {spawns} character spawns, {entries} worn-item entries, {seen.Keys.Select(k => k.Template).Distinct().Count()} distinct templates.");
			Console.WriteLine();

			// Is the spawn's level even a property of the TEMPLATE? If one template ships two different levels the
			// question "what should we emit" has no answer from data, and that has to be said before any comparison.
			var varying = seen.Keys.GroupBy(k => k.Template).Where(g => g.Count() > 1).ToList();
			Console.WriteLine($"  templates carrying more than one spawn level: {varying.Count}");
			foreach (var group in varying.Take(10))
				Console.WriteLine($"    {group.Key,-9} \"{names.GetValueOrDefault(group.Key, "")}\"  levels {string.Join(", ", group.Select(k => k.Level).Order())}");

			Console.WriteLine();

			int matches = 0, differs = 0, unknown = 0;
			var histogram = new Dictionary<uint, int>();
			var mismatches = new List<string>();
			foreach (var ((template, level), count) in seen)
			{
				histogram[level] = histogram.GetValueOrDefault(level) + count;
				if (!levels.TryGetValue(template, out var column))
				{
					unknown += count;
					continue;
				}

				if (column == level)
					matches += count;
				else
				{
					differs += count;
					if (mismatches.Count < 20)
						mismatches.Add($"    {template,-9} \"{names.GetValueOrDefault(template, "")}\"  spawn={level} Items.Level={column}  x{count}");
				}
			}

			Console.WriteLine("  the spawn's level vs Items.Level (what the snapshot entry was proven to carry):");
			Console.WriteLine($"    agree {matches}   differ {differs}   template not in the catalog {unknown}");
			foreach (var line in mismatches)
				Console.WriteLine(line);

			Console.WriteLine();
			Console.WriteLine("  what levels the spawn actually carries:");
			foreach (var (level, count) in histogram.OrderByDescending(p => p.Value).Take(15))
				Console.WriteLine($"    level {level,-5} x{count}");

			if (outPath == null)
				return;

			System.IO.File.WriteAllLines(outPath, seen
				.OrderBy(p => p.Key.Template)
				.Select(p => $"{p.Key.Template}\t{names.GetValueOrDefault(p.Key.Template, "")}\t{p.Key.Level}\t{levels.GetValueOrDefault(p.Key.Template)}\t{p.Value}")
				.Prepend("template\tname\tspawnLevel\titemsLevel\tsightings"));
			Console.WriteLine($"  wrote {outPath}");
		}
	}
}
