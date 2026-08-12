using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;

namespace Faolan.Importer.Cli
{
	// `--report fctx-header`: what the two u32s after an FCTX texture's magic are (#464).
	//
	// #464 lists them as *"an RDB asset format, needs an RDB-wide census rather than captures"* - i.e. the one entry
	// on that census whose evidence is not the capture corpus at all, and which therefore has no reason to be stuck.
	// This is that census.
	//
	// ⚠️ The whole point is to read the two words AGAINST the header fields around them, not on their own. A value
	// census would report "0x18 on most textures" and leave it there; what settles a header word is whether it
	// PREDICTS something the rest of the header already states (the payload's length, its block size, its mip
	// chain), because that is what a size or an offset does.
	public static partial class ReportCli
	{
		// The RDB types whose payloads are FCTX textures - RdbAssetExtractor's own list, so this walks exactly what
		// the extractor treats as a texture rather than a second opinion about which types those are.
		private static readonly uint[] FctxRdbTypes = [1010004, 1010006, 1010218, 1066606, 1066610];

		private static void FctxHeaderCensus(string rdbDir, string? arg)
		{
			using var rdb = OpenRdb(rdbDir);

			// ⚠️ SAMPLED, and it has to be. Reading a texture's 24-byte header means INFLATING the whole texture -
			// the RDB has no partial-decompress - so a full walk is tens of thousands of multi-megabyte zlib streams
			// and runs for the better part of an hour. The sample is per TYPE so a rare format cannot be crowded out
			// by a common one, the cap is printed with the total beside it, and `--report-arg <n>` raises it.
			var perType = uint.TryParse((arg ?? "").Trim(), out var wanted) ? (int)wanted : DefaultFctxSample;

			var rows = new List<(uint Type, uint Id, uint U1, uint U2, int Width, int Height, int Mip, string FourCc, int Payload)>();
			var available = 0;
			foreach (var type in FctxRdbTypes)
			foreach (var entry in rdb.EntriesOfType(type).Where(rdb.CanRead).Take(perType))
			{
				byte[] payload;
				try
				{
					payload = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue;
				}

				available++;
				if (!FctxConverter.IsFctx(payload) || payload.Length < FctxHeaderBytes)
					continue;

				rows.Add((type, entry.Id, payload.ReadU32Le(4), payload.ReadU32Le(8), payload.ReadU16Le(12),
					payload.ReadU16Le(14), payload[16], Encoding.ASCII.GetString(payload, 20, 4), payload.Length));
			}

			var total = FctxRdbTypes.Sum(t => rdb.EntriesOfType(t).Count(rdb.CanRead));
			Console.WriteLine($"FCTX header census over {rows.Count:N0} texture(s) in RDB types {string.Join("/", FctxRdbTypes)} (#464)");
			Console.WriteLine($"  SAMPLED: the first {perType:N0} readable entries per type, {available:N0} read of {total:N0} available."
			                  + " Raise with --report-arg <n>; a full walk inflates every texture in the client.");
			if (rows.Count == 0)
			{
				Console.WriteLine("  no readable FCTX payloads - is --Content:RdbDir pointing at the client's RDB folder?");
				return;
			}

			Console.WriteLine();
			foreach (var (name, values) in new (string, IEnumerable<uint>)[] { ("unknown1 @4", rows.Select(r => r.U1)), ("unknown2 @8", rows.Select(r => r.U2)) })
			{
				var counts = values.GroupBy(v => v).OrderByDescending(g => g.Count()).ToList();
				var top = string.Join("  ", counts.Take(6).Select(g => $"0x{g.Key:X} x{g.Count():N0}"));
				Console.WriteLine($"  {name,-12} {counts.Count,6:N0} distinct   {top}{(counts.Count > 6 ? "  …" : "")}");
			}

			// ⭐ The question a value census cannot ask. A header word that equals the payload length, or the length
			// minus the header, or the pixel-data length, is a SIZE - and that is checkable per row rather than
			// argued from a distribution.
			Console.WriteLine();
			Console.WriteLine("  does either word PREDICT something the rest of the header states?");
			var predicates = new (string Name, Func<(uint Type, uint Id, uint U1, uint U2, int Width, int Height, int Mip, string FourCc, int Payload), uint> Of)[]
			{
				("payload length", r => (uint)r.Payload),
				("payload - header", r => (uint)(r.Payload - FctxHeaderBytes)),
				("width", r => (uint)r.Width),
				("height", r => (uint)r.Height),
				("width * height", r => (uint)(r.Width * r.Height)),
				("mip count", r => (uint)r.Mip)
			};

			foreach (var (word, of) in new (string, Func<(uint Type, uint Id, uint U1, uint U2, int Width, int Height, int Mip, string FourCc, int Payload), uint>)[]
			         { ("unknown1", r => r.U1), ("unknown2", r => r.U2) })
			foreach (var (name, other) in predicates)
			{
				var hits = rows.Count(r => of(r) == other(r));
				if (hits > 0)
					Console.WriteLine($"    {word} == {name,-18} on {hits,6:N0} / {rows.Count:N0} = {100.0 * hits / rows.Count:0.0}%");
			}

			// And the cross-tab that says whether a word is a per-FORMAT constant rather than per-texture data.
			Console.WriteLine();
			Console.WriteLine($"  {"fourcc",-8} {"textures",8} {"u1 distinct",12} {"u2 distinct",12}  most common (u1, u2)");
			foreach (var group in rows.GroupBy(r => r.FourCc).OrderByDescending(g => g.Count()))
			{
				var modal = group.GroupBy(r => (r.U1, r.U2)).OrderByDescending(g => g.Count()).First();
				Console.WriteLine($"  {group.Key,-8} {group.Count(),8:N0} {group.Select(r => r.U1).Distinct().Count(),12:N0}"
				                  + $" {group.Select(r => r.U2).Distinct().Count(),12:N0}  (0x{modal.Key.U1:X}, 0x{modal.Key.U2:X}) x{modal.Count():N0}");
			}
		}

		// The FCTX header the converter reads: magic[4], two u32s, w/h u16, mips, fourcc[4].
		private const int FctxHeaderBytes = 24;

		// Per-type sample size. Big enough that a per-format constant shows as one, small enough to run in seconds.
		private const int DefaultFctxSample = 400;
	}
}
