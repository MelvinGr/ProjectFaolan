using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report fixture-names --report-arg <name>[,<name>...]`: where a personal name still sits INSIDE the
	// committed fixture archive's packet bytes (Gitea #465).
	//
	// #465's text sweep removed every occurrence from source, tests and docs, and the manifest half removed the
	// dump FILE NAMES; what it could not reach is the names carried in the packets themselves, as ordinary
	// `[u16 len][latin1]` wire strings. This report is the measurement of that residue - which fixtures, which
	// IIRs, how many - because the ticket's own figure was written before the manifest work and nothing has
	// re-counted since.
	//
	// ⭐ **The framing is PROTOBUF, not the `[u16 len][latin1]` the ticket assumed**, and that changes what option B
	// costs. Every hit in both carrying IIRs is a length-delimited protobuf field - `2a 06 <6-char name>` on
	// FullPlayerCharacterIIR (field 5) and `0a 08 <8-char name>` on SimpleCharFullUpdateIIR (field 1). So the length is
	// a single varint byte, not a u16, and nothing outside the field is addressed by offset: an EQUAL-LENGTH
	// substitution changes exactly the name's own bytes and ripples nowhere - no nested message length moves, no
	// following field shifts. The report prints, per hit, whether it is framed that way, because that is what says a
	// hit is a real wire string rather than bytes that happen to spell a name.
	//
	// Read-only. Substituting is a separate, deliberate operation - this only says what is there.
	public static partial class ReportCli
	{
		private static int FixtureNames(string archivePath, string? arg)
		{
			var needles = (arg ?? "")
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (needles.Count == 0)
			{
				Console.Error.WriteLine("fixture-names: pass one or more names, e.g. --report-arg <name>,<name>");
				Console.Error.WriteLine("               the blocked set is the one .git/hooks/personal-names.py carries.");
				return 1;
			}

			var (corpus, named) = FixtureArchiveWriter.ReadBodiesForNameScan(archivePath);
			Console.WriteLine($"fixture-names: scanning {corpus.Count} corpus packets + {named.Count} named fixtures in {archivePath}");
			Console.WriteLine($"  needles: {string.Join(", ", needles)}");
			Console.WriteLine();

			var byIir = new Dictionary<(string Needle, uint Iir), (int Hits, int Packets, int Prefixed)>();
			var byNamed = new Dictionary<(string Needle, string Key), (int Hits, int Prefixed)>();

			foreach (var entry in corpus)
			{
				foreach (var needle in needles)
				{
					var (hits, prefixed) = Occurrences(entry.Full, needle);
					if (hits == 0)
						continue;

					var key = (needle, entry.Iir);
					var cur = byIir.GetValueOrDefault(key);
					byIir[key] = (cur.Hits + hits, cur.Packets + 1, cur.Prefixed + prefixed);
				}
			}

			foreach (var (key, body) in named)
			{
				foreach (var needle in needles)
				{
					var (hits, prefixed) = Occurrences(body, needle);
					if (hits > 0)
						byNamed[(needle, key)] = (hits, prefixed);
				}
			}

			if (byIir.Count == 0 && byNamed.Count == 0)
			{
				Console.WriteLine("  CLEAN - no needle occurs in any packet body.");
				return 0;
			}

			if (byIir.Count > 0)
			{
				Console.WriteLine("CORPUS SLICE");
				Console.WriteLine($"  {"needle",-12} {"IIR",-28} {"hits",6} {"packets",8} {"len-prefixed",13}");
				foreach (var row in byIir.OrderBy(r => r.Key.Needle).ThenByDescending(r => r.Value.Hits))
				{
					var iir = Enum.IsDefined(typeof(IirTypes), row.Key.Iir) ? ((IirTypes)row.Key.Iir).ToString() : $"0x{row.Key.Iir:X8}";
					Console.WriteLine($"  {row.Key.Needle,-12} {iir,-28} {row.Value.Hits,6} {row.Value.Packets,8} {row.Value.Prefixed,13}");
				}

				Console.WriteLine();
			}

			if (byNamed.Count > 0)
			{
				Console.WriteLine("NAMED FIXTURES");
				Console.WriteLine($"  {"needle",-12} {"key",-44} {"hits",6} {"len-prefixed",13}");
				foreach (var row in byNamed.OrderBy(r => r.Key.Key))
					Console.WriteLine($"  {row.Key.Needle,-12} {row.Key.Key,-44} {row.Value.Hits,6} {row.Value.Prefixed,13}");

				Console.WriteLine();
			}

			// A WINDOW around the first few hits, because the counts alone cannot say how the name is framed - and
			// the framing is the whole question for option B. Printed for the corpus and the named half alike.
			Console.WriteLine("FRAMING (first 4 hits, 12 bytes either side)");
			var shown = 0;
			foreach (var entry in corpus)
			{
				if (shown >= 4)
					break;

				foreach (var needle in needles)
				{
					var at = IndexOfLatin1(entry.Full, needle);
					if (at < 0 || shown >= 4)
						continue;

					var from = Math.Max(0, at - 12);
					var to = Math.Min(entry.Full.Length, at + needle.Length + 12);
					var iir = Enum.IsDefined(typeof(IirTypes), entry.Iir) ? ((IirTypes)entry.Iir).ToString() : $"0x{entry.Iir:X8}";
					Console.WriteLine($"  {needle} in {iir} @{at}: {Convert.ToHexStringLower(entry.Full[from..to])}");
					Console.WriteLine($"    {"",-(0)}ascii: {Printable(entry.Full[from..to])}");
					shown++;
				}
			}

			Console.WriteLine();

			var total = byIir.Values.Sum(v => v.Hits) + byNamed.Values.Sum(v => v.Hits);
			var prefixedTotal = byIir.Values.Sum(v => v.Prefixed) + byNamed.Values.Sum(v => v.Prefixed);
			Console.WriteLine($"  {total} occurrence(s), {prefixedTotal} of them framed as a protobuf length-delimited field.");
			Console.WriteLine("  A framed hit is a real wire string and can be substituted in place by an EQUAL-LENGTH pseudonym");
			Console.WriteLine("  (#465 option B) - the length byte does not move, so nothing after it shifts. The rest are byte");
			Console.WriteLine("  coincidences or a framing this scan does not know; look at the window above before assuming.");
			Console.WriteLine();
			Console.WriteLine("  ⚠️ Needles that PREFIX one another double-count: a 7-char name whose first 6 chars are");
			Console.WriteLine("     another needle is counted under both.");
			return 0;
		}

		private static int IndexOfLatin1(byte[] haystack, string needle)
		{
			var pattern = Encoding.Latin1.GetBytes(needle);
			for (var i = 0; i + pattern.Length <= haystack.Length; i++)
			{
				var match = true;
				for (var j = 0; j < pattern.Length && match; j++)
					match = haystack[i + j] == pattern[j];

				if (match)
					return i;
			}

			return -1;
		}

		private static string Printable(byte[] bytes)
		{
			var sb = new StringBuilder(bytes.Length);
			foreach (var b in bytes)
				sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');

			return sb.ToString();
		}

		/// <summary>
		///     Occurrences of <paramref name="needle" /> as Latin-1 bytes, and how many of those are immediately
		///     preceded by a big-endian u16 equal to the needle's length - i.e. are the whole of a wire string.
		/// </summary>
		private static (int Hits, int Prefixed) Occurrences(byte[] haystack, string needle)
		{
			var pattern = Encoding.Latin1.GetBytes(needle);
			int hits = 0, prefixed = 0;

			for (var i = 0; i + pattern.Length <= haystack.Length; i++)
			{
				var match = true;
				for (var j = 0; j < pattern.Length && match; j++)
					match = haystack[i + j] == pattern[j];

				if (!match)
					continue;

				hits++;

				// PROTOBUF framing: `[tag][len][bytes]`, where the tag's low three bits are wire type 2
				// (length-delimited) and the len varint is a single byte for anything under 128 characters - which
				// every character name is. This is the form both carrying IIRs use.
				if (i >= 2 && haystack[i - 1] == pattern.Length && (haystack[i - 2] & 0x07) == 2)
					prefixed++;
			}

			return (hits, prefixed);
		}
	}
}
