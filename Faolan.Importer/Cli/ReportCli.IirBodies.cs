using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report iir-bodies --report-arg <IirTypeNameSubstring>`: every captured body of one IIR type, grouped by a
	// shape signature so a variant stands out instead of drowning in near-identical rows.
	//
	// The `method-bodies` sibling filters by RPC method, which cannot reach these: an IIR rides inside `PassBlob`, so
	// its type is the sub-opcode at the front of the body, not the method. This is how a question like "is there a
	// second variant of MainQuestUpdateIIR, and how many samples of it are there?" gets asked.
	//
	// The signature is `length` plus the first two words after the standard `[u32 class][u32 handle]` head - which is
	// where a discriminator sits when a record has one. Grouping on it turns "48 bodies" into "these N shapes", and a
	// shape with one member is exactly the thing a grammar guess must not be built on.
	public static partial class ReportCli
	{
		private static void IirBodies(IReadOnlyList<NamedCapture> captures, string? filter)
		{
			if (string.IsNullOrWhiteSpace(filter))
			{
				Console.WriteLine("--report iir-bodies needs --report-arg <IirTypeNameSubstring> (e.g. MainQuestUpdate).");
				return;
			}

			var wanted = Enum.GetValues<IirTypes>()
				.Where(t => t.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
				.ToDictionary(t => (uint)t, t => t.ToString());
			if (wanted.Count == 0)
			{
				Console.WriteLine($"No IirTypes member matches '{filter}'.");
				return;
			}

			var rows = new List<(string Type, string Capture, int Frame, bool Down, byte[] Record)>();
			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				if (!p.IsIirCarrier || !wanted.TryGetValue(p.SubOpcode, out var name))
					continue;

				rows.Add((name, capture.Name, p.Frame, p.IsDownload, p.Record.ToArray()));
			}

			Console.WriteLine($"iir-bodies '{filter}' -> {string.Join(", ", wanted.Values)}");
			Console.WriteLine($"{rows.Count} record(s) across {rows.Select(r => r.Capture).Distinct().Count()} capture(s).\n");
			if (rows.Count == 0)
				return;

			// shape = length + the two words after [u32 class][u32 handle]. A record that carries a discriminator
			// carries it there, so shapes separate variants without knowing the grammar.
			foreach (var g in rows.GroupBy(r => Shape(r.Record)).OrderByDescending(g => g.Count()))
			{
				var first = g.First();
				Console.WriteLine($"  shape {g.Key}   x{g.Count(),4}   {g.Select(r => r.Capture).Distinct().Count()} capture(s)");
				Console.WriteLine($"    e.g. {first.Capture} frame {first.Frame} {(first.Down ? "s2c" : "c2s")}");
				Console.WriteLine($"    {Convert.ToHexStringLower(first.Record)}");

				// A shape with few members is the one a grammar must not be guessed from, so print all of them.
				if (g.Count() <= 6)
				{
					foreach (var r in g.Skip(1))
						Console.WriteLine($"    {Convert.ToHexStringLower(r.Record)}   [{r.Capture} f{r.Frame}]");
				}

				Console.WriteLine();
			}

			return;

			static string Shape(byte[] record)
			{
				// [u32 iirType][u32 class][u32 handle] then the discriminating words.
				var words = new List<string>();
				for (var off = 12; off + 4 <= record.Length && words.Count < 2; off += 4)
					words.Add($"{BitConverter.ToUInt32(record, off):X8}");

				// The byte between handle and the first word is a marker on several of these records, so a shape that
				// splits on a bare word boundary would merge two variants; include the byte at 12 explicitly.
				var marker = record.Length > 12 ? $"{record[12]:X2}" : "--";
				return $"len={record.Length,4} b12={marker} [{string.Join(' ', words)}]";
			}
		}
	}
}
