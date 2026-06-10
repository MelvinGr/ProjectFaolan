using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Validates the refactored repository's unit-of-work model end-to-end: each call uses a fresh short-lived
	// context from a pooled factory, and detached entities round-trip correctly across calls. This is the safety
	// net for the data-layer change (the game flows can't be exercised headless).
	public class DatabaseRepositoryTests
	{
		[Fact]
		public async Task CreateCharacter_SeedsDefaultMaxStats_AndLoadsThem()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint realmId = 1;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync())
				{
					await ctx.Database.EnsureCreatedAsync();
					ctx.Accounts.Add(new Account
					{
						Id = accountId,
						UserName = "t",
						Password = "t",
						AuthChallenge = "",
						LastIpAddress = ""
					});
					ctx.Realms.Add(new Realm
					{
						Id = realmId,
						Name = "Test",
						IpAddress = "127.0.0.1"
					});
					await ctx.SaveChangesAsync();
				}

				// CreateCharacter should cascade-insert a default MaxStats row (level-1 baseline).
				var created = await repo.CreateCharacter(accountId, realmId);
				Assert.NotNull(created.MaxStats);

				// Reload across a fresh context: the MaxStats row is loaded with the character.
				var loaded = await repo.GetCharacterForAccount(accountId, created.Id);
				Assert.NotNull(loaded!.MaxStats);
				Assert.True(loaded.MaxStats!.Health > 0);
				Assert.True(loaded.MaxStats.Stamina > 0);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		[Fact]
		public async Task EquipmentAndAccount_RoundTrip_AcrossShortLivedContexts()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint characterId = 35020544;
				const uint bagItem = 3735601; // both present in strie-login-inventory.bin (buildable after import)
				const uint slaveManacles = 3361557;
				const string inventoryCapture = "/home/melvin/Development/C#/Project Faolan/Captures/strie-login-inventory.bin";

				var (repo, factory) = NewRepo(dbPath);

				// Arrange schema + items (imported from a real capture) + base rows via a one-off context.
				await using (var ctx = await factory.CreateDbContextAsync())
				{
					await ctx.Database.EnsureCreatedAsync();
					await CaptureItemImporter.ImportAsync(ctx, [await File.ReadAllBytesAsync(inventoryCapture)]);
					ctx.Accounts.Add(new Account
					{
						Id = accountId,
						UserName = "t",
						Password = "t",
						AuthChallenge = "",
						LastIpAddress = ""
					});
					ctx.Realms.Add(new Realm { Id = 1, Name = "Test", IpAddress = "127.0.0.1" });
					ctx.Maps.Add(new Map { Id = Map.TortageBeachId, Name = "Tortage Beach" });
					ctx.Characters.Add(new Character
					{
						Id = characterId,
						Name = "Tester",
						AccountId = accountId,
						RealmId = 1
					});
					await ctx.SaveChangesAsync();
				}

				// 1) No equipment yet.
				Assert.Null(await repo.GetCharacterEquipment(characterId));

				// 2) Seed equipment with two real items (detached, from separate GetItem calls) and save.
				var equipment = new CharacterEquipment
				{
					Id = characterId,
					CharacterId = characterId,
					Wrist = await repo.GetItem(slaveManacles),
					FirstPosBag = await repo.GetItem(bagItem)
				};
				await repo.SaveCharacterEquipment(equipment);

				// 3) Reload in a fresh context: item navs resolved.
				var loaded = await repo.GetCharacterEquipment(characterId);
				Assert.NotNull(loaded);
				Assert.Equal(slaveManacles, loaded!.Wrist!.Id);
				Assert.Equal(bagItem, loaded.FirstPosBag!.Id);

				// 4) Equip the ring: move FirstPosBag -> RingLeft, save, reload, verify (the original equip bug).
				loaded.RingLeft = loaded.FirstPosBag;
				loaded.FirstPosBag = null;
				await repo.SaveCharacterEquipment(loaded);

				var afterEquip = await repo.GetCharacterEquipment(characterId);
				Assert.Equal(bagItem, afterEquip!.RingLeft!.Id);
				Assert.Null(afterEquip.FirstPosBag);

				// 5) SaveAccount persists the on-wire ClientInstance (read back by a fresh load).
				var account = await repo.GetAccount(accountId);
				account!.ClientInstance = characterId;
				await repo.SaveAccount(account);
				Assert.Equal(characterId, (await repo.GetAccount(accountId))!.ClientInstance);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		[Fact]
		public async Task RegenerateMaxStats_OnNameCommit_UpdatesSingleRow()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint realmId = 1;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync())
				{
					await ctx.Database.EnsureCreatedAsync();
					ctx.Accounts.Add(new Account
					{
						Id = accountId,
						UserName = "t",
						Password = "t",
						AuthChallenge = "",
						LastIpAddress = ""
					});
					ctx.Realms.Add(new Realm
					{
						Id = realmId,
						Name = "Test",
						IpAddress = "127.0.0.1"
					});
					await ctx.SaveChangesAsync();
				}

				// Creation seeds the level-1 rogue baseline.
				var created = await repo.CreateCharacter(accountId, realmId);

				// Simulate the editor: choose a mage class + level, regenerate, and commit (as CommitCharacterName does).
				var character = await repo.GetCharacterForAccount(accountId, created.Id);
				character!.Class = Class.Necromancer;
				character.Level = 20;
				character.Name = "Mage";
				character.RegenerateMaxStats();
				await repo.SaveCharacter(character);

				// Reload: stats reflect the chosen class, and exactly one MaxStats row exists (updated in place).
				var reloaded = await repo.GetCharacterForAccount(accountId, created.Id);
				var expected = CharacterMaxStats.CreateDefaults(Class.Necromancer, 20);
				Assert.Equal(expected.Mana, reloaded!.MaxStats!.Mana);
				Assert.Equal(expected.Health, reloaded.MaxStats.Health);

				await using (var ctx = await factory.CreateDbContextAsync())
					Assert.Equal(1, await ctx.CharacterMaxStats.CountAsync(s => s.CharacterId == created.Id));
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		[Fact]
		public async Task SaveCharacter_PersistsNameAndBody_AsOneGraph()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint characterId = 35020545;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync())
				{
					await ctx.Database.EnsureCreatedAsync();
					ctx.Accounts.Add(new Account
					{
						Id = accountId,
						UserName = "t",
						Password = "t",
						AuthChallenge = "",
						LastIpAddress = ""
					});
					ctx.Realms.Add(new Realm { Id = 1, Name = "Test", IpAddress = "127.0.0.1" });
					ctx.Maps.Add(new Map { Id = Map.TortageBeachId, Name = "Tortage Beach" });
					ctx.Characters.Add(new Character
					{
						Id = characterId,
						AccountId = accountId,
						RealmId = 1
					});
					await ctx.SaveChangesAsync();
				}

				// Simulate the create-editor: accumulate name + body morph on the detached character, then commit.
				var character = await repo.GetCharacter(characterId);
				character!.Name = "Conan";
				character.Body = new CharacterBody
				{
					CharacterId = characterId,
					Arm = 59,
					Chest = 30
				};
				await repo.SaveCharacter(character);

				var reloaded = await repo.GetCharacter(characterId);
				Assert.Equal("Conan", reloaded!.Name);
				Assert.NotNull(reloaded.Body);
				Assert.Equal(59, reloaded.Body!.Arm);
				Assert.Equal(30, reloaded.Body.Chest);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		// The inventory-cell unit of work behind equip/unequip persistence: upsert parks an item into a bag
		// cell, a second upsert in another cell coexists (the FirstPosBag regression this replaces), re-upsert
		// updates in place, clear removes the row - all across short-lived contexts.
		[Fact]
		public async Task InventorySlot_UpsertAndClear_RoundTripAcrossContexts()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync())
				{
					await ctx.Database.EnsureCreatedAsync();
					ctx.Accounts.Add(new Account { Id = 1, UserName = "t", Password = "t", AuthChallenge = "", LastIpAddress = "" });
					ctx.Realms.Add(new Realm { Id = 1, Name = "Test", IpAddress = "127.0.0.1" });
					ctx.Maps.Add(new Map { Id = Map.TortageBeachId, Name = "Tortage Beach" });
					ctx.Characters.Add(new Character { Id = 5, Name = "T", AccountId = 1, RealmId = 1 });
					ctx.Items.Add(new Item { Id = 100, Name = "A" });
					ctx.Items.Add(new Item { Id = 200, Name = "B" });
					await ctx.SaveChangesAsync();
				}

				await repo.SetCharacterInventorySlot(5, 104, 2, 100);
				await repo.SetCharacterInventorySlot(5, 104, 7, 200, 3); // a second cell coexists

				var rows = await repo.GetCharacterInventory(5);
				Assert.Equal(2, rows.Length);
				Assert.Equal(100u, System.Linq.Enumerable.Single(rows, r => r.Slot == 2).ItemId);
				Assert.Equal("A", System.Linq.Enumerable.Single(rows, r => r.Slot == 2).Item!.Name); // Item nav loaded
				Assert.Equal(3u, System.Linq.Enumerable.Single(rows, r => r.Slot == 7).Count);

				await repo.SetCharacterInventorySlot(5, 104, 2, 200); // re-upsert updates in place
				await repo.SetCharacterInventorySlot(5, 104, 7, null); // clear removes
				await repo.SetCharacterInventorySlot(5, 104, 9, null); // clearing an empty cell is a no-op

				rows = await repo.GetCharacterInventory(5);
				var only = Assert.Single(rows);
				Assert.Equal(2, only.Slot);
				Assert.Equal(200u, only.ItemId);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		private static (DatabaseRepository repo, IDbContextFactory<DatabaseContext> factory) NewRepo(string path)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}").Options;
			var factory = new TestContextFactory(options);
			return (new DatabaseRepository(factory), factory);
		}

		private sealed class TestContextFactory : IDbContextFactory<DatabaseContext>
		{
			private readonly DbContextOptions<DatabaseContext> _options;

			public TestContextFactory(DbContextOptions<DatabaseContext> options)
			{
				_options = options;
			}

			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(_options);
			}
		}
	}
}
