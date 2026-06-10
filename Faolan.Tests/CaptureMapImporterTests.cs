using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Importer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// A real captured MapChange (0x2000 / 0x5D85BFC7, King Conan's Castle = map 2040) must be parsed and inserted
	// with its spawn position; an already-present map must be left untouched.
	public class CaptureMapImporterTests
	{
		// The 146-byte MapChange packet from account-faolan-character-necrol80-login-kingconanscastle (map 2040).
		private const string MapChangeHex =
			"0000008e426be154000000170a070d13ce71b11056120a0d47c1676c10d4cb8b4020000000006b5d85bfc70000000000000000004379d4bc41ffc29042d51810620000c79c000007f8000000026c3f3611000000000000000100009c500050357800000000010000001800000001005035780000cb204170000040df091200000003ffffffffffffffff0000000000000000";

		[Fact]
		public async Task Import_InsertsNewMap_AndLeavesExistingUntouched()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-map-{Guid.NewGuid():N}.db");
			try
			{
				var packet = Convert.FromHexString(MapChangeHex);

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					// Pre-existing curated map 2040 would be left alone; here it's absent so it gets inserted.
					db.Maps.Add(new Map
					{
						Id = 4010,
						Name = "Tortage Beach",
						PositionX = 1,
						PositionY = 2,
						PositionZ = 3
					});
					await db.SaveChangesAsync();

					var inserted = await CaptureMapImporter.ImportAsync(db, [packet]);
					Assert.Equal(1, inserted);
				}

				await using (var db = NewContext(dbPath))
				{
					var map = await db.Maps.FindAsync(2040u);
					Assert.NotNull(map);
					Assert.Equal(string.Empty, map!.Name); // capture-discovered, no name
					Assert.Equal(249.83f, map.PositionX, 0.5f);
					Assert.Equal(31.97f, map.PositionY, 0.5f);
					Assert.Equal(106.55f, map.PositionZ, 0.5f);

					// The pre-existing map was not overwritten.
					var beach = await db.Maps.FindAsync(4010u);
					Assert.Equal("Tortage Beach", beach!.Name);
					Assert.Equal(1f, beach.PositionX);
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
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}", x => x.MigrationsAssembly("Faolan"))
				.Options;
			return new DatabaseContext(options);
		}
	}
}
