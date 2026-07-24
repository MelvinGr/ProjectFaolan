using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Validates the refactored repository's unit-of-work model end-to-end: each call uses a fresh short-lived
	// context from a pooled factory, and detached entities round-trip correctly across calls. This is the safety
	// net for the data-layer change (the game flows can't be exercised headless).
	public class DatabaseRepositoryTests
	{
		// Bank deposit/withdraw persistence (issue #124): a move into the personal bank (container 105) is written to
		// CharacterInventory and the bank rebuilds from it (the bank is NOT in the login snapshot - it's delivered on
		// demand via BuildContainer from the kind-105 rows), so it survives relog. This is the data-layer contract
		// behind PersistInventoryMove + BankHandler; the move handler just routes the kind-105 source/dest through it.
		[Fact]
		public async Task BankDepositWithdraw_PersistsAndRebuilds()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint realmId = 1;
				const uint itemId = 354477; // an item with a real hash (renders in the container)
				const ContainerKind bank = FullPlayerCharacterBuilder.BankKind; // 105
				const ContainerKind bag = FullPlayerCharacterBuilder.BagKind; // 104

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
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
					ctx.Items.Add(new Item
					{
						Id = itemId,
						Name = "Ape King's Treasure",
						Hash = 1497905484,
						Key = new ProtoDecryptionKey
						{
							Key1 = 1,
							Key2 = 2,
							Key3 = 3,
							Key4 = 4
						}
					});
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var character = await repo.CreateCharacter(accountId, realmId);

				// Deposit: item lands in bank slot 3 (what PersistInventoryMove writes for a bag->bank move).
				await repo.SetCharacterInventorySlot(character.Id, bank, 3, itemId);

				// Reload across a fresh context: the bank row persists, and the on-demand bank container rebuilds it.
				var bankRows = (await repo.GetCharacterInventory(character.Id)).Where(r => r.Container == bank).ToList();
				Assert.Single(bankRows);
				var container = FullPlayerCharacterBuilder.BuildContainer(bank, 50, 30, character.Id, bankRows);
				Assert.Contains(container.Items, e => e.Pos == 3);

				// Withdraw: bank slot 3 -> bag slot 0 (the move clears the bank cell, writes the bag cell).
				await repo.SetCharacterInventorySlot(character.Id, bank, 3, null);
				await repo.SetCharacterInventorySlot(character.Id, bag, 0, itemId);

				var rows = await repo.GetCharacterInventory(character.Id);
				Assert.DoesNotContain(rows, r => r.Container == bank); // bank now empty
				Assert.Contains(rows, r => r is { Container: bag, Slot: 0, ItemId: itemId });
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		// Persisted self-buffs (#274) round-trip with their absolute expiry, and SaveCharacterBuffs is replace-all -
		// the second snapshot wholly replaces the first, so a cancelled/expired buff drops out. This is the data-layer
		// contract behind PlayerPersistenceService (snapshot on logout) + RestoreBuffs (re-apply on entry).
		[Fact]
		public async Task CharacterBuffs_RoundTrip_AndReplaceAllOnResave()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-buffs-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint realmId = 1;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
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
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var character = await repo.CreateCharacter(accountId, realmId);
				var expiry = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

				// First snapshot: a timed buff + a permanent buff (null expiry).
				await repo.SaveCharacterBuffs(character.Id, [
					new CharacterBuff
					{
						BuffId = 0x02387E03,
						Nonce = 0x14C5C912,
						ExpiresAt = expiry
					},
					new CharacterBuff
					{
						BuffId = 0x0037F298,
						Nonce = 0x195DA1FE,
						ExpiresAt = null
					}
				]);

				var loaded = await repo.GetCharacterBuffs(character.Id);
				Assert.Equal(2, loaded.Length);
				var timed = Assert.Single(loaded, b => b.BuffId == 0x02387E03);
				Assert.Equal(0x14C5C912u, timed.Nonce);
				Assert.Equal(expiry, timed.ExpiresAt);
				var permanent = Assert.Single(loaded, b => b.BuffId == 0x0037F298);
				Assert.Equal(0x195DA1FEu, permanent.Nonce);
				Assert.Null(permanent.ExpiresAt);

				// Re-save with only the permanent buff (the timed one expired): replace-all drops the missing row.
				await repo.SaveCharacterBuffs(character.Id, [
					new CharacterBuff
					{
						BuffId = 0x0037F298,
						Nonce = 0x195DA1FE,
						ExpiresAt = null
					}
				]);

				var after = await repo.GetCharacterBuffs(character.Id);
				Assert.Equal(0x0037F298u, Assert.Single(after).BuffId);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		[Fact]
		public async Task ChatMessages_PersistAndQueryRecent_NewestFirst_AndByChannel()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-chat-{Guid.NewGuid():N}.db");
			try
			{
				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

				var t0 = DateTimeOffset.UtcNow;
				await repo.SaveChatMessage(new ChatMessage
				{
					SentAt = t0,
					SenderId = 1,
					SenderName = "A",
					ChannelType = 5,
					ChannelId = 100,
					Text = "first"
				});
				await repo.SaveChatMessage(new ChatMessage
				{
					SentAt = t0.AddSeconds(1),
					SenderId = 2,
					SenderName = "B",
					ChannelType = 5,
					ChannelId = 100,
					Text = "second"
				});
				await repo.SaveChatMessage(new ChatMessage
				{
					SentAt = t0.AddSeconds(2),
					SenderId = 1,
					SenderName = "A",
					TargetId = 2,
					IsWhisper = true,
					Text = "psst"
				});

				var all = await repo.GetRecentChatMessages();
				Assert.Equal(3, all.Length);
				Assert.Equal("psst", all[0].Text); // newest-first
				Assert.True(all[0].IsWhisper);
				Assert.Equal(2u, all[0].TargetId);

				var ch100 = await repo.GetRecentChatMessages(channelId: 100);
				Assert.Equal(2, ch100.Length); // the whisper (channel 0) is excluded
				Assert.All(ch100, m => Assert.Equal(100u, m.ChannelId));
				Assert.Equal("second", ch100[0].Text); // newest-first within the channel

				Assert.Single(await repo.GetRecentChatMessages(1)); // limit honoured
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		[Fact]
		public async Task CreateCharacter_SeedsDefaultMaxStats_AndLoadsThem()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint realmId = 1;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
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
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
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
				var inventoryCapture = TestPaths.Asset("strie-login-inventory.bin");

				var (repo, factory) = NewRepo(dbPath);

				// Arrange schema + items (imported from a real capture) + base rows via a one-off context.
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					await CaptureItemImporter.ImportAsync(ctx, [await File.ReadAllBytesAsync(inventoryCapture, TestContext.Current.CancellationToken)]);
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
						Id = 1,
						Name = "Test",
						IpAddress = "127.0.0.1"
					});
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					ctx.Characters.Add(new Character
					{
						Id = characterId,
						Name = "Tester",
						AccountId = accountId,
						RealmId = 1,
						Class = Class.Barbarian,
						Race = Race.Cimmerian
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				// 1) No equipment yet.
				Assert.Null(await repo.GetCharacterEquipment(characterId));

				// 2) Seed equipment with two real items (detached, from separate GetItem calls) and save.
				var equipment = new CharacterEquipment
				{
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

		// Search returns the matching listings cheapest-first. Guards the SQLite "can't ORDER BY a ulong" trap: Price
		// is a ulong, so the sort must run client-side - ordering in SQL throws and drops the client on every search.
		[Fact]
		public async Task GetTradepostListings_SortsByPrice_CheapestFirst()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Realms.Add(new Realm
					{
						Id = 1,
						Name = "R",
						IpAddress = "127.0.0.1",
						Port = 1
					}); // #324: RealmId FK
					// ItemId is FK-restricted to Items.Id (#212), so seed the referenced catalog items first.
					ctx.Items.Add(new Item
					{
						Id = 0x7530,
						Name = "WareA"
					});
					ctx.Items.Add(new Item
					{
						Id = 0x9999,
						Name = "WareB"
					});
					ctx.TradepostListings.Add(new TradepostListing
					{
						SaleId = 1,
						RealmId = 1,
						ItemId = 0x7530,
						Template = 0x7530, // FK-restricted to Items.Id (the instance template's key lives there)
						Price = 5000,
						SellerName = "b"
					});
					ctx.TradepostListings.Add(new TradepostListing
					{
						SaleId = 2,
						RealmId = 1,
						ItemId = 0x7530,
						Template = 0x7530,
						Price = 25,
						SellerName = "a"
					});
					ctx.TradepostListings.Add(new TradepostListing
					{
						SaleId = 3,
						RealmId = 1,
						ItemId = 0x9999,
						Template = 0x9999,
						Price = 1,
						SellerName = "c"
					}); // other item
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var listings = await repo.GetTradepostListings(0x7530, 1);

				Assert.Equal(2, listings.Length); // only the matching item
				Assert.Equal(25ul, listings[0].Price); // cheapest first
				Assert.Equal(5000ul, listings[1].Price);
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
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Accounts.Add(new Account
					{
						Id = 1,
						UserName = "t",
						Password = "t",
						AuthChallenge = "",
						LastIpAddress = ""
					});
					ctx.Realms.Add(new Realm
					{
						Id = 1,
						Name = "Test",
						IpAddress = "127.0.0.1"
					});
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					ctx.Characters.Add(new Character
					{
						Id = 5,
						Name = "T",
						AccountId = 1,
						RealmId = 1,
						Class = Class.Barbarian,
						Race = Race.Cimmerian
					});
					ctx.Items.Add(new Item
					{
						Id = 100,
						Name = "A"
					});
					ctx.Items.Add(new Item
					{
						Id = 200,
						Name = "B"
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				await repo.SetCharacterInventorySlot(5, (ContainerKind)104, 2, 100);
				await repo.SetCharacterInventorySlot(5, (ContainerKind)104, 7, 200, 3); // a second cell coexists

				var rows = await repo.GetCharacterInventory(5);
				Assert.Equal(2, rows.Length);
				Assert.Equal(100u, rows.Single(r => r.Slot == 2).ItemId);
				Assert.Equal("A", rows.Single(r => r.Slot == 2).Item!.Name); // Item nav loaded
				Assert.Equal(3u, rows.Single(r => r.Slot == 7).Count);

				await repo.SetCharacterInventorySlot(5, (ContainerKind)104, 2, 200); // re-upsert updates in place
				await repo.SetCharacterInventorySlot(5, (ContainerKind)104, 7, null); // clear removes
				await repo.SetCharacterInventorySlot(5, (ContainerKind)104, 9, null); // clearing an empty cell is a no-op

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

		[Fact]
		public async Task RegenerateMaxStats_OnNameCommit_UpdatesSingleRow()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint realmId = 1;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
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
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
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

				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
					Assert.Equal(1, await ctx.CharacterMaxStats.CountAsync(s => s.CharacterId == created.Id, TestContext.Current.CancellationToken));
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
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
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
						Id = 1,
						Name = "Test",
						IpAddress = "127.0.0.1"
					});
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					ctx.Characters.Add(new Character
					{
						Id = characterId,
						AccountId = accountId,
						RealmId = 1,
						Class = Class.Barbarian,
						Race = Race.Cimmerian
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
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

		// The targeted scalar savers (ExecuteUpdate-backed: money / transform / progress) persist their columns the
		// moment they change and leave the rest of the character graph (e.g. the MaxStats nav) untouched - the point
		// of replacing the full-graph SaveCharacter on these hot paths.
		[Fact]
		public async Task ScalarSavers_PersistColumns_WithoutDisturbingTheGraph()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				const uint accountId = 1;
				const uint realmId = 1;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
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
					ctx.Maps.Add(new Map
					{
						Id = 2040,
						Name = "Test Destination"
					}); // Character.MapId is a FK; the transform target must exist
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					}); // CreateCharacter's default MapId
					ctx.RespawnPoints.Add(new RespawnPoint
					{
						Id = 7,
						MapId = 2040,
						Name = "Test Bind",
						AlwaysKnown = true
					}); // Character.BindRespawnPointId FK target
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var created = await repo.CreateCharacter(accountId, realmId); // seeds a MaxStats nav row
				Assert.NotNull(created.MaxStats);

				Assert.False(created.HasBind); // a fresh character is unbound (BindMapId 0)

				await repo.SetCharacterMoney(created.Id, 12345);
				await repo.SaveCharacterTransform(created.Id, new Vector3(10, 20, 30), new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), 2040);
				await repo.SetCharacterProgress(created.Id, 4500, 17);
				await repo.SaveCharacterBind(created.Id, 7); // bind to the seeded RespawnPoint

				var loaded = await repo.GetCharacter(created.Id);
				Assert.Equal(12345ul, loaded!.Money);
				Assert.Equal(new Vector3(10, 20, 30), loaded.Position);
				Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), loaded.Rotation);
				Assert.Equal(2040u, loaded.MapId);
				Assert.Equal(4500u, loaded.Experience);
				Assert.Equal((ushort)17, loaded.Level);
				Assert.True(loaded.HasBind);
				Assert.Equal(7u, loaded.BindRespawnPointId);
				Assert.Equal(2040u, (await repo.GetRespawnPoint(7))!.MapId);

				// The MaxStats navigation the full-graph save would have rewritten is still intact.
				Assert.NotNull(loaded.MaxStats);
				Assert.True(loaded.MaxStats!.Health > 0);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		// The auction-house BUY -> mail data path: a listing is resolvable by saleId and removed on buy; the system
		// mail (with the escrowed item) round-trips by recipient and wire id, the read flag flips, an attachment can be
		// taken (removed) leaving the mail, and delete cascades the remaining attachments. Backs TODO §9a Step 3.
		[Fact]
		public async Task TradepostBuyMail_DataPath_RoundTrips()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-repo-{Guid.NewGuid():N}.db");
			try
			{
				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Realms.Add(new Realm
					{
						Id = 1,
						Name = "R",
						IpAddress = "127.0.0.1",
						Port = 1
					}); // #324: RealmId FK
					// The listing's ItemId (search key) AND Template (instance, where the key lives) are both FK-restricted
					// to Items.Id, so seed both referenced catalog items.
					ctx.Items.Add(new Item
					{
						Id = 0x7530,
						Name = "TestWare"
					});
					ctx.Items.Add(new Item
					{
						Id = 0x3E0A97,
						Name = "TestInstanceTemplate"
					});
					ctx.TradepostListings.Add(new TradepostListing
					{
						SaleId = 0x20d82b62d02b198b,
						Handle = 1,
						RealmId = 1,
						SellerName = "Ponakin",
						Price = 25,
						Quantity = 1,
						ItemId = 0x7530,
						Template = 0x3E0A97,
						ItemLevel = 5
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				// Resolve + remove the listing (the buy).
				var listing = await repo.GetTradepostListingBySaleId(0x20d82b62d02b198b, 1);
				Assert.NotNull(listing);
				Assert.Equal("Ponakin", listing!.SellerName);
				await repo.DeleteTradepostListing(0x20d82b62d02b198b, 1);
				Assert.Null(await repo.GetTradepostListingBySaleId(0x20d82b62d02b198b, 1));

				// Create the system mail with the bought item escrowed.
				var mail = new Mail
				{
					MailId = 0x20d82b631c0c578f,
					RealmId = 1,
					RecipientClass = 0x08006F31,
					RecipientName = "AssassinL23",
					SenderClass = 0x08006F31,
					SenderName = "",
					BodyXml = "<remoteformat />",
					FooterWord = 0x0B
				};
				mail.Attachments.Add(new MailAttachment
				{
					ItemInstanceId = 0x20d82b62d02b198b,
					Quantity = 1,
					Template = 0x3E0A97
				});
				await repo.CreateMail(mail);

				// Retrieve by recipient + by wire id (attachments eager-loaded).
				var inbox = await repo.GetMailsForRecipient("AssassinL23", 1);
				Assert.Single(inbox);
				var byId = await repo.GetMailByWireId(0x20d82b631c0c578f);
				Assert.NotNull(byId);
				Assert.Single(byId!.Attachments);
				Assert.False(byId.IsRead);

				// Mark read (footer flips on the wire), then take the attachment (it leaves the mail).
				await repo.SetMailRead(0x20d82b631c0c578f, true);
				await repo.RemoveMailAttachment(0x20d82b631c0c578f, 0x20d82b62d02b198b);
				var afterTake = await repo.GetMailByWireId(0x20d82b631c0c578f);
				Assert.True(afterTake!.IsRead);
				// The attachment must be GONE FROM THE DB, not just from a nav the caller already mutated: the row
				// surviving means the item can be taken again after a relog (a dupe) and the mail can never be
				// deleted. RemoveMailAttachment takes the WIRE id but the FK is to the ROW key, and the uint/ulong
				// widening made the mismatch compile and silently delete nothing. (#368)
				Assert.Empty(afterTake.Attachments);

				// Delete the mail.
				await repo.DeleteMail(0x20d82b631c0c578f);
				Assert.Null(await repo.GetMailByWireId(0x20d82b631c0c578f));
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		private static (DatabaseRepository repo, IDbContextFactory<DatabaseContext> factory) NewRepo(string path)
		{
			var options = TestDb.Options(path);
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
