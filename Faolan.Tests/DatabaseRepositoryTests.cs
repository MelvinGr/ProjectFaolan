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
				const ContainerKind bank = ContainerKind.Bank; // 105
				const ContainerKind bag = ContainerKind.Bag; // 104

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Accounts.Add(new Account
					{
						Id = accountId,
						UserName = "t",
						Password = "t",
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
				var deposited = await repo.SetCharacterItemSlot(character.Id, bank, 3, itemId);
				Assert.NotNull(deposited);

				// Reload across a fresh context: the bank item persists, and the on-demand bank container rebuilds it.
				var banked = await repo.GetCharacterItems(character.Id, bank);
				Assert.Single(banked);
				var container = FullCharacterRoots.Container(bank, 50, 30, character.Id, banked);
				Assert.Contains(container.Items, e => e.Pos == 3);

				// Withdraw: bank slot 3 -> bag slot 0. A MOVE, so it is the SAME instance in the bag afterwards - which is
				// the whole point of an item being a row rather than an address (#750).
				Assert.True(await repo.MoveCharacterItem(character.Id, bank, 3, bag, 0));

				var held = await repo.GetCharacterItems(character.Id);
				Assert.DoesNotContain(held, i => i.Container == bank); // bank now empty
				var withdrawn = Assert.Single(held);
				Assert.Equal((bag, 0, itemId, deposited!.Id), (withdrawn.Container, withdrawn.Slot, withdrawn.TemplateId, withdrawn.Id));
			}
			finally
			{
				TestDb.Delete(dbPath);
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
					TestDb.SeedWorld(ctx, accountId, realmId);
				}

				var character = await repo.CreateCharacter(accountId, realmId);
				var expiry = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

				// First snapshot: a timed buff + a permanent buff (null expiry).
				await repo.SaveCharacterBuffs(character.Id, [
					new CharacterBuff
					{
						BuffId = 0x02387E03,
						ExpiresAt = expiry
					},
					new CharacterBuff
					{
						BuffId = 0x0037F298,
						ExpiresAt = null
					}
				]);

				var loaded = await repo.GetCharacterBuffs(character.Id);
				Assert.Equal(2, loaded.Length);
				var timed = Assert.Single(loaded, b => b.BuffId == 0x02387E03);
				Assert.Equal(expiry, timed.ExpiresAt);
				var permanent = Assert.Single(loaded, b => b.BuffId == 0x0037F298);
				Assert.Null(permanent.ExpiresAt);

				// Re-save with only the permanent buff (the timed one expired): replace-all drops the missing row.
				await repo.SaveCharacterBuffs(character.Id, [
					new CharacterBuff
					{
						BuffId = 0x0037F298,
						ExpiresAt = null
					}
				]);

				var after = await repo.GetCharacterBuffs(character.Id);
				Assert.Equal(0x0037F298u, Assert.Single(after).BuffId);
			}
			finally
			{
				TestDb.Delete(dbPath);
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
				TestDb.Delete(dbPath);
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
					TestDb.SeedWorld(ctx, accountId, realmId);
				}

				// Vitals are DERIVED from (Class, Level) (#578) - there is no row to cascade-insert, and a reloaded
				// character reports the same ceiling as the one just created because both compute it.
				var created = await repo.CreateCharacter(accountId, realmId);
				Assert.True(created.MaxVitals(VitalAttributes.LevelOneBase).Health > 0);

				var loaded = await repo.GetCharacterForAccount(accountId, created.Id);
				Assert.Equal(created.MaxVitals(VitalAttributes.LevelOneBase).Health, loaded!.MaxVitals(VitalAttributes.LevelOneBase).Health);
				Assert.True(loaded.MaxVitals(VitalAttributes.LevelOneBase).Stamina > 0);
			}
			finally
			{
				TestDb.Delete(dbPath);
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
						Class = ConanClass.Barbarian,
						Race = Race.Cimmerian
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				// 1) Nothing worn yet. An UNDRESSED character is an empty worn set, not a missing row: since #750 there is
				// no equipment row whose absence could mean anything else.
				Assert.Empty(await repo.GetEquippedItems(characterId));

				// 2) Wear one item and put another in the bag.
				await repo.SetCharacterItemSlot(characterId, ContainerKind.Equipped, (int)CharacterEquipmentSlot.Wrist, slaveManacles);
				var inBag = await repo.SetCharacterItemSlot(characterId, ContainerKind.Bag, 1, bagItem);

				// 3) Reload in a fresh context: templates resolved.
				var worn = await repo.GetEquippedItems(characterId);
				Assert.Equal(slaveManacles, worn[CharacterEquipmentSlot.Wrist].TemplateId);
				Assert.Equal(bagItem, (await repo.GetCharacterItemSlot(characterId, ContainerKind.Bag, 1))!.TemplateId);

				// 4) Equip it: bag[1] -> the left ring slot. The instance is the same one, worn.
				Assert.True(await repo.MoveCharacterItem(characterId, ContainerKind.Bag, 1, ContainerKind.Equipped, (int)CharacterEquipmentSlot.LeftRing));

				var afterEquip = await repo.GetEquippedItems(characterId);
				Assert.Equal((bagItem, inBag!.Id), (afterEquip[CharacterEquipmentSlot.LeftRing].TemplateId, afterEquip[CharacterEquipmentSlot.LeftRing].Id));
				Assert.Null(await repo.GetCharacterItemSlot(characterId, ContainerKind.Bag, 1));

				// 5) SaveAccount persists the on-wire ClientInstance (read back by a fresh load).
				var account = await repo.GetAccount(accountId);
				account!.ClientInstance = characterId;
				await repo.SaveAccount(account);
				Assert.Equal(characterId, (await repo.GetAccount(accountId))!.ClientInstance);
			}
			finally
			{
				TestDb.Delete(dbPath);
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
				TestDb.Delete(dbPath);
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
						Class = ConanClass.Barbarian,
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

				await repo.SetCharacterItemSlot(5, ContainerKind.Bag, 2, 100);
				await repo.SetCharacterItemSlot(5, ContainerKind.Bag, 7, 200, 3); // a second cell coexists

				var items = await repo.GetCharacterItems(5);
				Assert.Equal(2, items.Length);
				Assert.Equal(100u, items.Single(i => i.Slot == 2).TemplateId);
				Assert.Equal("A", items.Single(i => i.Slot == 2).Template.Name); // Template nav loaded
				Assert.Equal(3u, items.Single(i => i.Slot == 7).Count);

				await repo.SetCharacterItemSlot(5, ContainerKind.Bag, 2, 200); // minting over an occupied cell replaces it
				await repo.SetCharacterItemSlot(5, ContainerKind.Bag, 7, null); // clear removes
				await repo.SetCharacterItemSlot(5, ContainerKind.Bag, 9, null); // clearing an empty cell is a no-op

				items = await repo.GetCharacterItems(5);
				var only = Assert.Single(items);
				Assert.Equal(2, only.Slot);
				Assert.Equal(200u, only.TemplateId);
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		// Vitals FOLLOW the class/level committed by the editor with nothing to regenerate or keep in sync, because
		// they are computed rather than stored (#578). This used to assert that a MaxStats row was updated in place
		// and that exactly one existed; the row is gone, so what is left to prove is that the committed class is what
		// the character reports.
		[Fact]
		public async Task MaxVitals_OnNameCommit_FollowTheCommittedClass()
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
					TestDb.SeedWorld(ctx, accountId, realmId);
				}

				// Creation seeds the level-1 rogue baseline.
				var created = await repo.CreateCharacter(accountId, realmId);

				// Simulate the editor: choose a mage class + level, regenerate, and commit (as CommitCharacterName does).
				var character = await repo.GetCharacterForAccount(accountId, created.Id);
				character!.Class = ConanClass.Necromancer;
				character.Level = 20;
				character.Name = "Mage";
				await repo.SaveCharacter(character);

				// Reload: vitals reflect the chosen class purely from the stored Class/Level.
				var reloaded = await repo.GetCharacterForAccount(accountId, created.Id);
				var expected = CharacterMaxStats.For(ConanClass.Necromancer, 20, VitalAttributes.LevelOneBase);
				Assert.Equal(expected.Mana, reloaded!.MaxVitals(VitalAttributes.LevelOneBase).Mana);
				Assert.Equal(expected.Health, reloaded.MaxVitals(VitalAttributes.LevelOneBase).Health);
			}
			finally
			{
				TestDb.Delete(dbPath);
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
						Class = ConanClass.Barbarian,
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
				TestDb.Delete(dbPath);
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

				var created = await repo.CreateCharacter(accountId, realmId);
				Assert.True(created.MaxVitals(VitalAttributes.LevelOneBase).Health > 0);

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

				// Vitals track the level the targeted writes just changed - nothing to rewrite, nothing to go stale.
				Assert.Equal(CharacterMaxStats.For(loaded.Class, 17, VitalAttributes.LevelOneBase).Health, loaded.MaxVitals(VitalAttributes.LevelOneBase).Health);
			}
			finally
			{
				TestDb.Delete(dbPath);
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
						Template = 0x3E0A97
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
				TestDb.Delete(dbPath);
			}
		}

		// The web and game logins used to disagree about case: Identity's FindByNameAsync normalises to upper-case, so
		// the web let you in as "Faolan" for a row that said "faolan", while Database.GetAccount(name) - the game's
		// UniverseAgent lookup and the `.account` GM command - matched exactly and did not. Same account, same
		// password, different answer depending on which door you used.
		[Theory]
		[InlineData("faolan")]
		[InlineData("Faolan")]
		[InlineData("FAOLAN")]
		[InlineData("fAoLaN")]
		public async Task GetAccountByName_IsCaseInsensitive(string typed)
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-acct-{Guid.NewGuid():N}.db");
			try
			{
				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Accounts.Add(new Account
					{
						Id = 1,
						UserName = "faolan",
						Password = "-",
						LastIpAddress = ""
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var account = await repo.GetAccount(typed);

				Assert.NotNull(account);
				Assert.Equal("faolan", account.UserName);
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		// A name that differs by more than case is still a different account - the fold must not swallow real misses.
		[Fact]
		public async Task GetAccountByName_StillRejectsADifferentName()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-acct-{Guid.NewGuid():N}.db");
			try
			{
				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Accounts.Add(new Account
					{
						Id = 1,
						UserName = "faolan",
						Password = "-",
						LastIpAddress = ""
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				Assert.Null(await repo.GetAccount("faolan2"));
				Assert.Null(await repo.GetAccount("faola"));
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		// #590: a feat's SPELL boost reaches combat as `spellId -> percent x 100`, which takes TWO hops the query has
		// to get right - CharacterFeats holds the WIRE feat id while the modifier rows are keyed by the TEMPLATE id
		// (different id spaces, hence FeatIdMaps), and the modifier then names a spell LINE that fans out to every
		// RANK. This is Decomposition rank 5 in miniature: one feat, one line, three ranks of the spell.
		[Fact]
		public async Task SpellBoosts_ResolveThroughTheIdMapAndFanOutToEveryRank()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-boosts-{Guid.NewGuid():N}.db");
			try
			{
				const uint characterId = 7;
				const uint wireFeatId = 80004, templateId = 1173628, otherTemplateId = 1173600;
				const uint line = 41000;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Accounts.Add(new Account { Id = 1, UserName = "t", Password = "t", LastIpAddress = "" });
					ctx.Realms.Add(new Realm { Id = 1, Name = "Test", IpAddress = "127.0.0.1" });
					ctx.Maps.Add(new Map { Id = Map.TortageBeachId, Name = "Tortage Beach" });
					ctx.Characters.Add(new Character
					{
						Id = characterId, Name = "Caster", AccountId = 1, RealmId = 1, Class = ConanClass.Necromancer, Race = Race.Stygian
					});
					ctx.CharacterFeatSpecs.Add(new CharacterFeatSpec { CharacterId = characterId, Index = 0 });
					ctx.CharacterFeats.Add(new CharacterFeat { CharacterId = characterId, SpecIndex = 0, FeatId = wireFeatId, Sequence = 0 });
					ctx.FeatIdMaps.Add(new FeatIdMap { WireFeatId = wireFeatId, TemplateId = templateId, BaseIndex = 800, Rank = 4 });
					ctx.Feats.Add(new Feat { Id = templateId, Name = "Decomposition", BaseName = "Decomposition" });
					ctx.Feats.Add(new Feat { Id = otherTemplateId, Name = "Untrained", BaseName = "Untrained" });
					ctx.FeatSpellModifiers.Add(new FeatSpellModifier { FeatId = templateId, Sequence = 0, SpellLineId = line, PercentHundredths = 3500, EffectKind = 53075 });
					// A modifier on a feat the character has NOT trained, on the same line: it must not leak in.
					ctx.FeatSpellModifiers.Add(new FeatSpellModifier { FeatId = otherTemplateId, Sequence = 0, SpellLineId = line, PercentHundredths = 9900, EffectKind = 53075 });
					// Three ranks of the boosted line, plus one spell on another line that must stay unboosted.
					ctx.Spells.Add(new Spell { Id = 144675, Name = "Flesh to Worms (Rank 3)", GuiStackingLine = line });
					ctx.Spells.Add(new Spell { Id = 144676, Name = "Flesh to Worms (Rank 4)", GuiStackingLine = line });
					ctx.Spells.Add(new Spell { Id = 144677, Name = "Flesh to Worms (Rank 5)", GuiStackingLine = line });
					ctx.Spells.Add(new Spell { Id = 144308, Name = "Pestilential Blast (Rank 2)", GuiStackingLine = 41001 });
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var boosts = await repo.GetCharacterSpellBoosts(characterId);

				// Every rank of the line, at the trained feat's percent - the feat names a line, not a rank.
				Assert.Equal(3, boosts.Count);
				Assert.Equal(3500, boosts[144675]);
				Assert.Equal(3500, boosts[144676]);
				Assert.Equal(3500, boosts[144677]);
				Assert.DoesNotContain(144308u, boosts.Keys); // a different line is untouched
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		// #697: a feat's COMBO bonuses reach combat as `comboId -> bonus`, through the same two hops the spell boosts
		// take. Two things this has to get right that the spell query does not: the two source tables mean DIFFERENT
		// things (one weapon attack vs the combo as a whole) so they land in different fields, and their UNITS differ
		// - `FeatComboAttackModifier.DpsPercent` is a whole percent and `FeatComboModifier` is hundredths, which is
		// the client's own doing (#593). A single sum would be wrong by 100x on one of them.
		[Fact]
		public async Task ComboBonuses_KeepTheTwoKindsApartAndNormaliseTheirUnits()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-combo-bonus-{Guid.NewGuid():N}.db");
			try
			{
				const uint characterId = 9;
				const uint wireFeatId = 80104, templateId = 3527884, otherTemplateId = 3527800;
				const uint comboId = 3202262, otherComboId = 228651;

				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Accounts.Add(new Account { Id = 1, UserName = "t", Password = "t", LastIpAddress = "" });
					ctx.Realms.Add(new Realm { Id = 1, Name = "Test", IpAddress = "127.0.0.1" });
					ctx.Maps.Add(new Map { Id = Map.TortageBeachId, Name = "Tortage Beach" });
					ctx.Characters.Add(new Character { Id = characterId, Name = "Fighter", AccountId = 1, RealmId = 1, Class = ConanClass.Conqueror, Race = Race.Cimmerian });
					ctx.CharacterFeatSpecs.Add(new CharacterFeatSpec { CharacterId = characterId, Index = 0 });
					ctx.CharacterFeats.Add(new CharacterFeat { CharacterId = characterId, SpecIndex = 0, FeatId = wireFeatId, Sequence = 0 });
					ctx.FeatIdMaps.Add(new FeatIdMap { WireFeatId = wireFeatId, TemplateId = templateId, BaseIndex = 801, Rank = 4 });
					ctx.Feats.Add(new Feat { Id = templateId, Name = "Brutal Cyclone", BaseName = "Brutal Cyclone" });
					ctx.Feats.Add(new Feat { Id = otherTemplateId, Name = "Untrained", BaseName = "Untrained" });

					// The OPENING attack: +30% damage (a whole percent) and +400 crit rating.
					ctx.FeatComboAttackModifiers.Add(new FeatComboAttackModifier
					{
						FeatId = templateId, Sequence = 0, ComboId = comboId, AttackSlot = ComboAttackSlot.Opening, DpsPercent = 30, CriticalChanceRating = 400, EffectKind = 53250
					});
					// The FINISHING attack of the same combo, on the same feat - a second row, not a merge.
					ctx.FeatComboAttackModifiers.Add(new FeatComboAttackModifier
					{
						FeatId = templateId, Sequence = 1, ComboId = comboId, AttackSlot = ComboAttackSlot.Finishing, DpsPercent = 5, EffectKind = 53250
					});
					// The WHOLE-combo axis, in hundredths: +20.00%.
					ctx.FeatComboModifiers.Add(new FeatComboModifier { FeatId = templateId, Sequence = 0, ComboId = comboId, DamagePercentHundredths = 2000, EffectKind = 53209 });
					// An untrained feat's modifier on another combo: it must not leak in.
					ctx.FeatComboModifiers.Add(new FeatComboModifier { FeatId = otherTemplateId, Sequence = 0, ComboId = otherComboId, DamagePercentHundredths = 9900, EffectKind = 53209 });
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var bonuses = await repo.GetCharacterComboBonuses(characterId);

				var bonus = Assert.Single(bonuses).Value;
				Assert.Equal(3000, bonus.OpeningDamagePercentHundredths); // 30% -> hundredths
				Assert.Equal(400, bonus.OpeningCriticalRating);
				Assert.Equal(500, bonus.FinishingDamagePercentHundredths);
				Assert.Equal(0, bonus.FinishingCriticalRating);
				Assert.Equal(2000, bonus.SpellDamagePercentHundredths); // already hundredths
				Assert.DoesNotContain(otherComboId, bonuses.Keys);

				// A step that is neither the opener nor the finisher takes nothing; a one-step combo is both.
				Assert.Equal(0, bonus.SwingDamagePercentHundredths(false, false));
				Assert.Equal(3500, bonus.SwingDamagePercentHundredths(true, true));
				Assert.Equal(400, bonus.CriticalRating(true, true));
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		// #835: an ambient spawn-area pool builds its creature from the TYPE, so the type's APPEARANCE navigations have
		// to come back populated. Lazy loading is off by design, so a missing Include is not an error - the collection
		// simply arrives EMPTY and the spawn body goes out with no morph table and no worn equipment. That is exactly
		// how every ambient NPC in the world ended up naked, and how it STAYED naked after the type rows were filled
		// in: the rows existed, the query never loaded them. Asserting the navigations are non-empty is the only thing
		// that catches it - a row count would have passed throughout.
		[Fact]
		public async Task GetResolvedSpawnAreas_LoadsTheTypesAppearance_NotJustItsStatCore()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-areas-{Guid.NewGuid():N}.db");
			try
			{
				const uint templateId = 8848; // Poacher, the reported creature
				const uint itemId = 189907; // the pirate sailor chest it wears
				var (repo, factory) = NewRepo(dbPath);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Maps.Add(new Map
					{
						Id = Map.TortageBeachId,
						Name = "Tortage Beach"
					});
					ctx.Items.Add(new Item
					{
						Id = itemId,
						Name = "NPC_Pirate_sailor_chest",
						Hash = 1497905484,
						Key = new ProtoDecryptionKey
						{
							Key1 = 1,
							Key2 = 2,
							Key3 = 3,
							Key4 = 4
						}
					});
					ctx.NpcTemplates.Add(new NpcTemplate
					{
						TemplateId = templateId,
						Name = "Poacher",
						StatCore = new NpcStatCore
						{
							NpcTemplateId = templateId,
							Level = 5,
							IirHeader = 0xFA015807
						},
						Morphs =
						[
							new NpcMorph
							{
								NpcTemplateId = templateId, Ordinal = 0, MorphId = 324, Value = 0.918f
							}
						],
						Equipment =
						[
							new NpcEquipment
							{
								NpcTemplateId = templateId, Pos = 5, Template = itemId, Level = 1
							}
						]
					});

					var area = new NpcSpawnArea
					{
						MapId = Map.TortageBeachId,
						NpcTemplateId = templateId,
						GroupOrdinal = 0,
						MaxCount = 8,
						Category = "Name",
						GroupKey = "Poacher1",
						GroupName = "Poacher1"
					};
					area.Points.Add(new NpcSpawnAreaPoint
					{
						PointIndex = 0, PositionX = 1, PositionY = 2, PositionZ = 3
					});
					ctx.NpcSpawnAreas.Add(area);
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var areas = await repo.GetResolvedSpawnAreas(Map.TortageBeachId);

				var loaded = Assert.Single(areas);
				Assert.NotEmpty(loaded.Points); // the pool's spawn points, and the reason this query is a split one
				var type = loaded.NpcTemplate;
				Assert.NotNull(type);
				Assert.NotNull(type!.StatCore);
				Assert.NotEmpty(type.Morphs); // the hair/face table - empty here means a naked, featureless spawn body
				var worn = Assert.Single(type.Equipment);
				Assert.NotNull(worn.Item); // the builder reads the worn item's identity THROUGH this navigation
				Assert.Equal("NPC_Pirate_sailor_chest", worn.Item.Name);
			}
			finally
			{
				TestDb.Delete(dbPath);
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
