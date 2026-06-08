using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Importer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// A real captured ApplySpell (0x2000 / 0x6B87E316) must yield the cast spell id (0x3dfa48 = 4061768) and
	// catalog it in Spells if absent.
	public class CaptureSpellImporterTests
	{
		// ApplySpell packet from account-faolan-character-necrol80-login-kingconanscastle (spell id 0x3dfa48).
		private const string ApplySpellHex =
			"000000731bd646d2000000170a070d13ce71b11056120a0d47c1676c10d4cb8b402000000000506b87e3160000c3500802e5d400003dfa48000000c3500802e5d400000000000000000000000100010000000100000000003dfa480000005000000000000000000000000000000050000000001534b945";

		[Fact]
		public async Task Import_CatalogsCastSpellId()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-spell-{Guid.NewGuid():N}.db");
			try
			{
				var packet = Convert.FromHexString(ApplySpellHex);

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					var (seen, inserted) = await CaptureSpellImporter.ImportAsync(db, [packet]);
					Assert.Equal(1, seen);
					Assert.Equal(1, inserted);
				}

				await using (var db = NewContext(dbPath))
				{
					var spell = await db.Spells.FindAsync(4061768u); // 0x3dfa48
					Assert.NotNull(spell);
				}
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}", x => x.MigrationsAssembly("Faolan")).Options;
			return new DatabaseContext(options);
		}
	}
}
