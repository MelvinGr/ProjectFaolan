using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Importer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The archive SQL importer parses p_abilities (MySQL dump rows, quoted + unquoted + NULL) and upserts Abilities.
	public class ArchiveSqlImporterTests
	{
		private const string Sql = """
		                           INSERT INTO `p_abilities` VALUES('Normal Attack Left', '769094', '1', '1263352118', '3236668225', '2153842419', '314062142', '3015939650');
		                           INSERT INTO `p_abilities` VALUES('Normal Attack Front', '769099', '1', '1227967307', '3443694845', '1660108369', '1296666148', '2957995211');
		                           """;

		[Fact]
		public async Task Import_UpsertsAbilities()
		{
			var dir = Path.Combine(Path.GetTempPath(), $"faolan-arch-{Guid.NewGuid():N}");
			Directory.CreateDirectory(dir);
			var dbPath = Path.Combine(dir, "faolan.db");
			try
			{
				await File.WriteAllTextAsync(Path.Combine(dir, "aocdatabase.sql"), Sql);

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					await ArchiveSqlImporter.ImportAsync(db, dir);
				}

				await using (var db = NewContext(dbPath))
				{
					Assert.Equal(2, await db.Abilities.CountAsync());
					var ability = await db.Abilities.FindAsync(769094u);
					Assert.NotNull(ability);
					Assert.Equal("Normal Attack Left", ability!.Name);
					Assert.Equal(1263352118u, ability.Data0);
					Assert.Equal(3015939650u, ability.Data4);
				}

				// Idempotent: re-running upserts the same rows, no duplicates.
				await using (var db = NewContext(dbPath))
				{
					await ArchiveSqlImporter.ImportAsync(db, dir);
					Assert.Equal(2, await db.Abilities.CountAsync());
				}
			}
			finally
			{
				if (Directory.Exists(dir))
					Directory.Delete(dir, true);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}", x => x.MigrationsAssembly("Faolan"))
				.Options;
			return new DatabaseContext(options);
		}
	}
}
