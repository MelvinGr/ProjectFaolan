using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer;
using Faolan.Helpers;
using Faolan.Importer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
				const string inventoryCapture = "/home/melvin/Development/C#/Project Faolan/Captures/strie-login-inventory.bin";

				// Arrange: import items from a capture, seed a character + equipment (Wrist + FirstPosBag).
				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					await CaptureItemImporter.ImportAsync(db, [await File.ReadAllBytesAsync(inventoryCapture)]);

					db.Accounts.Add(new Account
					{
						Id = 1,
						UserName = "t",
						Password = "t",
						AuthChallenge = "",
						LastIpAddress = ""
					});
					await db.SaveChangesAsync();
					db.Characters.Add(new Character
					{
						Id = characterId,
						Name = "Tester",
						AccountId = 1
					});
					db.CharacterEquipment.Add(new CharacterEquipment
					{
						Id = characterId,
						CharacterId = characterId,
						Wrist = await db.Items.FindAsync(3361557u),
						FirstPosBag = await db.Items.FindAsync(royalSignet)
					});
					await db.SaveChangesAsync();
				}

				// Act: replicate PersistEquip - load equipment, resolve item, set slot, clear bag, save.
				await using (var db = NewContext(dbPath))
				{
					var equipment = await db.CharacterEquipment.FirstOrDefaultAsync(e => e.CharacterId == characterId);
					var item = await db.Items.FirstOrDefaultAsync(i => i.Id == royalSignet);

					Assert.True(GameServerListener.SetEquipmentSlot(equipment!, CharacterEquipmentSlot.LeftRing, item));
					if (equipment!.FirstPosBag != null && equipment.FirstPosBag.Id == item!.Id)
						equipment.FirstPosBag = null;

					await db.SaveChangesAsync();
				}

				// Assert: a fresh context sees RingLeft set and FirstPosBag cleared.
				await using (var db = NewContext(dbPath))
				{
					var equipment = await db.CharacterEquipment.Include(e => e.RingLeft).Include(e => e.FirstPosBag).FirstAsync(e => e.CharacterId == characterId);
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
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}", x => x.MigrationsAssembly("Faolan")).UseLazyLoadingProxies().Options;
			return new DatabaseContext(options);
		}
	}
}
