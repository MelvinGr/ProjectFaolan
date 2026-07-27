using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;
using Xunit;

namespace Faolan.Tests
{
	// A real captured MapChange (0x2000 / 0x5D85BFC7, King Conan's Castle = map 2040) must be parsed and the map id
	// inserted (a Map carries no position - the coordinate space - so id + name + the per-map rule words are stored);
	// an already-present map keeps its curated name but has those words refreshed from the capture.
	//
	// This fixture is a good one for #451 by luck: 2040 is one of the six maps whose FieldA/FieldC differ from the
	// values the builder used to hardcode - it carries A=2 (not 0) and C=0 (not 7) - so an import that dropped either
	// would show up here rather than only in the shipped database.
	public class CaptureMapImporterTests
	{
		// The 146-byte MapChange packet from NecroL80_mage-necromancer_login_kingconanscastle (map 2040, PlayfieldFlags 0).
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
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					// A map NOT in the capture is left entirely alone (not in `found`).
					db.Maps.Add(new Map
					{
						Id = 4010,
						Name = "Tortage Beach"
					});
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					var (inserted, reflagged) = await CaptureMapImporter.ImportAsync(db, [packet]);
					Assert.Equal(1, inserted);
					Assert.Equal(0, reflagged);
				}

				await using (var db = NewContext(dbPath))
				{
					var map = await db.Maps.FindAsync([2040u], TestContext.Current.CancellationToken);
					Assert.NotNull(map);
					Assert.Equal(string.Empty, map!.Name); // capture-discovered, no name
					Assert.Equal(0u, map.PlayfieldFlags); // the captured per-area rule word
					Assert.Equal(2u, map.PlayfieldFieldA); // #451: 2 on this map, where the builder hardcoded 0
					Assert.Equal(0u, map.PlayfieldFieldC); // #451: 0 here, where the builder hardcoded 7

					// A map absent from the capture was not overwritten.
					var beach = await db.Maps.FindAsync([4010u], TestContext.Current.CancellationToken);
					Assert.Equal("Tortage Beach", beach!.Name);
				}
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		[Fact]
		public async Task Import_RefreshesTheRuleWords_OnExistingMap_KeepingName()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-map-{Guid.NewGuid():N}.db");
			try
			{
				var packet = Convert.FromHexString(MapChangeHex); // map 2040, captured flags = 0

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					// Curated row with a STALE/wrong flag word - the mount-rule bug this import fixes (#340) - and the
					// two rule words at the values the builder used to hardcode for every map (#451).
					db.Maps.Add(new Map
					{
						Id = 2040,
						Name = "Castle of King Conan",
						PlayfieldFlags = 7,
						PlayfieldFieldA = 0,
						PlayfieldFieldC = 7
					});
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					var (inserted, reflagged) = await CaptureMapImporter.ImportAsync(db, [packet]);
					Assert.Equal(0, inserted); // already present
					Assert.Equal(1, reflagged); // rule words corrected from capture-truth
				}

				await using (var db = NewContext(dbPath))
				{
					var map = await db.Maps.FindAsync([2040u], TestContext.Current.CancellationToken);
					Assert.Equal(0u, map!.PlayfieldFlags); // refreshed to the captured value
					Assert.Equal(2u, map.PlayfieldFieldA);
					Assert.Equal(0u, map.PlayfieldFieldC);
					Assert.Equal("Castle of King Conan", map.Name); // name curation preserved
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
			var options = TestDb.Options(path);
			return new DatabaseContext(options);
		}
	}
}
