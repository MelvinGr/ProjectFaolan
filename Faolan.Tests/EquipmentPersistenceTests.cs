using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Inventory;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// Runs EquipmentService's real equip/unequip against a temp DB and verifies the change is visible in a SEPARATE
	// context - i.e. it actually hit disk. This used to REPLICATE the service's EF operations inline, which could only
	// ever prove that the replica worked; the service is small enough to call, so it is called.
	//
	// The invariant that matters since #750 is that an equip is a MOVE: the instance that was in the bag is the one now
	// worn, so its id, its crafter and its SOCKETED GEMS come with it. Under the old wide-row model an equip re-created
	// the item at its destination from whatever the code remembered to copy, and the gems were exactly what it did not.
	public class EquipmentPersistenceTests
	{
		[Fact]
		public async Task Equip_MovesTheInstance_CarryingItsCrafterAndGems()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-equip-{Guid.NewGuid():N}.db");
			try
			{
				const uint characterId = 35020544;
				const uint royalSignet = 3735601; // a buildable item present in strie-login-inventory.bin
				const uint manacles = 3361557;
				const int bagSlot = 4;
				uint instanceId;

				// Arrange: import items from a capture, seed a character, and put a CRAFTED, GEMMED signet in the bag.
				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					await CaptureItemImporter.ImportAsync(db, [await File.ReadAllBytesAsync(TestPaths.Asset("strie-login-inventory.bin"), TestContext.Current.CancellationToken)]);

					TestDb.SeedWorld(db);
					db.Characters.Add(new Character
					{
						Id = characterId,
						Name = "Tester",
						AccountId = 1,
						RealmId = 1,
						Class = ConanClass.Barbarian,
						Race = Race.Cimmerian
					});

					var signet = new ItemInstance
					{
						OwnerCharacterId = characterId,
						Container = ContainerKind.Bag,
						Slot = bagSlot,
						TemplateId = royalSignet,
						CreatorName = "NecroL80"
					};
					signet.Sockets.Add(new ItemInstanceSocket
					{
						SocketIndex = 0,
						GemItemId = manacles // any catalog item stands in for a gem here; the FK is what matters
					});
					db.ItemInstances.Add(signet);
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);
					instanceId = signet.Id;
				}

				Assert.NotEqual(0u, instanceId); // the identity key really is store-generated

				// Act: the real service, on the real repository.
				var repo = new DatabaseRepository(new Factory(TestDb.Options(dbPath)));
				var service = new EquipmentService(repo, NullLogger.Instance);
				var client = new RecordingNetworkClient(characterId);
				client.Session.Character = (await repo.GetCharacter(characterId))!;

				await service.PersistEquip(client, CharacterEquipmentSlot.LeftRing, royalSignet, bagSlot);

				// Assert: worn, SAME instance, crafter and gem intact - and the bag cell empty.
				var worn = await repo.GetEquippedItems(characterId);
				var ring = worn[CharacterEquipmentSlot.LeftRing];
				Assert.Equal((instanceId, royalSignet, "NecroL80"), (ring.Id, ring.TemplateId, ring.CreatorName));
				Assert.Equal(manacles, Assert.Single(ring.Sockets).GemItemId);
				Assert.Null(await repo.GetCharacterItemSlot(characterId, ContainerKind.Bag, bagSlot));

				// And unequipping it back into a different bag cell is the same instance again.
				await service.PersistUnequip(client, CharacterEquipmentSlot.LeftRing, 9);

				var back = await repo.GetCharacterItemSlot(characterId, ContainerKind.Bag, 9);
				Assert.Equal((instanceId, "NecroL80"), (back!.Id, back.CreatorName));
				Assert.Equal(manacles, Assert.Single(back.Sockets).GemItemId);
				Assert.Empty(await repo.GetEquippedItems(characterId));
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		// A swap: equipping into an OCCUPIED slot puts the piece that was worn into the bag cell the new one came from,
		// which is what the client shows. Both instances survive - neither is re-minted.
		[Fact]
		public async Task Equip_IntoAnOccupiedSlot_SwapsBothInstances()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-equip-swap-{Guid.NewGuid():N}.db");
			try
			{
				const uint characterId = 35020544;
				const uint royalSignet = 3735601;
				const uint manacles = 3361557;
				const int bagSlot = 4;
				uint incoming, outgoing;

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					await CaptureItemImporter.ImportAsync(db, [await File.ReadAllBytesAsync(TestPaths.Asset("strie-login-inventory.bin"), TestContext.Current.CancellationToken)]);

					TestDb.SeedWorld(db);
					db.Characters.Add(new Character
					{
						Id = characterId,
						Name = "Tester",
						AccountId = 1,
						RealmId = 1,
						Class = ConanClass.Barbarian,
						Race = Race.Cimmerian
					});

					var inBag = new ItemInstance
					{
						OwnerCharacterId = characterId, Container = ContainerKind.Bag, Slot = bagSlot, TemplateId = royalSignet
					};
					var alreadyWorn = new ItemInstance
					{
						OwnerCharacterId = characterId, Container = ContainerKind.Equipped, Slot = (int)CharacterEquipmentSlot.LeftRing, TemplateId = manacles
					};
					db.ItemInstances.AddRange(inBag, alreadyWorn);
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);
					(incoming, outgoing) = (inBag.Id, alreadyWorn.Id);
				}

				var repo = new DatabaseRepository(new Factory(TestDb.Options(dbPath)));
				var client = new RecordingNetworkClient(characterId);
				client.Session.Character = (await repo.GetCharacter(characterId))!;

				await new EquipmentService(repo, NullLogger.Instance).PersistEquip(client, CharacterEquipmentSlot.LeftRing, royalSignet, bagSlot);

				Assert.Equal(incoming, (await repo.GetEquippedItems(characterId))[CharacterEquipmentSlot.LeftRing].Id);
				Assert.Equal(outgoing, (await repo.GetCharacterItemSlot(characterId, ContainerKind.Bag, bagSlot))!.Id);
				Assert.Equal(2, (await repo.GetCharacterItems(characterId)).Length); // nothing minted, nothing lost
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			return new DatabaseContext(TestDb.Options(path, true));
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
