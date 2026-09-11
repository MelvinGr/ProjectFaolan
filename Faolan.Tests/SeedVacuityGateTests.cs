using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// `TestDb.GameDatabase` is built from `faolan.sql`, and the seed deliberately excludes every player-
	// and deployment-state table - `Characters` and its whole FK closure, `Accounts`, `Realms`, `Guilds`, `Mails`,
	// `ChatMessages`, `TradepostListings`, `AccountClaims`. So a gate that reads one of those out of the shared
	// fixture is asserting over an empty set, and the usual shape of such a gate ("this intersection is empty", "no
	// row does X") passes for exactly the wrong reason.
	//
	// A vacuity guard does not save such a gate: `Assert.NotEmpty(...)` is usually written over the right-hand
	// population, which is never the empty one. That is how quietly it fails - the oracle is real (249 known-spell
	// entries across four captured characters, two of them level-80), it just does not survive into the fixture, and
	// nothing in the test says so.
	//
	// A gate that cannot fail is worse than no gate, because it reads as coverage. This is the gate on the gates:
	// naming an excluded table in a file that also opens the shared fixture has to be a deliberate, stated choice.
	//
	// The right home for such a comparison is a `--report`: both sides are shipped data, importer input against
	// importer output. `--report spell-grant-check` runs against the real `faolan.db` and fails on a vacuous run
	// rather than passing.
	public class SeedVacuityGateTests
	{
		// Files that name an excluded table and open the shared fixture, legitimately. Every one of these builds its
		// own player rows in a scratch database - the fixture is opened for game content (items, spells, templates)
		// and the character is inserted by the test itself, so nothing is being asserted over the empty seed.
		private static readonly Dictionary<string, string> Allowed = new()
		{
			["ItemTransferTests.cs"] = "seeds its own Account/Realm/Character scaffold, then moves items between them",
			["CharacterEquipmentStatSlotsTests.cs"] = "seeds its own character and equips it",
			["EquippedStatItemsDuplicateTests.cs"] = "seeds its own character and equips it",
			["SocketedGemStatsTests.cs"] = "seeds its own character and sockets a gem into its item",
			["TradeskillProfessionTests.cs"] = "seeds its own character and grants it a profession",
			["TestDb.cs"] = "the fixture builder itself - it is what states the exclusion"
		};

		// A DbSet whose entity maps to a table the seed drops. Derived from the model via the seed writer's own
		// exclusion closure, so this cannot drift from what `faolan.sql` actually carries the way a hardcoded list
		// would - adding a table that hangs off `Characters` puts it in scope automatically.
		private static async Task<HashSet<string>> ExcludedDbSetNames()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var seeded = SqlSeedWriter.ContentTables(db).Select(g => g.Key).ToHashSet();

			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var property in typeof(DatabaseContext).GetProperties())
			{
				if (!property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(DbSet<>))
					continue;

				var table = db.Model.FindEntityType(property.PropertyType.GetGenericArguments()[0])?.GetTableName();
				if (table != null && !seeded.Contains(table) && table != "__EFMigrationsHistory")
					names.Add(property.Name);
			}

			return names;
		}

		[Fact]
		public async Task NoTest_AssertsOverATableTheSeedExcludes_WithoutSayingWhy()
		{
			var excluded = await ExcludedDbSetNames();
			Assert.NotEmpty(excluded); // the exclusion set is real - otherwise this test is the vacuous one

			var offenders = new List<string>();
			foreach (var file in Directory.EnumerateFiles(TestPaths.Repo("Faolan.Tests"), "*.cs", SearchOption.AllDirectories))
			{
				var name = Path.GetFileName(file);
				if (name == nameof(SeedVacuityGateTests) + ".cs" || Allowed.ContainsKey(name))
					continue;

				if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
					continue;

				var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
				if (!text.Contains("TestDb.GameDatabase", StringComparison.Ordinal))
					continue;

				// Line comments are stripped first: the comment left behind when one of these gates moves out names the
				// table it read before moving (that is the point of it), and flagging the explanation as the offence
				// would make the only correct fix look like the violation.
				var code = Regex.Replace(text, @"//[^\n]*", "");
				var named = excluded.Where(e => Regex.IsMatch(code, $@"\.{Regex.Escape(e)}\b")).ToList();
				if (named.Count > 0)
					offenders.Add($"{name} ({string.Join(", ", named)})");
			}

			Assert.True(offenders.Count == 0,
				"these files open the shared seed fixture AND read a table the seed excludes, so any assertion over those rows "
				+ "is vacuous - the left-hand side is empty. Move the comparison to a `--report` (#729/#849), or add the file to "
				+ "`Allowed` with the reason it is building its own rows. Offenders: " + string.Join("; ", offenders));
		}

		// ...and the premise itself, pinned rather than assumed: the fixture really does hold no character rows. If a
		// future seed change started shipping them, the gate above would be over-strict and this is what says so.
		[Fact]
		public async Task TheSharedFixture_HoldsNoCharacterRows()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			Assert.Equal(0, await db.Characters.AsNoTracking().CountAsync(ct));
			Assert.Equal(0, await db.CharacterSpells.AsNoTracking().CountAsync(ct));
			Assert.Equal(0, await db.CharacterFeats.AsNoTracking().CountAsync(ct));
		}
	}
}
