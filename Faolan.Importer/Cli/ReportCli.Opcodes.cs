using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// Matrix CSV, one row per capture, cell = packet count. Columns are the (direction, opcode) summary first
		// (sub-opcode collapsed, ~122), then every (direction, opcode, sub-opcode) appended at the end (header
		// "S2C 0x2000:0x737A6DF9"). Wide (~10k sub columns, many are one-off entity handles) but a full pivot table.
		private static void OpcodeCsv(IReadOnlyList<NamedCapture> captures)
		{
			var perCapOp = new Dictionary<string, Dictionary<(bool Dl, uint Op), int>>();
			var perCapSub = new Dictionary<string, Dictionary<(bool Dl, uint Op, uint Sub), int>>();
			var opCols = new SortedSet<(uint Op, bool Dl)>();
			var subCols = new SortedSet<(uint Op, uint Sub, bool Dl)>();
			foreach (var c in captures)
			{
				var ops = new Dictionary<(bool Dl, uint Op), int>();
				var subs = new Dictionary<(bool Dl, uint Op, uint Sub), int>();
				foreach (var p in c.Packets)
				{
					ops[(p.IsDownload, p.WireOpcode)] = ops.GetValueOrDefault((p.IsDownload, p.WireOpcode)) + 1;
					subs[(p.IsDownload, p.WireOpcode, p.SubOpcode)] = subs.GetValueOrDefault((p.IsDownload, p.WireOpcode, p.SubOpcode)) + 1;
					opCols.Add((p.WireOpcode, p.IsDownload));
					subCols.Add((p.WireOpcode, p.SubOpcode, p.IsDownload));
				}

				perCapOp[c.Name] = ops;
				perCapSub[c.Name] = subs;
			}

			var ops2 = opCols.ToList();
			var subs2 = subCols.ToList();

			Console.Error.WriteLine($"{ops2.Count} opcode columns + {subs2.Count} sub-opcode columns across {captures.Count} captures");
			Console.WriteLine("capture," + string.Join(",", ops2.Select(x => $"{(x.Dl ? "S2C" : "C2S")} 0x{x.Op:X4}")) + "," +
							  string.Join(",", subs2.Select(x => $"{(x.Dl ? "S2C" : "C2S")} 0x{x.Op:X4}:0x{x.Sub:X8}")));
			foreach (var c in captures.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var op = perCapOp[c.Name];
				var sub = perCapSub[c.Name];
				Console.WriteLine(Csv(c.Name) + "," + string.Join(",", ops2.Select(x => op.TryGetValue((x.Dl, x.Op), out var n)
					? n
					: 0)) + "," + string.Join(",", subs2.Select(x => sub.TryGetValue((x.Dl, x.Op, x.Sub), out var n)
					? n
					: 0)));
			}

			return;

			static string Csv(string s)
			{
				return s.Contains(',') || s.Contains('"')
					? $"\"{s.Replace("\"", "\"\"")}\""
					: s;
			}
		}

		// Grep-friendly index of every (direction, opcode, sub-opcode) tuple across the captures: per-dump counts, then
		// a global catalog (total + how many dumps carry it). Lets you find which dump to open for a given (sub)opcode.
		private static void OpcodeScan(IReadOnlyList<NamedCapture> captures)
		{
			var globalCount = new Dictionary<(bool Dl, uint Op, uint Sub), int>();
			var globalDumps = new Dictionary<(bool Dl, uint Op, uint Sub), HashSet<string>>();
			var totalPackets = 0;

			Console.WriteLine($"# Opcode index - {captures.Count} dumps");
			Console.WriteLine();
			Console.WriteLine("## Per dump");
			foreach (var c in captures.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var counts = new Dictionary<(bool Dl, uint Op, uint Sub), int>();
				foreach (var p in c.Packets)
				{
					var k = (p.IsDownload, p.WireOpcode, p.SubOpcode);
					counts[k] = counts.GetValueOrDefault(k) + 1;
					globalCount[k] = globalCount.GetValueOrDefault(k) + 1;
					(globalDumps.TryGetValue(k, out var s)
						? s
						: globalDumps[k] = []).Add(c.Name);
					totalPackets++;
				}

				Console.WriteLine($"### {c.Name}  ({c.Packets.Count} packets)");
				foreach (var (k, n) in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Op).ThenBy(kv => kv.Key.Sub))
					Console.WriteLine($"  {(k.Dl ? "S2C" : "C2S")} op=0x{k.Op:X4} sub=0x{k.Sub:X8}  {n}");
				Console.WriteLine();
			}

			var byOp = globalCount
				.GroupBy(kv => (kv.Key.Dl, kv.Key.Op))
				.Select(g => (g.Key, Total: g.Sum(x => x.Value), Subs: g.Count(), Dumps: g.SelectMany(x => globalDumps[x.Key]).Distinct().Count()))
				.OrderBy(x => x.Key.Op)
				.ThenBy(x => x.Key.Dl)
				.ToList();
			Console.WriteLine($"## Opcodes (sub-opcode collapsed) - {byOp.Count} distinct (dir,op)");
			foreach (var x in byOp)
				Console.WriteLine($"{(x.Key.Dl ? "S2C" : "C2S")} op=0x{x.Key.Op:X4}  total={x.Total}  {x.Subs} subs  in {x.Dumps} dumps");
			Console.WriteLine();

			Console.WriteLine($"## Global (sub)opcode catalog - {totalPackets} packets, {globalCount.Count} distinct (dir,op,sub) tuples");
			foreach (var (k, count) in globalCount.OrderBy(kv => kv.Key.Op).ThenBy(kv => kv.Key.Sub).ThenBy(kv => kv.Key.Dl))
				Console.WriteLine($"{(k.Dl ? "S2C" : "C2S")} op=0x{k.Op:X4} sub=0x{k.Sub:X8}  total={count}  in {globalDumps[k].Count} dumps");
		}

		// Matrix CSV for one opcode (arg, e.g. "0x2000"; default 0x2000): one row per capture, one column per
		// (direction, sub-opcode) seen under that opcode, cell = packet count. This is the drill-down of opcodes-csv -
		// e.g. every S2C 0x2000 IIR type as its own column. High-cardinality opcodes (0x2008/0x2020 handles) will be wide.
		private static void SubOpcodeCsv(IReadOnlyList<NamedCapture> captures, string? arg)
		{
			var op = string.IsNullOrWhiteSpace(arg)
				? 0x2000u
				: Convert.ToUInt32(arg.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);

			var perCap = new Dictionary<string, Dictionary<(bool Dl, uint Sub), int>>();
			var cols = new SortedSet<(uint Sub, bool Dl)>();
			foreach (var c in captures)
			{
				var counts = new Dictionary<(bool Dl, uint Sub), int>();
				foreach (var p in c.Packets)
				{
					if (p.WireOpcode != op)
						continue;

					counts[(p.IsDownload, p.SubOpcode)] = counts.GetValueOrDefault((p.IsDownload, p.SubOpcode)) + 1;
					cols.Add((p.SubOpcode, p.IsDownload));
				}

				perCap[c.Name] = counts;
			}

			var ordered = cols.ToList();

			Console.Error.WriteLine($"opcode 0x{op:X4}: {ordered.Count} distinct (dir,sub) columns across {captures.Count} captures");
			Console.WriteLine("capture," + string.Join(",", ordered.Select(x => $"{(x.Dl ? "S2C" : "C2S")} 0x{x.Sub:X8}")));
			foreach (var c in captures.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var counts = perCap[c.Name];
				Console.WriteLine(Csv(c.Name) + "," + string.Join(",", ordered.Select(x => counts.TryGetValue((x.Dl, x.Sub), out var n)
					? n
					: 0)));
			}

			return;

			static string Csv(string s)
			{
				return s.Contains(',') || s.Contains('"')
					? $"\"{s.Replace("\"", "\"\"")}\""
					: s;
			}
		}

		// One 0x5D85BFC7 MapChange body per distinct map across the captures, so the map-build constants can be diffed
		// against BuildMapChange's hardcoded (beach) values - to see whether they're per-map or universal.
		private static void MapChangeScan(IReadOnlyList<NamedCapture> captures)
		{
			// Every MapChange body per map, with the session-dynamic fields masked (instance@42, token@58, token@75), so
			// we can tell whether the map-build constants are CONSISTENT per map (1 distinct masked body across all its
			// captures) or still vary by session - the gate for whether a per-map stored template is sound.
			var byMap = new Dictionary<uint, List<(string Cap, string Masked)>>();
			foreach (var c in captures)
				foreach (var p in c.Packets)
				{
					if (!p.IsIirCarrier || p.SubOpcode != PlayfieldFCodec.Opcode || p.Bytes.Length < 16)
						continue;

					var body = p.Bytes[(12 + (int)p.Bytes.ReadU32Be(8))..];
					if (body.Length < 79)
						continue;

					var map = body.ReadU32Be(34);
					var masked = (byte[])body.Clone();
					Array.Clear(masked, 42, 4); // instance
					Array.Clear(masked, 58, 4); // playfield token
					Array.Clear(masked, 75, 4); // playfield token (2nd)
					(byMap.TryGetValue(map, out var l)
						? l
						: byMap[map] = []).Add((c.Name, Convert.ToHexString(masked)));
				}

			foreach (var (map, list) in byMap.OrderBy(kv => kv.Key))
			{
				var distinct = list.Select(x => x.Masked).Distinct().ToList();
				Console.WriteLine($"map=0x{map:X8}  {list.Count} caps  {distinct.Count} distinct masked  {(distinct.Count == 1 ? "CONSISTENT" : "*** VARIES ***")}");
				if (distinct.Count > 1)
				{
					foreach (var d in distinct)
						Console.WriteLine($"    {d}  <- {string.Join(", ", list.Where(x => x.Masked == d).Select(x => x.Cap[..Math.Min(34, x.Cap.Length)]).Distinct().Take(4))}");
				}
			}
		}
	}
}
