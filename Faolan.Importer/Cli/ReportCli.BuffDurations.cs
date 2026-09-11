using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report buff-durations [--report-arg <spell name substring or base id>]`
	//
	// Every captured `MagicBuffIIR` apply grouped by spell, with the durations retail stated for it and the
	// `Spells.DurationMs` we would build from. It is the evidence view behind `MagicBuffCorpusTests`'
	// duration gate: that test asserts counts, this prints the rows the counts came from.
	//
	// Two populations it separates, because they look identical in a total:
	//   * shortfall - the wire is below the row, and decreases across a capture for the same spell. That is retail
	//     re-sending a long buff with its remaining time (the restore path), not a data disagreement.
	//   * excess - the wire is above the row, which the restore reading cannot explain and which is therefore a
	//     wrong `Spells.DurationMs`.
	public static partial class ReportCli
	{
		private static void BuffDurations(IReadOnlyList<NamedCapture> captures, string? filter)
		{
			var wanted = (filter ?? "").Trim();
			var perSpell = new Dictionary<uint, List<(string Capture, uint Centiseconds, double Seconds)>>();

			foreach (var capture in captures)
			{
				// Seconds into the capture, which is what separates a LOGIN RESTORE (arrives in the entry burst,
				// carrying whatever was left) from a fresh mid-session apply (carries the base duration).
				var start = capture.Packets.Count > 0
					? capture.Packets[0].Ticks
					: 0L;
				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: MagicBuffCodec.Opcode })
						continue;

					if (!p.TryParse(MagicBuffCodec.Parse, out var buff) || buff.Form != MagicBuffIds.FormApply)
						continue;

					if (buff.Data.Length != MagicBuffDataCodec.Length)
						continue;

					perSpell.TryAdd(buff.SpellBaseId, []);
					perSpell[buff.SpellBaseId].Add((capture.Name, MagicBuffDataCodec.Parse(buff.Data).Duration, (p.Ticks - start) / 1e7));
				}
			}

			using var db = OpenDb();
			var ids = perSpell.Keys.ToList();
			var rows = db.Spells.Where(s => ids.Contains(s.Id)).ToDictionary(s => s.Id, s => (s.Name, s.DurationMs));

			Console.WriteLine($"{"spell",-44} {"applies",7} {"row_cs",10}  wire_cs (distinct)");
			var excess = new List<string>();
			foreach (var (spellId, applies) in perSpell.OrderBy(kv => rows.GetValueOrDefault(kv.Key).Name ?? "~"))
			{
				var (name, durationMs) = rows.TryGetValue(spellId, out var row)
					? row
					: ($"(uncatalogued 0x{spellId:X8})", 0u);
				if (wanted.Length > 0 && !name.Contains(wanted, StringComparison.OrdinalIgnoreCase) && spellId.ToString() != wanted)
					continue;

				var rowCs = durationMs / 10;
				var distinct = applies.Select(a => a.Centiseconds).Distinct().OrderBy(c => c).ToList();
				Console.WriteLine($"{name[..Math.Min(44, name.Length)],-44} {applies.Count,7} {rowCs,10}  {string.Join(" ", distinct.Take(12))}{(distinct.Count > 12 ? " ..." : "")}");

				if (wanted.Length > 0)
				{
					foreach (var group in applies.GroupBy(a => a.Capture).OrderBy(g => g.Key))
						Console.WriteLine($"      {group.Key}: {string.Join(" ", group.OrderBy(a => a.Seconds).Select(a => $"{a.Centiseconds}@{a.Seconds:0.#}s"))}");
				}

				if (distinct.Count > 0 && distinct[^1] > rowCs)
					excess.Add($"0x{spellId:X8} '{name}': row {rowCs} cs, retail sent up to {distinct[^1]} cs on {applies.Count(a => a.Centiseconds > rowCs)} of {applies.Count} applies");
			}

			if (excess.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine("EXCESS - retail stated a duration LONGER than the row, which the restore reading cannot explain (#659):");
			foreach (var line in excess)
				Console.WriteLine($"  {line}");
		}
	}
}
