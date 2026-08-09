using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report bcc-census`: every BehaviorControlCenterIIR in the corpus, bucketed by its behaviorId, with the tail
	// lengths each one carries (#536/#549). This is the scan the per-Command reader table in
	// docs/re/behaviorcontrolcenter-grammar.md is measured against, shipped as a report rather than kept as a scratch
	// script so the numbers in that doc can be re-derived rather than trusted.
	//
	// It also dumps, in FULL, every record whose behaviorId is not in `BehaviorId` - the client's own classmap. Those
	// are the interesting ones: an id the client cannot look up is either a command we have not found the
	// registration for, or (as #549 turned out) a record we are reading wrong.
	public static partial class ReportCli
	{
		// `--report-arg <hex>` additionally dumps, in full, every record whose PHASE byte is <hex> (e.g. `0E`);
		// `--report-arg cmd:<hex>` dumps every record of one COMMAND instead (e.g. `cmd:27`). Phase was the only
		// filter for a while, which is the wrong axis when the question is "what do this command's records look
		// like" - and for a command with four records in the whole corpus, phase pulls in thousands of neighbours.
		private static void BccCensus(IReadOnlyList<NamedCapture> captures, string? phaseArg)
		{
			var wantsCommand = (phaseArg ?? "").StartsWith("cmd:", StringComparison.OrdinalIgnoreCase);
			var command = wantsCommand ? ParseHexByte(phaseArg![4..]) : null;
			var phase = wantsCommand ? null : ParseHexByte(phaseArg);
			var known = Enum.GetValues<BehaviorId>().ToDictionary(b => (uint)b, b => b.ToString());
			var buckets = new SortedDictionary<uint, Bucket>();
			var interesting = new List<(string Capture, CapturedPacket Packet, BehaviorControlCenterIIR Bcc)>();
			var total = 0;
			var short_ = 0;

			foreach (var c in captures)
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
						continue;

					total++;
					if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
					{
						short_++; // the dump cut the record short - not a grammar failure
						continue;
					}

					if (!buckets.TryGetValue(bcc.BehaviorId, out var bucket))
						buckets[bcc.BehaviorId] = bucket = new Bucket();

					bucket.Add(p.IsDownload, bcc);

					if (!known.ContainsKey(bcc.BehaviorId) || (byte)bcc.Seq == phase || bcc.BehaviorId == command)
						interesting.Add((c.Name, p, bcc));
				}

			Console.WriteLine($"{total} BehaviorControlCenterIIR records across {captures.Count} captures ({short_} truncated by the dump)");
			Console.WriteLine();
			Console.WriteLine("  id     name                            s2c    c2s   phases          tail lengths");

			foreach (var (id, b) in buckets)
			{
				var name = known.TryGetValue(id, out var n) ? n : "*** not in the classmap ***";
				Console.WriteLine($"  0x{id:X2}   {name,-30}  {b.Down,5}  {b.Up,5}   {Join(b.Seqs, s => $"0x{s:X4}"),-14}  {Join(b.TailLengths, t => t.ToString())}");
			}

			if (interesting.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine($"{interesting.Count} record(s) in full (an id outside the classmap, or the requested phase/command):");

			foreach (var (capture, p, bcc) in interesting)
			{
				Console.WriteLine();
				Console.WriteLine($"  {capture} frame {p.Frame} {(p.IsDownload ? "s2c" : "c2s")}  id=0x{bcc.BehaviorId:X8} seq=0x{bcc.Seq:X4} actor=0x{bcc.Actor:X8} delay={bcc.DelayCentiseconds?.ToString() ?? "-"}");
				Console.WriteLine($"    record[{p.Record.Length}] {Convert.ToHexStringLower(p.Record)}");
				Console.WriteLine($"    args[{bcc.Args.Length}] {Convert.ToHexStringLower(bcc.Args)}");
				Console.WriteLine($"    tail[{bcc.Tail.Length}] {Convert.ToHexStringLower(bcc.Tail)}");
			}
		}

		private static byte? ParseHexByte(string? arg)
		{
			var text = arg?.Trim();
			if (string.IsNullOrEmpty(text))
				return null;

			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				text = text[2..];

			return byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
				? value
				: throw new ArgumentException($"--report-arg must be a hex phase byte (e.g. 0E), not \"{arg}\"");
		}

		private static string Join<T>(SortedSet<T> values, Func<T, string> format) => string.Join(",", values.Select(format));

		private sealed class Bucket
		{
			public int Down;
			public int Up;
			public readonly SortedSet<ushort> Seqs = [];
			public readonly SortedSet<int> TailLengths = [];

			public void Add(bool isDownload, BehaviorControlCenterIIR bcc)
			{
				if (isDownload)
					Down++;
				else
					Up++;

				Seqs.Add(bcc.Seq);
				TailLengths.Add(bcc.Tail.Length);
			}
		}
	}
}
