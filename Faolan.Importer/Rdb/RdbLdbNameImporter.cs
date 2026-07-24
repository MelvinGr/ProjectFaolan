using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// #291/#300: backfills EMPTY spell + item names from the localized-string DB (LDB, RDB 1030002).
	//
	// The LDB grammar (LdbStringIndex) gives id->string keyed by an EXPLICIT id. For both categories the id == the
	// record id (verified: spell 222675 -> "Set's Cold Hatred"; item names in cat 50200 crib 99% against our catalog).
	// LANGUAGE is per-PAGE and this client bundles several (FR/EN/DE/ES/RU/PL/KO), so the English page is picked by
	// CRIBBING each page against the names the catalog ALREADY carries from the template: English matches ~97-99%,
	// every other language <30% - unambiguous, no hard-coded page id. Only EMPTY names are filled, so a wrong page
	// could never overwrite a good name; and if no page cribs clean, nothing is written.
	//
	// Categories:
	//   50210 = spell names (id == spell id).
	//   50200 = item "dev" names (id == item id) - the SAME string source our ExtractName already pulls for the named
	//           items, so this recovers a catalog-CONSISTENT name for items whose own template yielded none. (The
	//           polished localized item names in cat 50220 are keyed by a SEPARATE name-id, not the item id, so they
	//           need the item record's name-id field decoded first - a follow-up, see docs/rdb/ldb-string-index.md.)
	public static class RdbLdbNameImporter
	{
		internal const int EnglishMatchPercent = 80;
		internal const int MinOverlap = 50; // need enough shared ids to trust a page's language verdict
		internal const uint LdbType = 1030002;

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string proxyBase, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			var spellNames = await db.Spells.Where(s => s.Name != null && s.Name != "").ToDictionaryAsync(s => s.Id, s => Norm(s.Name!));
			var itemNames = await db.Items.Where(i => i.Name != null && i.Name != "").ToDictionaryAsync(i => i.Id, i => Norm(i.Name!));

			// One pass over the LDB: bucket the name-category pages we care about as id->string.
			var spellPages = new List<Dictionary<uint, string>>();
			var itemPages = new List<Dictionary<uint, string>>();
			foreach (var e in rdb.EntriesOfType(LdbType).Where(x => x.IsLocal))
			{
				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				var cat = LdbStringIndex.CategoryOf(body);
				if (cat != LdbStringIndex.SpellNameCategory && cat != LdbStringIndex.ItemNameCategory)
					continue;

				var ids = new Dictionary<uint, string>();
				foreach (var entry in LdbStringIndex.Parse(body))
					ids[entry.Id] = entry.Value;
				if (ids.Count == 0)
					continue;

				(cat == LdbStringIndex.SpellNameCategory
					? spellPages
					: itemPages).Add(ids);
			}

			var spellEnglish = EnglishNames(spellPages, spellNames);
			var itemEnglish = EnglishNames(itemPages, itemNames);

			var spellsFilled = await BackfillAsync(db.Spells, s => s.Id, (s, n) => s.Name = n, spellEnglish);
			var itemsFilled = await BackfillAsync(db.Items, i => i.Id, (i, n) => i.Name = n, itemEnglish);

			await db.SaveChangesAsync();
			logger?.LogInformation("RdbLdbNameImporter: spell English={SpellEng} (filled {SpellFill}); item English={ItemEng} (filled {ItemFill}).", spellEnglish.Count, spellsFilled,
				itemEnglish.Count, itemsFilled);
			return new Result(spellEnglish.Count, spellsFilled, itemEnglish.Count, itemsFilled);
		}

		private static async Task<int> BackfillAsync<T>(DbSet<T> set, Func<T, uint> idOf, Action<T, string> setName, Dictionary<uint, string> english) where T : class
		{
			if (english.Count == 0)
				return 0;

			var empties = await set.ToListAsync();
			var filled = 0;
			foreach (var row in empties)
			{
				if (idOf(row) is var id && english.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) && IsEmptyName(row))
				{
					setName(row, name);
					filled++;
				}
			}

			return filled;
		}

		// The union of every page that cribs clean (>= EnglishMatchPercent over >= MinOverlap shared ids) against the
		// catalog's existing names - i.e. the English page(s), which partition the id space. Empty if none cribs clean.
		internal static Dictionary<uint, string> EnglishNames(List<Dictionary<uint, string>> pages, Dictionary<uint, string> known)
		{
			var english = new Dictionary<uint, string>();
			foreach (var page in pages)
			{
				int overlap = 0, match = 0;
				foreach (var (id, str) in page)
				{
					if (known.TryGetValue(id, out var name))
					{
						overlap++;
						if (Norm(str) == name)
							match++;
					}
				}

				if (overlap < MinOverlap || match * 100 / overlap < EnglishMatchPercent)
					continue;

				foreach (var (id, str) in page)
					english[id] = str;
			}

			return english;
		}

		private static bool IsEmptyName(object row)
		{
			var name = row switch
			{
				Spell s => s.Name,
				Item i  => i.Name,
				_       => null
			};
			return string.IsNullOrEmpty(name);
		}

		// Match key: trim, case-fold, and normalise the curly apostrophes the LDB uses (’/‘) to a straight one so an
		// LDB "Set's Ruin" cribs against a catalog "Set's Ruin".
		internal static string Norm(string s)
		{
			return s.Trim().ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
		}

		public readonly record struct Result(int SpellEnglish, int SpellsFilled, int ItemEnglish, int ItemsFilled);
	}
}
