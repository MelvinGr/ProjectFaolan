using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Helpers;
using Faolan.Importer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.PacketAnalyzer.Tests
{
	// Validates the refactored repository's unit-of-work model end-to-end: each call uses a fresh short-lived
	// context from a pooled factory, and detached entities round-trip correctly across calls. This is the safety
	// net for the data-layer change (the game flows can't be exercised headless).
	public class DatabaseRepositoryTests
	{
		[Fact]
		public async Task EquipmentAndAccount_RoundTrip_AcrossShortLivedContexts()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint characterId = 35020544;
				const uint bagItem = 3735601;       // both present in strie-login-inventory.bin (buildable after import)
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
					ctx.Characters.Add(new Character
					{
						Id = characterId,
						Name = "Tester",
						AccountId = accountId
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
					ctx.Characters.Add(new Character
					{
						Id = characterId,
						AccountId = accountId
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
