using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report ldb-name-categories`: which LDB name category names each of our catalogs, measured over every row we
	// hold rather than over a handful of samples.
	//
	// The question it answers: RDB type 1000020 holds items, feats and abilities in one id space, and the encrypted
	// RDBH header carries no category tag, so an entry's kind looks knowable only by decrypting it - which needs a
	// key harvested from a capture. That would block ever creating a row for an un-keyed template, since a template
	// cannot be filed as an item without knowing it is one.
	//
	// The LDB knows. Each catalog's rows land in a different name category, so the category is an outside witness to
	// the kind, available for every entry and needing no key. This report is that claim's evidence.
	public static partial class ReportCli
	{
		// The client's own name/description categories over the two template types, from `docs/rdb/ldb-string-index.md`.
		private static readonly (uint Category, string What)[] NameCategories =
		[
			(50200, "1000020 name"),
			(50201, "1000020 description"),
			(50220, "1000020 name (the other half)"),
			(50210, "1040005 spell name"),
			(50211, "1040005 spell description")
		];

		private static async Task LdbNameCategories(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);
			await using var db = OpenDb();

			var ldb = LdbStringIndex.EnglishPlaintext(rdb);
			var byCategory = new Dictionary<uint, HashSet<uint>>();
			foreach (var ((category, id), _) in ldb)
			{
				if (!byCategory.TryGetValue(category, out var ids))
					byCategory[category] = ids = [];

				ids.Add(id);
			}

			var catalogs = new (string Name, HashSet<uint> Ids)[]
			{
				("Items", (await db.Items.AsNoTracking().Select(i => i.Id).ToListAsync()).ToHashSet()),
				("Feats", (await db.Feats.AsNoTracking().Select(f => f.Id).ToListAsync()).ToHashSet()),
				("Abilities", (await db.Abilities.AsNoTracking().Select(a => a.Id).ToListAsync()).ToHashSet()),
				("Spells", (await db.Spells.AsNoTracking().Select(s => s.Id).ToListAsync()).ToHashSet())
			};

			Console.WriteLine("LDB name categories against every row of each catalog (#415)");
			Console.WriteLine($"  {"cat",-6} {"what",-28} {"ids",8}   " + string.Join("  ", catalogs.Select(c => $"{c.Name,-18}")));
			foreach (var (category, what) in NameCategories)
			{
				var ids = byCategory.GetValueOrDefault(category, []);
				var cells = catalogs.Select(c =>
				{
					var hit = c.Ids.Count(i => ids.Contains(i));
					return $"{hit,6} / {c.Ids.Count,-9}";
				});
				Console.WriteLine($"  {category,-6} {what,-28} {ids.Count,8}   " + string.Join("  ", cells));
			}

			// The half the report is really about. `docs/rdb/ldb-string-index.md` read 50200 and 50220 as "two
			// halves of one id space" of item names, on the strength of their ids being disjoint and both keying
			// 1000020. They key it because feats live in 1000020 too - the split is by kind, and the rows above say
			// which is which.
			var names200 = byCategory.GetValueOrDefault(50200u, []);
			var names220 = byCategory.GetValueOrDefault(50220u, []);
			var overlap = names200.Intersect(names220).Count();
			Console.WriteLine();
			Console.WriteLine($"  50200 and 50220 share {overlap} id(s) - they partition 1000020 rather than duplicating it.");

			// The coverage the ticket asks about, and the denominator it has to be read against: how much of the
			// 1000020 catalog either category names at all.
			var entries = rdb.EntriesOfType(TemplateType).Select(e => e.Id).ToHashSet();
			var in200 = entries.Count(names200.Contains);
			var in220 = entries.Count(names220.Contains);
			Console.WriteLine($"  of the {entries.Count:N0} type-{TemplateType} entries: {in200:N0} named by 50200, {in220:N0} by 50220, "
			                  + $"{entries.Count - in200 - in220 + overlap:N0} by neither.");

			// The independent cross-check, from the bytes rather than from our catalogs: an RDBH entry is
			// TEA-encrypted and needs a per-template key, a non-RDBH one is readable with none. If the name category
			// really is the kind, the two splits have to line up - and nothing about the LDB pages knows anything
			// about the RDB's encryption, so agreement is not circular.
			int e200 = 0, p200 = 0, e220 = 0, p220 = 0, eNone = 0, pNone = 0;
			foreach (var entry in rdb.EntriesOfType(TemplateType).Where(rdb.CanRead))
			{
				bool encrypted;
				try
				{
					encrypted = rdb.IsEncrypted(entry);
				}
				catch
				{
					continue;
				}

				if (names200.Contains(entry.Id))
					_ = encrypted ? e200++ : p200++;
				else if (names220.Contains(entry.Id))
					_ = encrypted ? e220++ : p220++;
				else
					_ = encrypted ? eNone++ : pNone++;
			}

			// The client picks an alternate-advancement tree as the first feat tree whose id is above 999,999
			// (`FUN_01506a60`, log tag `GUI/AAFeatPlanner`), so the reserved range is something the LDB can be asked
			// about directly: if AA content ships in this build, its names sit above that line in some category.
			Console.WriteLine();
			Console.WriteLine($"  ⭐ ids at or above {AaReservedIdFloor:N0} - the AA/perk reserved range (#150):");
			var anyAbove = false;
			foreach (var (category, ids) in byCategory.OrderBy(c => c.Key))
			{
				var above = ids.Where(i => i >= AaReservedIdFloor).ToList();
				if (above.Count == 0)
					continue;

				anyAbove = true;
				var sample = above.OrderBy(i => i).Take(4).Select(i => $"{i}=\"{Trim(ldb[(category, i)], 34)}\"");
				Console.WriteLine($"     cat {category,-8} {above.Count,7:N0} id(s)   {string.Join("  ", sample)}");
			}

			if (!anyAbove)
				Console.WriteLine("     none in any category");

			Console.WriteLine();
			Console.WriteLine("  ⭐ and against the ENCRYPTION, which knows nothing about the LDB:");
			Console.WriteLine($"     named by 50200 : {e200,7:N0} encrypted   {p200,7:N0} plaintext");
			Console.WriteLine($"     named by 50220 : {e220,7:N0} encrypted   {p220,7:N0} plaintext");
			Console.WriteLine($"     named by neither:{eNone,7:N0} encrypted   {pNone,7:N0} plaintext");
		}

		// RDB 1000020 - the one type that holds item, feat and ability templates, which is why the kind has to come
		// from outside the record.
		private const uint TemplateType = 1000020;

		// The client's own AA cut-off: `FUN_01506a60` picks a class's alternate-advancement tree as the first feat
		// tree whose id is greater than 999,999.
		private const uint AaReservedIdFloor = 1_000_000;
	}
}
