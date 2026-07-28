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
	//
	// LANGUAGE is per-PAGE and this client bundles seven (EN/FR/DE/ES/PL/RU/KO). The page's language is DECLARED by the
	// client - `LdbLanguageIndex` reads the language records (RDB 1030001) that list each language's pages - so this
	// reads English pages and nothing else. The name crib (EnglishNames) is kept on top as a CHECK, not as the way the
	// language is chosen; only EMPTY names are filled, so nothing can overwrite a good value either way.
	//
	// It used to sniff the language from the text, which is wrong on this data and was caught in-client: whole pages
	// ship UNTRANSLATED (the Polish and Korean 50211 pages are English sentences), so a prose test scores them as
	// English, the pages get unioned, and the last write per id wins - which is how 1991 of 3380 spell descriptions
	// came out Spanish ("Aumenta en 1 la Fuerza"). We only support English; take the client's word for which page that
	// is.
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

			// Which page is in which language - stated by the client's own language records (LdbLanguageIndex), not
			// guessed from the text. Everything below reads ENGLISH pages only.
			var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);
			var englishPages = languageByPage.Count(kv => kv.Value == LdbLanguageIndex.English);
			if (englishPages == 0)
				logger?.LogWarning("RdbLdbNameImporter: no English LDB pages found (language records missing?) - nothing will be backfilled.");

			// One pass over the LDB: bucket the categories we care about as id->string, per page.
			var spellPages = new List<Dictionary<uint, string>>();
			var itemPages = new List<Dictionary<uint, string>>();
			var descriptionPages = new List<Dictionary<uint, string>>();
			var spellDescriptionPages = new List<Dictionary<uint, string>>();
			foreach (var e in rdb.EntriesOfType(LdbType).Where(x => x.IsLocal))
			{
				// Not English (or claimed by no language record at all) - skip it outright.
				if (!languageByPage.TryGetValue(e.Id, out var language) || language != LdbLanguageIndex.English)
					continue;

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

			// The pages are already English-only, so the crib is now a CHECK rather than the selection mechanism: it
			// verifies each page still matches the names our catalog carries (97-99% on a real English page), and a
			// page that fails contributes nothing. Belt and braces - if the language index ever mis-attributes a page,
			// names still cannot be filled from the wrong language.
			var spellEnglish = EnglishNames(spellPages, spellNames);
			var itemEnglish = EnglishNames(itemPages, itemNames);

			var spellsFilled = await BackfillAsync(db.Spells, s => s.Id, (s, n) => s.Name = n, spellEnglish);
			var itemsFilled = await BackfillAsync(db.Items, i => i.Id, (i, n) => i.Name = n, itemEnglish);

			// Descriptions (#414 items, #367 spells) cannot use the name crib - we hold no descriptions to crib against -
			// which is exactly why they need the language index: they are taken from the English pages, full stop.
			var descriptionEnglish = Merge(descriptionPages);
			var descriptionsFilled = await BackfillDescriptionsAsync(await db.Items.ToListAsync(), i => i.Id, i => i.Description, (i, d) => i.Description = d, descriptionEnglish, logger);

			var spellDescriptionEnglish = Merge(spellDescriptionPages);
			var spellDescriptionsFilled =
				await BackfillDescriptionsAsync(await db.Spells.ToListAsync(), s => s.Id, s => s.Description, (s, d) => s.Description = d, spellDescriptionEnglish, logger);

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

		// The union of the (already English-only) pages of a category. A category is split across several pages per
		// language, so a category's English strings are the union of ITS pages - there is no winner to pick.
		private static Dictionary<uint, string> Merge(List<Dictionary<uint, string>> pages)
		{
			var merged = new Dictionary<uint, string>();
			foreach (var page in pages)
			foreach (var (id, str) in page)
				merged[id] = str;

			return merged;
		}

		// Fills Description on the rows we hold, from the English page set. Only ever ADDS - never overwrites a
		// description already stored, so a re-run is idempotent and a hand-edit survives. Shared by items and spells:
		// both carry a Description keyed by their own id, from their own LDB category.
		//
		// The cap was 512 and it CLIPPED: 99 spells and one item landed on exactly that length, cut mid-word - "Tome
		// of Epithur" ended at "significantly increases the recast", "Seal of Yog" at "the number of Seals consum".
		// They are all long ability/perk tooltips, i.e. the descriptions carrying the most information. 2048 covers
		// every one of them with room, and anything still over it is now WARNED about rather than silently shortened -
		// a truncation nobody is told about is the same failure as an importer `continue`.
		private const int MaxDescription = 2048;

		private static Task<int> BackfillDescriptionsAsync<T>(List<T> rows, Func<T, uint> idOf, Func<T, string?> descriptionOf, Action<T, string> setDescription,
			Dictionary<uint, string> english, ILogger? logger = null)
		{
			if (english.Count == 0)
				return Task.FromResult(0);

			var filled = 0;
			foreach (var row in rows)
			{
				if (!string.IsNullOrEmpty(descriptionOf(row)) || !english.TryGetValue(idOf(row), out var text) || string.IsNullOrWhiteSpace(text))
					continue;

				if (text.Length > MaxDescription)
				{
					logger?.LogWarning("LDB description for id {Id} is {Length} chars, over the {Max}-char column - CLIPPED. Widen Description if this recurs.", idOf(row), text.Length, MaxDescription);
					text = text[..MaxDescription];
				}

				setDescription(row, text);
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
