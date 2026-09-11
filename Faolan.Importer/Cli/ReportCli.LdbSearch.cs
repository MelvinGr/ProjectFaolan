using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report ldb-search --report-arg [<category>:]<substring>`: which LDB strings the client ships whose text
	// contains a substring, with the id that names them. `--report-arg id:<n>` goes the other way and prints what
	// every category holds at one id, which is how an opaque wire id gets a name when its category is not known
	// (a tag, a title, a nameplate label).
	//
	// Every other LDB tool answers "what is the string at this id", which is the direction the runtime needs and the
	// wrong one for a content gap: when the question is "does the client ship an item called Ironwood, and at what
	// id", the name is all there is to go on. `rdb-search` does not help despite its name; it greps mesh manifest
	// source paths.
	//
	// English only. An LDB category is shipped once per language, in adjacent pages carrying the same (category, id)
	// pairs, so an unfiltered search returns each hit once per bundled language - several lines per result, with
	// whichever language has the most pages as the headline. The walk is therefore filtered to the English pages up
	// front.
	//
	// The filter is the client's own statement of a page's language (`LdbLanguageIndex`, RDB type 1030001), never a
	// sniff of the text - whole pages ship untranslated, so a text sniffer scores the Polish page of a category as
	// English and silently unions several languages.
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

			var (category, needle) = Split(arg ?? "");
			if (string.IsNullOrWhiteSpace(needle))
			{
				Console.WriteLine("ldb-search: --report-arg is [<category>:]<substring> (e.g. \"50200:Ironwood\") or id:<n>.");
				return;
			}

			using var rdb = OpenRdb(rdbDir);

			// (category, id) -> the English text.
			var hits = new Dictionary<(uint Category, uint Id), string>();
			var pages = 0;
			var scanned = 0;
			foreach (var (_, pageCategory, strings) in EnglishLdbPagesOf(rdb))
			{
				pages++;
				if (category is { } wanted && pageCategory != wanted)
					continue;

				scanned++;
				foreach (var (id, value) in strings)
				{
					if (value == null || !value.Contains(needle, StringComparison.OrdinalIgnoreCase))
						continue;

					hits[(pageCategory, id)] = value;
				}
			}

			Console.WriteLine($"ldb-search '{needle}'{(category is { } c ? $" in category {c}" : " across every category")}: "
			                  + $"{hits.Count} (category, id) hit(s) over {scanned} of {pages} English TDC2 page(s)");
			Console.WriteLine();
			Console.WriteLine("  category        id  text");
			foreach (var ((cat, id), text) in hits.OrderBy(h => h.Key.Category).ThenBy(h => h.Key.Id))
				Console.WriteLine($"  {cat,8}  {id,9}  {text}");
		}

		// What every category holds at one id - the reverse lookup. An id alone does not say which category it belongs
		// to, so this prints all of them and lets the reader judge: a tag id landing in a category of achievement names
		// and nowhere else is the answer, and one landing in six unrelated categories is a collision.
		private static void LdbById(string rdbDir, uint id)
		{
			using var rdb = OpenRdb(rdbDir);

			var hits = new SortedDictionary<uint, string>();
			var pages = 0;
			foreach (var (_, category, strings) in EnglishLdbPagesOf(rdb))
			{
				pages++;
				if (strings.TryGetValue(id, out var value) && value != null)
					hits[category] = value;
			}

			Console.WriteLine($"ldb id {id} (0x{id:X}): present in {hits.Count} categor(ies) over {pages} English TDC2 page(s)");
			Console.WriteLine();
			Console.WriteLine("  category  text");
			foreach (var (category, text) in hits)
				Console.WriteLine($"  {category,8}  {text}");
		}

		// The English pages only - see the type comment for why this is not optional. A page the client's language
		// records do not claim (39 of the 1103 shipped ones are stale/duplicate) belongs to no language and is skipped,
		// which is the same rule LdbStringIndex.EnglishPlaintext applies.
		private static IEnumerable<(uint PageId, uint Category, Dictionary<uint, string> Strings)> EnglishLdbPagesOf(RdbReader rdb)
		{
			var language = LdbLanguageIndex.LanguageByPage(rdb);
			foreach (var page in LdbPagesOf(rdb))
			{
				if (language.GetValueOrDefault(page.PageId) == LdbLanguageIndex.English)
					yield return page;
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
