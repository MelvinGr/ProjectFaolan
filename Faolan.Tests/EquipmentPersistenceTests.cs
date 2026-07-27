using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Replicates PersistEquip/PersistUnequip's exact EF operations against a temp DB (lazy-loading proxies, same as
	// the runtime context) and verifies the change is visible in a SEPARATE context - i.e. it actually hit disk.
	public class EquipmentPersistenceTests
	{
		[Fact]
		public async Task Equip_PersistsToDb_AndIsVisibleInFreshContext()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-equip-{Guid.NewGuid():N}.db");
			try
			{
				const uint characterId = 35020544;
				const uint royalSignet = 3735601; // a buildable item present in strie-login-inventory.bin
				var inventoryCapture = TestPaths.Asset("strie-login-inventory.bin");

				// Arrange: import items from a capture, seed a character + equipment (Wrist + FirstPosBag).
				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					await CaptureItemImporter.ImportAsync(db, [await File.ReadAllBytesAsync(inventoryCapture, TestContext.Current.CancellationToken)]);

					db.Accounts.Add(new Account
					{
						Id = 1,
						UserName = "t",
						Password = "t",
						AuthChallenge = "",
						LastIpAddress = ""
					});
					db.Realms.Add(new Realm
					{
						Id = 1,
						Name = "Test",
						IpAddress = "127.0.0.1"
					});
					db.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);
					db.Characters.Add(new Character
					{
						Id = characterId,
						Name = "Tester",
						AccountId = 1,
						RealmId = 1,
						Class = ConanClass.Barbarian,
						Race = Race.Cimmerian
					});
					db.CharacterEquipment.Add(new CharacterEquipment
					{
						CharacterId = characterId,
						Wrist = await db.Items.FindAsync([3361557u], TestContext.Current.CancellationToken),
						FirstPosBag = await db.Items.FindAsync([royalSignet], TestContext.Current.CancellationToken)
					});
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				// Act: replicate PersistEquip - load equipment, resolve item, set slot, clear bag, save.
				await using (var db = NewContext(dbPath))
				{
					var equipment = await db.CharacterEquipment.FirstOrDefaultAsync(e => e.CharacterId == characterId, TestContext.Current.CancellationToken);
					var item = await db.Items.FirstOrDefaultAsync(i => i.Id == royalSignet, TestContext.Current.CancellationToken);

					equipment![CharacterEquipmentSlot.LeftRing] = item;
					if (equipment!.FirstPosBag != null && equipment.FirstPosBag.Id == item!.Id)
						equipment.FirstPosBag = null;

					await db.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				// Assert: a fresh context sees RingLeft set and FirstPosBag cleared.
				await using (var db = NewContext(dbPath))
				{
					var equipment = await db
						.CharacterEquipment.Include(e => e.RingLeft)
						.Include(e => e.FirstPosBag)
						.FirstAsync(e => e.CharacterId == characterId, TestContext.Current.CancellationToken);
					Assert.NotNull(equipment.RingLeft);
					Assert.Equal(royalSignet, equipment.RingLeft!.Id);
					Assert.Null(equipment.FirstPosBag);
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
			var options = TestDb.Options(path, true);
			return new DatabaseContext(options);
		}
	}
}
