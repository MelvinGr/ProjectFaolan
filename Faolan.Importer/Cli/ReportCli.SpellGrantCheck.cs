using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-grant-check`: does the level path offer a captured character the spellbook retail gave them?
	//
	// The oracle is `CharacterSpells` - the known-spell lists harvested from the captures, of which two are level-80
	// (a Necromancer's 109 and a Demonologist's 127). For each of them this runs the SHIPPING queries
	// (`GetStartingSpells`, the relog re-grant, and `GetStartingBarSpells`) and prints the two errors separately,
	// because only one of them can only be a defect:
	//
	//   MISSING - a spell the character demonstrably knows that the path would not restore. Some of these are
	//             expected and are counted apart: a spell that is not in `ClassSpells` for the class at all, or
	//             carries no level, was never on offer and is a CATALOG gap rather than a gate failure.
	//   EXTRA   - a spell the path offers that the character does not know. Retail is the leading source, so an extra
	//             is wrong; but the population moves whenever captures or the catalog grow, so it is PRINTED.
	//
	// ⭐ **Why a report and not a test** (Gitea #729): both sides are shipped DATA - importer OUTPUT (`ClassSpells` +
	// `Spells.Level` + `FeatSpellGrants`) against importer INPUT (the captured known-spell lists). It also physically
	// cannot be a test: `faolan.sql` excludes every `Character*` table as per-developer play state, so the suite's
	// fixture holds ZERO `CharacterSpells` rows and any such gate there passes vacuously.
	//
	// What it exists to hold is the claim `DatabaseRepository.FeatMapCovers` was introduced on - that gating the
	// level path on "a feat teaches it" costs no recall - so the assertion is: **no spell a captured character knows,
	// and that the class catalog does offer, may be dropped**. That is the direction that can only be a defect.
	public static partial class ReportCli
	{
		private static async Task<int> SpellGrantCheck()
		{
			await using var db = OpenDb();
			var repo = new DatabaseRepository(new ReportContextFactory());

			var characters = await db.Characters.AsNoTracking()
				.Select(c => new
				{
					c.Id,
					c.Name,
					c.Class,
					c.Level
				})
				.OrderByDescending(c => c.Level).ToListAsync();

			var comboActions = (await db.Combos.AsNoTracking().Select(c => c.GameActionId).ToListAsync()).ToHashSet();
			var levels = await db.Spells.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Level);
			var names = await db.Spells.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);

			var failures = 0;
			foreach (var character in characters)
			{
				var known = (await db.CharacterSpells.AsNoTracking().Where(cs => cs.CharacterId == character.Id).Select(cs => cs.SpellId).ToListAsync()).ToHashSet();
				if (known.Count == 0)
					continue;

				// The class's own catalog rows, so a MISSING spell can be attributed: absent from the catalog is a
				// harvest gap, present in it but not offered is a gate failure.
				var inCatalog = (await db.ClassSpells.AsNoTracking()
					.Where(cs => cs.Class == 0 || cs.Class == (ConanClass)character.Class)
					.Select(cs => cs.SpellId).ToListAsync()).ToHashSet();

				var offered = (await repo.GetStartingSpells(Math.Max(character.Level, (ushort)1), (ConanClass)character.Class)).ToHashSet();
				var bar = await repo.GetStartingBarSpells(character.Level, (ConanClass)character.Class);

				var missing = known.Where(id => !offered.Contains(id)).ToList();
				var offCatalog = missing.Where(id => !inCatalog.Contains(id) || levels.GetValueOrDefault(id) == 0 || comboActions.Contains(id)).ToList();
				var dropped = missing.Except(offCatalog).ToList();
				var extra = offered.Where(id => !known.Contains(id)).ToList();

				Console.WriteLine($"{character.Name} (class {(ConanClass)character.Class}, level {character.Level}): knows {known.Count}, path offers {offered.Count}.");
				Console.WriteLine($"  matched {known.Count - missing.Count}   EXTRA {extra.Count}   missing {missing.Count} = {offCatalog.Count} never on offer (catalog gap) + {dropped.Count} DROPPED");

				var offBar = bar.Where(id => !offered.Contains(id)).ToList();
				if (offBar.Count > 0)
				{
					Console.WriteLine($"  ⚠️ the default bar seeds {offBar.Count} spell(s) the book does not hold: {Describe(offBar, names)}");
					failures++;
				}

				if (dropped.Count > 0)
				{
					Console.WriteLine($"  ⚠️ DROPPED - in the class catalog, carries a level, and still not offered: {Describe(dropped, names)}");
					failures++;
				}
			}

			if (characters.Count == 0)
				Console.WriteLine("no characters in this database - the check is vacuous, so this is a failure, not a pass.");

			if (failures == 0)
				Console.WriteLine("✅ no captured character loses a spell its own class catalog offers, and no bar slot points outside the book.");

			return failures == 0 && characters.Count > 0 ? 0 : 1;
		}

		private static string Describe(IEnumerable<uint> ids, Dictionary<uint, string> names)
		{
			return string.Join(", ", ids.Take(15).Select(id => $"{names.GetValueOrDefault(id, "?")} ({id})"));
		}

		// The repository takes a context FACTORY and disposes what it hands back per unit of work, so this must mint a
		// FRESH context each time - handing out one shared instance would leave it disposed after the first query.
		private sealed class ReportContextFactory : IDbContextFactory<Faolan.Core.Database.DatabaseContext>
		{
			public Faolan.Core.Database.DatabaseContext CreateDbContext()
			{
				return OpenDb();
			}

			public Task<Faolan.Core.Database.DatabaseContext> CreateDbContextAsync(System.Threading.CancellationToken ct = default)
			{
				return Task.FromResult(OpenDb());
			}
		}
	}
}
