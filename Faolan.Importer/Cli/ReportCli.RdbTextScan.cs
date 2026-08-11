using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Faolan.Importer.Cli
{
	// `--report rdb-text-scan --report-arg <needle>[,<needle>...][:local][:<type>]`: every RDB record whose
	// DECOMPRESSED body contains a given ASCII string, counted by record TYPE.
	//
	// ⚠️ It is the TEXT twin of `rdb-word-scan`, and it exists because a raw grep over `RDB/*.rdbdata` answers a
	// different question than it appears to. Most records are zlib-compressed in the pack, so a string that is
	// plainly present in the game is absent from the file bytes - and "grep found nothing in the RDB" then reads as
	// "the client does not ship this name" when it means "the client does not ship it UNCOMPRESSED". Every hit this
	// report finds inside a compressed record is one a file grep cannot see.
	//
	// Matching is case-insensitive over Latin-1, and both plain and UTF-16LE forms are searched (a client string is
	// usually 8-bit, but XML payloads and a few tables are wide) - getting the width wrong turns a real hit into a
	// false negative, the same trap `rdb-word-scan` documents for endianness.
	//
	// ⚠️ Same CDN warning as `rdb-word-scan`, for the same reason: the default sweep FETCHES CDN-backed records over
	// HTTP one at a time and runs for the better part of an hour. It still defaults to the full sweep, because the
	// value of the report is its MISS and a local-only miss means "absent from the local packs" rather than "absent
	// from the shipped game". Pass `:local` for the quick partial pass; the header says so when you do.
	public static partial class ReportCli
	{
		private static void RdbTextScan(string rdbDir, string? arg)
		{
			var parts = (arg ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var needles = parts.FirstOrDefault()?
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
			if (needles.Length == 0)
			{
				Console.WriteLine("rdb-text-scan needs one or more strings: --report-arg RenownBystanderLovers,RenownCrier[:local][:<type>]");
				return;
			}

			var options = parts.Skip(1).ToArray();
			var localOnly = options.Any(o => o.Equals("local", StringComparison.OrdinalIgnoreCase));
			var onlyType = options.Select(o => uint.TryParse(o, out var t) ? t : (uint?)null).FirstOrDefault(t => t != null);

			using var rdb = OpenRdb(rdbDir);
			Console.WriteLine($"rdb-text-scan: looking for {string.Join(", ", needles.Select(n => $"\"{n}\""))}" +
				(onlyType is { } ot ? $" in type {ot} only" : " across every record type") +
				(localOnly
					? " -- LOCAL RECORDS ONLY (:local): a miss here means 'not in the local packs', NOT 'not in the game'"
					: " including CDN-backed records (fetched through the mirror - slow, and the reason a miss is meaningful)"));

			// Both widths of each needle, lower-cased, as byte patterns: an 8-bit Latin-1 run and a UTF-16LE one.
			var patterns = needles.ToDictionary(
				n => n,
				n => new[] { Encoding.Latin1.GetBytes(n.ToLowerInvariant()), Encoding.Unicode.GetBytes(n.ToLowerInvariant()) });

			var hits = needles.ToDictionary(n => n, _ => new SortedDictionary<uint, int>());
			var samples = needles.ToDictionary(n => n, _ => new List<string>());
			int scanned = 0, unreadable = 0;

			foreach (var e in rdb.Entries)
			{
				if (onlyType is { } only && e.Type != only)
					continue;

				if (localOnly && !e.IsLocal)
					continue;

				if (!rdb.CanRead(e))
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					unreadable++;
					continue;
				}

				scanned++;
				// Paced by record count rather than a clock, like rdb-word-scan: the local packs fly past and the CDN
				// half crawls, so the gap between two lines is the honest signal of which half the sweep is in.
				if (scanned % 5000 == 0)
					Console.WriteLine($"  ... {scanned} records ({hits.Values.Sum(h => h.Values.Sum())} hits so far)");

				// Lower-case the body ONCE per record rather than once per needle: the case-fold is the expensive part
				// and every needle searches the same bytes.
				var lower = new byte[body.Length];
				for (var i = 0; i < body.Length; i++)
					lower[i] = body[i] is >= (byte)'A' and <= (byte)'Z' ? (byte)(body[i] + 32) : body[i];

				foreach (var (needle, forms) in patterns)
				{
					var at = forms.Select(f => IndexOf(lower, f)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
					if (at < 0)
						continue;

					hits[needle][e.Type] = hits[needle].GetValueOrDefault(e.Type) + 1;
					if (samples[needle].Count < 12)
						samples[needle].Add($"      type {e.Type} id {e.Id} @{at}   {Context(body, at)}");
				}
			}

			Console.WriteLine($"  {scanned} records read ({unreadable} unreadable){(localOnly ? "; CDN-only records SKIPPED" : "")}.");
			foreach (var needle in needles)
			{
				var total = hits[needle].Values.Sum();
				Console.WriteLine();
				Console.WriteLine($"  \"{needle}\": {total} record(s)");
				if (total == 0)
				{
					Console.WriteLine("      ABSENT - this string does not occur in any decompressed RDB body, in either width.");
					continue;
				}

				foreach (var (type, n) in hits[needle].OrderByDescending(p => p.Value))
					Console.WriteLine($"      type {type,-10} {n} record(s)");

				Console.WriteLine("    first sightings:");
				foreach (var s in samples[needle])
					Console.WriteLine(s);
			}
		}

		private static int IndexOf(byte[] haystack, byte[] needle)
		{
			if (needle.Length == 0 || haystack.Length < needle.Length)
				return -1;

			var first = needle[0];
			for (var i = 0; i + needle.Length <= haystack.Length; i++)
			{
				if (haystack[i] != first)
					continue;

				var j = 1;
				while (j < needle.Length && haystack[i + j] == needle[j])
					j++;

				if (j == needle.Length)
					return i;
			}

			return -1;
		}

		// The printable neighbourhood of a hit - enough to tell an XML attribute from a bare identifier, which is
		// what says whether the surrounding record is worth reading.
		private static string Context(byte[] body, int at)
		{
			var from = Math.Max(0, at - 40);
			var to = Math.Min(body.Length, at + 80);
			var sb = new StringBuilder();
			for (var i = from; i < to; i++)
				sb.Append(body[i] is >= 0x20 and < 0x7f ? (char)body[i] : '.');

			return sb.ToString();
		}
	}
}
