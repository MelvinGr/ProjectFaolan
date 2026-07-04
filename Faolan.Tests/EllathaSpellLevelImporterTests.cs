using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Tsv;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The ellatha level backfill sets Spell.Level on exact "<name> (Rank <rank>)" matches only (level isn't in the
	// RDB record). Base-name-only rows are ambiguous and skipped; every DB row sharing a matched name is updated.
	public class EllathaSpellLevelImporterTests
	{
		[Fact]
		public async Task Backfill_SetsLevel_OnExactRankMatchesOnly()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-ella-{Guid.NewGuid():N}.db");
			var tsv = Path.Combine(Path.GetTempPath(), $"ella-{Guid.NewGuid():N}.tsv");
			try
			{
				File.WriteAllText(tsv,
					string.Join("\n",
						"# name\trank\tlevel\tmana\tcast",
						"Smite\t1\t1\t35\t",
						"Smite\t2\t20\t79\t",
						"Chill\t5\t15\t33\t1s", // exact match exists
						"Mystery Spell\t1\t40\t\t")); // no DB row -> ignored

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					// Two id-range rows share the name "Smite (Rank 1)" (both must get the level).
					db.Spells.Add(new Spell
					{
						Id = 1,
						Name = "Smite (Rank 1)",
						Level = 0
					});
					db.Spells.Add(new Spell
					{
						Id = 2,
						Name = "Smite (Rank 1)",
						Level = 0
					});
					db.Spells.Add(new Spell
					{
						Id = 3,
						Name = "Smite (Rank 2)",
						Level = 0
					});
					db.Spells.Add(new Spell
					{
						Id = 4,
						Name = "Chill (Rank 5)",
						Level = 0
					});
					db.Spells.Add(new Spell
					{
						Id = 5,
						Name = "Chill",
						Level = 0
					}); // base-name only -> NOT matched
					await db.SaveChangesAsync();

					var (rows, matched, updated) = await EllathaSpellLevelImporter.ImportAsync(db, tsv);
					Assert.Equal(4, rows); // 4 data lines (comment skipped)
					Assert.Equal(4, matched); // Smite R1 x2 + Smite R2 + Chill R5
					Assert.Equal(4, updated);
				}

				await using (var db = NewContext(dbPath))
				{
					Assert.Equal(1, db.Spells.Single(s => s.Id == 1).Level);
					Assert.Equal(1, db.Spells.Single(s => s.Id == 2).Level); // both id-range rows set
					Assert.Equal(20, db.Spells.Single(s => s.Id == 3).Level);
					Assert.Equal(15, db.Spells.Single(s => s.Id == 4).Level);
					Assert.Equal(0, db.Spells.Single(s => s.Id == 5).Level); // base-name row untouched
				}

				// Idempotent: a second run sets nothing new.
				await using (var db = NewContext(dbPath))
				{
					var (_, _, updated) = await EllathaSpellLevelImporter.ImportAsync(db, tsv);
					Assert.Equal(0, updated);
				}
			}
			finally
			{
				File.Delete(dbPath);
				File.Delete(tsv);
			}
		}

		[Fact]
		public async Task ComboBackfill_SetsLevelAndCooldown_ByExactName()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-combo-{Guid.NewGuid():N}.db");
			var tsv = Path.Combine(Path.GetTempPath(), $"combo-{Guid.NewGuid():N}.tsv");
			try
			{
				// combos list cols: name, level, cooldown, stamina, hash (same combo repeats per class)
				File.WriteAllText(tsv, string.Join("\n", "# name\tlevel\tcooldown\tstamina\tarchetype", "Sweep\t2\t10\t20\tGuardian", "Sweep\t2\t10\t20\tConqueror", "Slam\t4\t15\t22\tGuardian"));

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					db.Spells.Add(new Spell
					{
						Id = 1,
						Name = "Sweep",
						Level = 0,
						Cooldown = 0
					}); // two id-variants
					db.Spells.Add(new Spell
					{
						Id = 2,
						Name = "Sweep",
						Level = 0,
						Cooldown = 0
					});
					db.Spells.Add(new Spell
					{
						Id = 3,
						Name = "Slam",
						Level = 0,
						Cooldown = 0
					});
					db.Spells.Add(new Spell
					{
						Id = 4,
						Name = "Fireball",
						Level = 0,
						Cooldown = 0
					}); // untouched
					await db.SaveChangesAsync();

					var (combos, matched, updated) = await EllathaSpellLevelImporter.ImportCombosAsync(db, tsv);
					Assert.Equal(2, combos); // Sweep, Slam (deduped by name)
					Assert.Equal(3, matched); // 2x Sweep + 1 Slam
					Assert.Equal(3, updated);
				}

				await using (var db = NewContext(dbPath))
				{
					Assert.Equal((2, 10), (db.Spells.Single(s => s.Id == 1).Level, db.Spells.Single(s => s.Id == 1).Cooldown));
					Assert.Equal((2, 10), (db.Spells.Single(s => s.Id == 2).Level, db.Spells.Single(s => s.Id == 2).Cooldown));
					Assert.Equal((4, 15), (db.Spells.Single(s => s.Id == 3).Level, db.Spells.Single(s => s.Id == 3).Cooldown));
					Assert.Equal((0, 0), (db.Spells.Single(s => s.Id == 4).Level, db.Spells.Single(s => s.Id == 4).Cooldown));
				}
			}
			finally
			{
				File.Delete(dbPath);
				File.Delete(tsv);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}", x => x.MigrationsAssembly("Faolan")).Options;
			return new DatabaseContext(options);
		}
	}
}
