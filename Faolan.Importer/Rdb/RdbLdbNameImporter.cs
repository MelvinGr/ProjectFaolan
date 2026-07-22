using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// #291/#300: backfills EMPTY spell names from the localized-string DB (LDB, RDB 1030002) using the English page.
	// The LDB grammar (LdbStringIndex) gives id->string keyed by an EXPLICIT id, and for spells that id == the spell
	// id (verified: 222675 -> "Set's Cold Hatred"). LANGUAGE is per-PAGE and this client bundles several
	// (FR/EN/DE/ES/RU/PL/KO), so the English cat-50210 page is picked by CRIBBING each page against the English names
	// our Spells catalog already carries from the template - the English page matches ~97%, every other language
	// <30%, so the winner is unambiguous. Only EMPTY names are filled (a spell whose template already yielded a name
	// keeps it), so a wrong page could never overwrite a good name; and if no page cribs clean, nothing is written.
	public static class RdbLdbNameImporter
	{
		private const uint LdbType = 1030002;
		private const int MinOverlap = 50;       // need enough shared ids to trust a page's language verdict
		private const int EnglishMatchPercent = 80;

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string proxyBase, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			var spellNames = await db.Spells
				.Where(s => s.Name != null && s.Name != "")
				.ToDictionaryAsync(s => s.Id, s => Norm(s.Name!));

			// Collect every cat-50210 (spell-name) page as id->string.
			var pages = new List<(uint page, Dictionary<uint, string> ids)>();
			foreach (var e in rdb.EntriesOfType(LdbType).Where(x => x.IsLocal))
			{
				byte[] body;
				try { body = rdb.ReadDecompressed(e); }
				catch { continue; }
				if (LdbStringIndex.CategoryOf(body) != LdbStringIndex.SpellNameCategory)
					continue;
				var ids = new Dictionary<uint, string>();
				foreach (var entry in LdbStringIndex.Parse(body))
					ids[entry.Id] = entry.Value;
				if (ids.Count > 0)
					pages.Add((e.Id, ids));
			}

			// Crib each page against the catalog's existing English names; the English page matches decisively.
			var english = new Dictionary<uint, string>();
			var englishPages = 0;
			foreach (var (page, ids) in pages)
			{
				int overlap = 0, match = 0;
				foreach (var (id, str) in ids)
					if (spellNames.TryGetValue(id, out var known))
					{
						overlap++;
						if (Norm(str) == known)
							match++;
					}

				if (overlap < MinOverlap || match * 100 / overlap < EnglishMatchPercent)
					continue;

				englishPages++;
				foreach (var (id, str) in ids)
					english[id] = str; // union across English pages (they partition the id space)
			}

			if (englishPages == 0)
			{
				logger?.LogWarning("RdbLdbNameImporter: no English cat-50210 page cribbed clean (>= {Pct}% over >= {Min} ids) - nothing backfilled.", EnglishMatchPercent, MinOverlap);
				return new Result(pages.Count, 0, 0);
			}

			var empties = await db.Spells.Where(s => s.Name == null || s.Name == "").ToListAsync();
			var filled = 0;
			foreach (var spell in empties)
				if (english.TryGetValue(spell.Id, out var name) && !string.IsNullOrWhiteSpace(name))
				{
					spell.Name = name;
					filled++;
				}

			await db.SaveChangesAsync();
			logger?.LogInformation("RdbLdbNameImporter: {Pages} cat-50210 pages, {EnglishPages} English, {English} English names; filled {Filled} of {Empty} empty spell names.",
				pages.Count, englishPages, english.Count, filled, empties.Count);
			return new Result(pages.Count, english.Count, filled);
		}

		// Match key: trim, case-fold, and normalise the curly apostrophes the LDB uses (’/‘) to a straight one so an
		// LDB "Set's Ruin" cribs against a catalog "Set's Ruin".
		private static string Norm(string s)
		{
			return s.Trim().ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
		}

		public readonly record struct Result(int Pages, int EnglishNames, int Filled);
	}
}
