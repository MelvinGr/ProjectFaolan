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
	// #303: the FULL ability catalog, from the client's own data instead of only the abilities a capture happened to
	// grant. The captured `0x2052` grants yield 27; the client knows **265**.
	//
	// **What an ability IS, in RDB terms.** An ability is an RDB type-**1000020** record - the same type as an item
	// template - so the type alone cannot tell them apart. The LDB does: an ability is named in cat **50210**, the
	// ABILITY/SPELL name category (with its description in 50211), and is *not* named in the ITEM name categories
	// (50200/50220). That classifier is validated both ways against the catalog we already have: all 27 captured
	// abilities are 1000020 + 50210, and **0** of them carry an item name, while the 12 candidates that do carry one
	// are consumables whose spell-name entry is their effect (e.g. "Purple Lotus Powder Spray").
	//
	// ⚠️ **A row from here is IDENTITY ONLY and cannot be granted.** The `0x2052` grant carries the ability's 128-bit
	// TEA key (its `Key` quad, the key to its own 1000020 template) and the keys are random - not derivable from the
	// id (checked). So an ability nobody ever saw granted has a name and nothing else: `Key`/`Data0`/`Slot` stay zero,
	// which is what `Ability.IsGrantable` reads. The grant path refuses to emit those rather than sending a `0x2052`
	// with a null key. Recovering the rest needs a capture that grants them - the same key blocker as the item
	// catalog (#415).
	//
	// Existing rows are never touched: a captured ability keeps its name, key and slot.
	public static class RdbLdbAbilityImporter
	{
		private const uint ItemNameCategory2 = 50220; // the disjoint other half of the item-name id space
		private const uint TemplateType = 1000020;

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? proxyBase = null, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			// The English cat-50210 page is picked by cribbing against the SPELL names the catalog already carries -
			// the same self-calibrating trick RdbLdbNameImporter uses, since language is per-PAGE and this client
			// bundles seven. Abilities ride that same page (they share the category).
			var spellNames = await db.Spells.Where(s => s.Name != null && s.Name != "").ToDictionaryAsync(s => s.Id, s => RdbLdbNameImporter.Norm(s.Name!));

			var namePages = new List<Dictionary<uint, string>>();
			var itemNamed = new HashSet<uint>();
			foreach (var e in rdb.EntriesOfType(RdbLdbNameImporter.LdbType).Where(rdb.CanRead))
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
				if (cat is LdbStringIndex.ItemNameCategory or ItemNameCategory2)
				{
					foreach (var entry in LdbStringIndex.Parse(body))
						itemNamed.Add(entry.Id); // ANY language: this is an exclusion set, so the id is all that matters
					continue;
				}

				if (cat != LdbStringIndex.SpellNameCategory)
					continue;

				var ids = new Dictionary<uint, string>();
				foreach (var entry in LdbStringIndex.Parse(body))
					ids[entry.Id] = entry.Value;
				if (ids.Count > 0)
					namePages.Add(ids);
			}

			var english = RdbLdbNameImporter.EnglishNames(namePages, spellNames);
			if (english.Count == 0)
			{
				logger?.LogWarning("RdbLdbAbilityImporter: no cat-50210 page cribbed as English; nothing catalogued.");
				return new Result(0, 0, 0);
			}

			var templates = rdb.EntriesOfType(TemplateType).Select(e => e.Id).ToHashSet();
			var abilities = english.Keys.Where(id => templates.Contains(id) && !itemNamed.Contains(id)).ToList();

			var existing = await db.Abilities.Select(a => a.Id).ToListAsync();
			var known = existing.ToHashSet();
			var added = 0;
			foreach (var id in abilities)
			{
				if (known.Contains(id))
					continue; // a captured ability owns its row - name, key and slot alike

				var name = english[id];
				if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
					continue;

				db.Abilities.Add(new Ability
				{
					Id = id,
					Name = name // Key/Data0/Slot stay zero: identity only, not grantable
				});
				added++;
			}

			if (added > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("RdbLdbAbilityImporter: {Total} abilities in the client (1000020 named in cat 50210, not item-named); {Known} already catalogued, {Added} added.",
				abilities.Count, known.Count, added);
			return new Result(abilities.Count, known.Count, added);
		}

		public readonly record struct Result(int Total, int AlreadyKnown, int Added);
	}
}
