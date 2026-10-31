using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Cli;
using Faolan.Importer.Modules;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The characters module's refresh guard. An overwrite rebuilds every capture-imported character, discarding the
	// player's rearrangement of its bag, so it asks for --force - but only when such a character exists: a fresh
	// clone's database, seeded from faolan.sql, has no characters, and its first import must not need the override.
	public class CharactersModuleTests
	{
		[Fact]
		public async Task Overwrite_WithoutForce_RunsOnADatabaseWithNoImportedCharacter()
		{
			await WithDatabase(async db =>
			{
				var result = await new CharactersModule().RunAsync(Context(db, false));

				Assert.StartsWith("0 captured, 0 added", result.Summary);
			});
		}

		[Fact]
		public async Task Overwrite_WithoutForce_RefusesOnceAnImportedCharacterExists()
		{
			await WithDatabase(async db =>
			{
				await AddImportedCharacter(db);

				await Assert.ThrowsAsync<InvalidOperationException>(() => new CharactersModule().RunAsync(Context(db, false)));
				var forced = await new CharactersModule().RunAsync(Context(db, true));
				Assert.StartsWith("0 captured", forced.Summary);
			});
		}

		private static ImportContext Context(DatabaseContext db, bool force) => new ImportContext
		{
			Db = db,
			CreateCaptures = [],
			Captures = [],
			RawPackets = [],
			CapturesDir = "",
			RdbProxyBase = null,
			Mode = WriteMode.Overwrite,
			Force = force
		};

		private static async Task AddImportedCharacter(DatabaseContext db)
		{
			db.Accounts.Add(new Account
			{
				Id = 7,
				UserName = "t",
				Password = "t",
				LastIpAddress = ""
			});
			var realm = new Realm
			{
				Name = "Project Faolan",
				IpAddress = "127.0.0.1"
			};
			db.Realms.Add(realm);
			db.Maps.Add(new Map
			{
				Id = 4000,
				Name = "Slave Galley"
			});
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);

			db.Characters.Add(new Character
			{
				Id = 0x02000001,
				AccountId = 7,
				RealmId = realm.Id,
				Name = "Imported",
				Level = 20,
				Class = ConanClass.Barbarian,
				Race = Race.Cimmerian,
				Sex = Sex.Male,
				MapId = 4000,
				ImportedFromCapture = true
			});
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);
		}

		private static async Task WithDatabase(Func<DatabaseContext, Task> body)
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-charmodule-{Guid.NewGuid():N}.db");
			try
			{
				await using var db = new DatabaseContext(TestDb.Options(dbPath));
				await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
				await body(db);
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}
	}
}
