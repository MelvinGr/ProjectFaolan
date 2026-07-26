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
	//   50201 = item descriptions (id == item id, #414).
	//   50211 = SPELL descriptions (id == spell id, #367) - the client's own tooltip text for a spell or buff. The one
	//           source that says what an effect DOES in words, so it is what an effect implementation is written and
	//           checked against; ~3.6k of the 20k spells carry one.
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

			// One pass over the LDB: bucket the categories we care about as id->string, per page.
			var spellPages = new List<Dictionary<uint, string>>();
			var itemPages = new List<Dictionary<uint, string>>();
			var descriptionPages = new List<Dictionary<uint, string>>();
			var spellDescriptionPages = new List<Dictionary<uint, string>>();
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
				var bucket = cat switch
				{
					LdbStringIndex.SpellNameCategory        => spellPages,
					LdbStringIndex.ItemNameCategory         => itemPages,
					LdbStringIndex.ItemDescriptionCategory  => descriptionPages,
					LdbStringIndex.SpellDescriptionCategory => spellDescriptionPages,
					_                                       => null
				};
				if (bucket == null)
					continue;

				var ids = new Dictionary<uint, string>();
				foreach (var entry in LdbStringIndex.Parse(body))
					ids[entry.Id] = entry.Value;
				if (ids.Count == 0)
					continue;

				bucket.Add(ids);
			}

			var spellEnglish = EnglishNames(spellPages, spellNames);
			var itemEnglish = EnglishNames(itemPages, itemNames);

			var spellsFilled = await BackfillAsync(db.Spells, s => s.Id, (s, n) => s.Name = n, spellEnglish);
			var itemsFilled = await BackfillAsync(db.Items, i => i.Id, (i, n) => i.Name = n, itemEnglish);

			// Descriptions (#414 items, #367 spells) cannot be language-picked the same way: EnglishNames cribs each page
			// against names we already hold, and we hold no descriptions to crib against. They ARE full sentences though
			// ("Deals piercing damage over time."), so the language is detectable directly - take every page whose prose
			// reads as English.
			var descriptionEnglish = EnglishProse(descriptionPages);
			var descriptionsFilled = await BackfillDescriptionsAsync(await db.Items.ToListAsync(), i => i.Id, i => i.Description, (i, d) => i.Description = d, descriptionEnglish);

			var spellDescriptionEnglish = EnglishProse(spellDescriptionPages);
			var spellDescriptionsFilled =
				await BackfillDescriptionsAsync(await db.Spells.ToListAsync(), s => s.Id, s => s.Description, (s, d) => s.Description = d, spellDescriptionEnglish);

			await db.SaveChangesAsync();
			logger?.LogInformation(
				"RdbLdbNameImporter: spell English={SpellEng} (filled {SpellFill}); item English={ItemEng} (filled {ItemFill}); item descriptions English={DescEng} (filled {DescFill}); spell descriptions English={SpellDescEng} (filled {SpellDescFill}).",
				spellEnglish.Count, spellsFilled, itemEnglish.Count, itemsFilled, descriptionEnglish.Count, descriptionsFilled, spellDescriptionEnglish.Count, spellDescriptionsFilled);
			return new Result(spellEnglish.Count, spellsFilled, itemEnglish.Count, itemsFilled, descriptionEnglish.Count, descriptionsFilled, spellDescriptionEnglish.Count,
				spellDescriptionsFilled);
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

		// Common English function/domain words. A description page in another language scores near zero on these, and
		// the winning page in a 7-language install wins by a wide margin - this is not a close call.
		// A genuinely English page of sentences clears this comfortably; a wrong-language page is nowhere near it.
		private const double EnglishProseRatio = 0.30;

		private static readonly string[] EnglishMarkers = ["the ", " and ", " of ", " to ", " a ", " your ", "damage", "increase", "target", "seconds"];

		// Merges EVERY description page that reads as English, not just the best one - a category is split across
		// several pages per language, so picking a single winner returns only a fraction of the ids (226 of the ~420
		// items we hold, when the whole English set covers far more). This mirrors EnglishNames, which also unions all
		// pages that pass its test rather than choosing one.
		//
		// A page that fails the threshold contributes NOTHING, so a mis-detection stores no description rather than
		// storing the wrong language.
		internal static Dictionary<uint, string> EnglishProse(List<Dictionary<uint, string>> pages)
		{
			var english = new Dictionary<uint, string>();
			foreach (var page in pages)
			{
				var sampled = page.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(400).ToList();
				if (sampled.Count == 0)
					continue;

				var hits = sampled.Count(v => EnglishMarkers.Any(m => v.Contains(m, StringComparison.OrdinalIgnoreCase)));
				if ((double)hits / sampled.Count < EnglishProseRatio)
					continue;

				foreach (var (id, str) in page)
					english[id] = str;
			}

			return english;
		}

		// Fills Description on the rows we hold, from the English page set. Only ever ADDS - never overwrites a
		// description already stored, so a re-run is idempotent and a hand-edit survives. Shared by items and spells:
		// both carry a 512-char Description keyed by their own id, from their own LDB category.
		private static Task<int> BackfillDescriptionsAsync<T>(List<T> rows, Func<T, uint> idOf, Func<T, string?> descriptionOf, Action<T, string> setDescription,
			Dictionary<uint, string> english)
		{
			if (english.Count == 0)
				return Task.FromResult(0);

			var filled = 0;
			foreach (var row in rows)
			{
				if (!string.IsNullOrEmpty(descriptionOf(row)) || !english.TryGetValue(idOf(row), out var text) || string.IsNullOrWhiteSpace(text))
					continue;

				setDescription(row, text.Length > 512
					? text[..512]
					: text);
				filled++;
			}

			return Task.FromResult(filled);
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

		public readonly record struct Result(int SpellEnglish, int SpellsFilled, int ItemEnglish, int ItemsFilled, int DescriptionEnglish, int DescriptionsFilled, int SpellDescriptionEnglish,
			int SpellDescriptionsFilled);
	}
}
