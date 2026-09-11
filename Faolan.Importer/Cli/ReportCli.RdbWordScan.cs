using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faolan.Core.Extensions;

namespace Faolan.Importer.Cli
{
	// `--report rdb-word-scan --report-arg <hex>[,<hex>...][:<type>]`: every RDB record containing a given 32-bit
	// word, counted by record type.
	//
	// It exists because our property readers cannot answer this. `ItemTemplateProperties`,
	// `RdbItemImporter.TryReadPropertyField` and `RdbSpellImporter.ReadIncreasingFields` all bound a field id to
	// <= 2000, so a field whose id is larger is invisible to every one of them by CONSTRUCTION - and "our reader
	// found nothing" then reads as "the data is not there". The client's stat table has plenty above that bound
	// (`LootTable` 2,000,139; `LootTable2..5` 2,000,413-416), so the question "is this shipped at all?" needs a scan
	// that knows nothing about grammar.
	//
	// A hit is a word, not a field. The scan cannot tell a field id from a coincidental value, a length or half a
	// float - four bytes are four bytes. So a hit is a lead to go and read the record at that offset, and a MISS is
	// the strong result: a word that appears nowhere in 400k records is not in the shipped data.
	//
	// Both endiannesses are searched, because RDB payloads are little-endian while plenty of embedded wire records
	// are big-endian, and getting that wrong turns a real hit into a false negative.
	//
	// It reads CDN-backed records too, and that is slow. `RdbReader.CanRead` is true for a CDN-only entry the
	// moment a mirror is configured, so a sweep over `rdb.Entries` does not read files - it FETCHES a quarter of a
	// million records over HTTP, one at a time. The first run of this report did exactly that and had to be killed.
	//
	// It still defaults to the full sweep, because the fast alternative answers a WEAKER question. The value of this
	// report is its miss, and a local-only miss means "absent from the local packs", not "absent from the shipped
	// game" - and for several types the CDN half is most of the game. Pass `:local` for a quick partial pass; it
	// says so in its own header when you do.
	public static partial class ReportCli
	{
		private static void RdbWordScan(string rdbDir, string? arg)
		{
			var spec = (arg ?? "").Split(':', 2, StringSplitOptions.TrimEntries);
			var wanted = spec[0]
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(t => uint.TryParse(t.TrimStart('0', 'x', 'X'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
					? v
					: uint.TryParse(t, out var d)
						? d
						: 0u)
				.Where(v => v != 0)
				.ToArray();
			if (wanted.Length == 0)
			{
				Console.WriteLine("rdb-word-scan needs one or more words: --report-arg 1E850B,1E861D[:<type>]");
				return;
			}

			var options = spec.Length == 2
				? spec[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				: [];
			// `:local` OPTS OUT of the CDN half. The default is the full sweep - see the class comment for why the
			// slow answer is the right default here.
			var localOnly = options.Any(o => o.Equals("local", StringComparison.OrdinalIgnoreCase));
			var onlyType = options.Select(o => uint.TryParse(o, out var t2)
					? t2
					: (uint?)null)
				.FirstOrDefault(t => t != null);

			using var rdb = OpenRdb(rdbDir);
			Console.WriteLine($"rdb-word-scan: looking for {string.Join(", ", wanted.Select(w => $"0x{w:X8} ({w})"))}" +
				(onlyType is { } ot ? $" in type {ot} only" : " across every record type") +
				(localOnly
					? " -- LOCAL RECORDS ONLY (:local): a miss here means 'not in the local packs', NOT 'not in the game'"
					: " including CDN-backed records (fetched through the mirror - slow, and the reason a miss is meaningful)"));

			// hits[word][type] = how many records of that type contain the word at least once.
			var hits = wanted.ToDictionary(w => w, _ => new SortedDictionary<uint, int>());
			var samples = wanted.ToDictionary(w => w, _ => new List<string>());
			var lookup = wanted.ToHashSet();
			var seenHere = new HashSet<uint>();
			int scanned = 0, unreadable = 0;

			foreach (var e in rdb.Entries)
			{
				if (onlyType is { } only && e.Type != only)
					continue;

				if (localOnly && !e.IsLocal)
					continue; // the caller asked for the fast partial pass; the header says so

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
				// A CDN-inclusive sweep runs for the better part of an hour and printed nothing until it finished, so
				// a long run and a hung one looked identical. The tally is per 5000 records rather than on a timer -
				// no clock, and it paces itself: the local packs fly past, the CDN half crawls, and the gap between
				// two lines is the honest signal of which half it is in.
				if (scanned % 5000 == 0)
					Console.WriteLine($"  ... {scanned} records ({hits.Values.Sum(h => h.Values.Sum())} hits so far)");

				// one aligned pass per record, matching every wanted word at once. The naive form (a full byte-wise
				// search per word) is `records x words x bytes` and does not finish on a 400k-record RDB - this is
				// ~24x less work and answers the same question.
				//
				// 4-byte ALIGNED only. RDB payloads are word-structured, so a field id sits on a boundary; an
				// unaligned occurrence would be a coincidence rather than a field, and skipping them is what makes
				// the scan tractable. A miss is therefore "not present as a word", not "not present as bytes".
				for (var i = 0; i + 4 <= body.Length; i += 4)
				{
					var le = body.ReadU32Le(i);
					var be = body.ReadU32Be(i);
					if (!lookup.Contains(le) && !lookup.Contains(be))
						continue;

					var word = lookup.Contains(le)
						? le
						: be;
					if (seenHere.Add(word))
					{
						hits[word][e.Type] = hits[word].GetValueOrDefault(e.Type) + 1;
						if (samples[word].Count < 8)
							samples[word].Add($"      type {e.Type} id {e.Id} @{i}");
					}
				}

				seenHere.Clear();
			}

			Console.WriteLine($"  {scanned} records read ({unreadable} unreadable){(localOnly ? "; CDN-only records SKIPPED" : "")}.");
			foreach (var word in wanted)
			{
				var total = hits[word].Values.Sum();
				Console.WriteLine();
				Console.WriteLine($"  0x{word:X8} ({word}): {total} record(s)");
				if (total == 0)
				{
					Console.WriteLine("      ABSENT - this word does not occur in the shipped RDB at all, in either endianness.");
					continue;
				}

				foreach (var (type, n) in hits[word].OrderByDescending(p => p.Value))
					Console.WriteLine($"      type {type,-10} {n} record(s)");

				Console.WriteLine("    first sightings:");
				foreach (var s in samples[word])
					Console.WriteLine(s);
			}
		}

	}
}
