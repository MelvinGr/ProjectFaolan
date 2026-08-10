using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Faolan.Importer.Cli
{
	// `--report ldb-search --report-arg [<category>:]<substring>`: which LDB strings the client ships whose TEXT
	// contains a substring, with the id that names them. `--report-arg id:<n>` goes the other way and prints what
	// EVERY category holds at one id, which is how an opaque wire id gets a name when its category is not known
	// (a tag, a title, a nameplate label).
	//
	// The lookup that was missing. Every other LDB tool answers "what is the string at this id" - which is the
	// direction the runtime needs, and the wrong one for a content gap: when the question is *"does the client ship an
	// item called Ironwood, and at what id"*, the name is all there is to go on. `rdb-search` does not help despite
	// the name; it greps MESH manifest source paths.
	//
	// ⚠️ An LDB category is shipped ONCE PER LANGUAGE, in adjacent pages carrying the same (category, id) pairs, so a
	// bare match count would be multiplied by however many languages the install bundles. Hits are therefore grouped by
	// (category, id) and reported with the DISTINCT texts across pages, which is also what makes a translation visible
	// rather than confusing: an English hit and its six translations are one row.
	public static partial class ReportCli
	{
		private static void LdbSearch(string rdbDir, string arg)
		{
			if ((arg ?? "").StartsWith("id:", StringComparison.OrdinalIgnoreCase)
			    && uint.TryParse(arg![3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wantedId))
			{
				LdbById(rdbDir, wantedId);
				return;
			}

			var (category, needle) = Split(arg);
			if (string.IsNullOrWhiteSpace(needle))
			{
				Console.WriteLine("ldb-search: --report-arg is [<category>:]<substring> (e.g. \"50200:Ironwood\") or id:<n>.");
				return;
			}

			using var rdb = OpenRdb(rdbDir);

			// (category, id) -> the distinct texts seen for it, and how many pages carried each.
			var hits = new Dictionary<(uint Category, uint Id), Dictionary<string, int>>();
			var pages = 0;
			var scanned = 0;
			foreach (var (_, pageCategory, strings) in LdbPagesOf(rdb))
			{
				pages++;
				if (category is { } wanted && pageCategory != wanted)
					continue;

				scanned++;
				foreach (var (id, value) in strings)
				{
					if (value == null || !value.Contains(needle, StringComparison.OrdinalIgnoreCase))
						continue;

					if (!hits.TryGetValue((pageCategory, id), out var texts))
						hits[(pageCategory, id)] = texts = [];

					texts[value] = texts.GetValueOrDefault(value) + 1;
				}
			}

			Console.WriteLine($"ldb-search '{needle}'{(category is { } c ? $" in category {c}" : " across every category")}: "
			                  + $"{hits.Count} (category, id) hit(s) over {scanned} of {pages} TDC2 page(s)");
			Console.WriteLine();
			Console.WriteLine("  category        id  pages  text");
			foreach (var ((cat, id), texts) in hits.OrderBy(h => h.Key.Category).ThenBy(h => h.Key.Id))
			{
				var ordered = texts.OrderByDescending(t => t.Value).ToList();
				Console.WriteLine($"  {cat,8}  {id,9}  {ordered.Sum(t => t.Value),5}  {ordered[0].Key}");
				// The other languages, when they differ - one line each, so a hit in the wrong language is obvious.
				foreach (var (text, count) in ordered.Skip(1))
					Console.WriteLine($"  {"",8}  {"",9}  {count,5}    (also) {text}");
			}
		}

		// What every category holds at ONE id - the reverse lookup. An id alone does not say which category it belongs
		// to, so this prints all of them and lets the reader judge: a tag id landing in a category of achievement names
		// and nowhere else is the answer, and one landing in six unrelated categories is a collision.
		private static void LdbById(string rdbDir, uint id)
		{
			using var rdb = OpenRdb(rdbDir);

			// category -> the distinct texts at this id, with how many pages (i.e. languages) carried each.
			var hits = new Dictionary<uint, Dictionary<string, int>>();
			var pages = 0;
			foreach (var (_, category, strings) in LdbPagesOf(rdb))
			{
				pages++;
				if (!strings.TryGetValue(id, out var value) || value == null)
					continue;

				if (!hits.TryGetValue(category, out var texts))
					hits[category] = texts = [];

				texts[value] = texts.GetValueOrDefault(value) + 1;
			}

			Console.WriteLine($"ldb id {id} (0x{id:X}): present in {hits.Count} categor(ies) over {pages} TDC2 page(s)");
			Console.WriteLine();
			Console.WriteLine("  category  pages  text");
			foreach (var (category, texts) in hits.OrderBy(h => h.Key))
			{
				var ordered = texts.OrderByDescending(t => t.Value).ToList();
				Console.WriteLine($"  {category,8}  {ordered.Sum(t => t.Value),5}  {ordered[0].Key}");
				foreach (var (text, count) in ordered.Skip(1))
					Console.WriteLine($"  {"",8}  {count,5}    (also) {text}");
			}
		}

		// "<category>:<substring>" or a bare substring. A leading number followed by ':' is the category; anything else
		// is all needle, so a search for a string that happens to contain a colon still works.
		private static (uint? Category, string Needle) Split(string arg)
		{
			var text = arg ?? "";
			var colon = text.IndexOf(':');
			if (colon > 0 && uint.TryParse(text[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var category))
				return (category, text[(colon + 1)..]);

			return (null, text);
		}
	}
}
