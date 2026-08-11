using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report stat-series --report-arg <id>[,<id>...]`: the ordered value sequence of one or more stats, per
	// capture and per entity handle, out of the captured `StatIIR` stream.
	//
	// The general question it answers is "what does this stat DO at runtime" - a census says a stat is pushed, a
	// SERIES says whether it counts up, counts down, resets, or is restated unchanged. `--report morph-stats` is
	// the census half; this is the other.
	//
	// Written for #656 (the daily kill counters `106` / `1000069 DailyPvEKills` / `1000070 DailyPvPKills`, whose
	// whole evidence was a hand-read of one live session), and kept general because every "what is this stat"
	// ticket wants exactly this view.
	//
	// ⚠️ Grouped by (capture, handle). A handle is session-scoped, so grouping on the handle alone merges unrelated
	// entities across captures - the same rule the buff level-slot measurement needed.
	public static partial class ReportCli
	{
		private static void StatSeries(IReadOnlyList<NamedCapture> captures, string? arg)
		{
			var wanted = (arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(s => uint.TryParse(s, out var u) ? u : 0u).Where(u => u != 0).ToHashSet();
			if (wanted.Count == 0)
			{
				Console.WriteLine("--report-arg <statId>[,<statId>...] is required (decimal).");
				return;
			}

			var series = new Dictionary<(string Capture, uint Handle, uint Stat), List<uint>>();
			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				if (p is not { IsIirCarrier: true, SubOpcode: StatCodec.Opcode } || !p.TryParse(StatCodec.Parse, out var stat))
					continue;

				if (!wanted.Contains(stat.StatId))
					continue;

				var key = (capture.Name, stat.Handle, stat.StatId);
				if (!series.TryGetValue(key, out var values))
					series[key] = values = [];

				values.Add(stat.Value);
			}

			if (series.Count == 0)
			{
				Console.WriteLine($"no captured StatIIR carries any of {string.Join(",", wanted.Order())}.");
				return;
			}

			foreach (var stat in wanted.Order())
			{
				var runs = series.Where(kv => kv.Key.Stat == stat).ToList();
				if (runs.Count == 0)
				{
					Console.WriteLine($"stat {stat}: ABSENT from the captured StatIIR stream.");
					Console.WriteLine();
					continue;
				}

				var all = runs.SelectMany(r => r.Value).ToList();
				// The shape question: does it MOVE, and which way. A stat that is only ever restated at the same
				// value is a snapshot field; one that steps in one direction is a counter.
				var steps = runs.SelectMany(r => r.Value.Zip(r.Value.Skip(1), (a, b) => (long)b - a)).Where(d => d != 0).ToList();
				Console.WriteLine($"stat {stat}: {all.Count} record(s) over {runs.Count} (capture, handle) run(s), "
				                  + $"values {all.Min()}..{all.Max()}, {steps.Count} move(s) "
				                  + $"({steps.Count(d => d > 0)} up, {steps.Count(d => d < 0)} down)");
				foreach (var run in runs.OrderByDescending(r => r.Value.Count).Take(8))
					Console.WriteLine($"    0x{run.Key.Handle:X8} {Trim(run.Key.Capture, 44),-44} {string.Join(",", Collapse(run.Value))}");

				Console.WriteLine();
			}
		}

		// A run with the repeats collapsed - `20,20,20,19` prints as `20 x3,19`, because a restatement is not a move
		// and a screenful of them hides the ones that are.
		private static IEnumerable<string> Collapse(List<uint> values)
		{
			for (var i = 0; i < values.Count;)
			{
				var j = i;
				while (j < values.Count && values[j] == values[i])
					j++;

				yield return j - i > 1 ? $"{values[i]} x{j - i}" : values[i].ToString();
				i = j;
			}
		}
	}
}
