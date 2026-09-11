using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Only the active weapon set's two slots feed the gear-stat aggregation, because a character can carry both a
	// melee and a ranged set at once and counting all four would double-count. Armour + jewellery always apply; the
	// weapon set is selected by the drawn loadout.
	//
	// The worn set is item instances in container 101, so the selection is a slot filter in the repository query and
	// the gate has to go through the DB to exercise it at all. An in-memory stand-in could agree with itself while the
	// query it stands for disagreed.
	public class CharacterEquipmentStatSlotsTests
	{
		// One item in every stat-bearing armour/jewellery slot, ids 1..12 so each slot is identifiable, plus both
		// weapon loadouts: set 1 (MainWeapon 6 / OffHandWeapon 8) at 100/101 and set 2 (RangedWeapon 20 / Ammo 21) at
		// 200/201.
		private static readonly (CharacterEquipmentSlot Slot, uint Template)[] FullyGeared =
		[
			(CharacterEquipmentSlot.Neck, 1), (CharacterEquipmentSlot.Head, 2), (CharacterEquipmentSlot.Chest, 3), (CharacterEquipmentSlot.Belt, 4),
			(CharacterEquipmentSlot.LeftRing, 5), (CharacterEquipmentSlot.RightRing, 6), (CharacterEquipmentSlot.Cape, 7), (CharacterEquipmentSlot.Shoulder, 8),
			(CharacterEquipmentSlot.Boots, 9), (CharacterEquipmentSlot.Hands, 10), (CharacterEquipmentSlot.Legs, 11), (CharacterEquipmentSlot.Wrist, 12),
			(CharacterEquipmentSlot.MainWeapon, 100), (CharacterEquipmentSlot.OffHandWeapon, 101),
			(CharacterEquipmentSlot.RangedWeapon, 200), (CharacterEquipmentSlot.Ammo, 201)
		];

		private static readonly uint[] Armour = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

		[Fact]
		public async Task EmptyWeaponSlots_AreSkipped()
		{
			// A character with only a set-1 weapon equipped, drawing set 1: its one weapon plus the chest piece.
			await WithWorn([(CharacterEquipmentSlot.Chest, 3), (CharacterEquipmentSlot.MainWeapon, 100)], async repo =>
			{
				Assert.Equal(new uint[] { 3, 100 }, (await repo.GetEquippedStatItems(CharacterId, WeaponSet.Primary)).Select(i => i.Id).Order().ToArray());
				// Drawing a second set it does not own yields no weapon ids (nothing to double-count, nothing crashes).
				Assert.Equal(new uint[] { 3 }, (await repo.GetEquippedStatItems(CharacterId, WeaponSet.Secondary)).Select(i => i.Id).ToArray());
			});
		}

		[Fact]
		public async Task PrimarySet_AddsSet1Weapons_ExcludesSet2()
		{
			await WithWorn(FullyGeared, async repo =>
			{
				var ids = (await repo.GetEquippedStatItems(CharacterId, WeaponSet.Primary)).Select(i => i.Id).ToHashSet();

				Assert.Contains(100u, ids); // set-1 MainWeapon
				Assert.Contains(101u, ids); // set-1 OffHandWeapon
				Assert.DoesNotContain(200u, ids); // set-2 ranged not counted
				Assert.DoesNotContain(201u, ids); // set-2 ammo not counted
				Assert.All(Armour, a => Assert.Contains(a, ids));
			});
		}

		[Fact]
		public async Task SecondarySet_AddsSet2Weapons_ExcludesSet1()
		{
			await WithWorn(FullyGeared, async repo =>
			{
				var ids = (await repo.GetEquippedStatItems(CharacterId, WeaponSet.Secondary)).Select(i => i.Id).ToHashSet();

				Assert.Contains(200u, ids); // set-2 RangedWeapon (often a bow/crossbow)
				Assert.Contains(201u, ids); // set-2 Ammo (often a quiver)
				Assert.DoesNotContain(100u, ids); // set-1 main not counted
				Assert.DoesNotContain(101u, ids); // set-1 off not counted
				Assert.All(Armour, a => Assert.Contains(a, ids));
			});
		}

		// The vanity slots and the bag containers are not stat-bearing: vanity is appearance only and a bag's capacity is
		// a bag stat, not a combat one. They are worn instances like any other, so nothing but the slot filter keeps them
		// out - which is exactly the sort of thing a column-based model got for free and a row-based one has to state.
		[Fact]
		public async Task VanityAndBagSlots_AreNotStatBearing()
		{
			await WithWorn([
				(CharacterEquipmentSlot.Chest, 3), (CharacterEquipmentSlot.VanityChest, 500), (CharacterEquipmentSlot.VanityHead, 501),
				(CharacterEquipmentSlot.Bag, 502), (CharacterEquipmentSlot.ResourceBag, 503)
			], async repo => Assert.Equal(new uint[] { 3 }, (await repo.GetEquippedStatItems(CharacterId, WeaponSet.Primary)).Select(i => i.Id).ToArray()));
		}

		private const uint CharacterId = 7;

		// A writable copy of the shipped fixture rather than a bare EnsureCreated: Character hangs off Account, Realm and
		// Map, and the fixture already carries the seeded realm/account and the map rows.
		private static async Task WithWorn(IEnumerable<(CharacterEquipmentSlot Slot, uint Template)> worn, Func<DatabaseRepository, Task> assert)
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-equip-slots-{Guid.NewGuid():N}.db");
			File.Copy(TestDb.GameDatabase, path);
			try
			{
				var options = TestDb.Options(path);
				await using (var ctx = new DatabaseContext(options))
				{
					var realm = await ctx.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken);
					var account = await ctx.Accounts.Select(a => a.Id).FirstAsync(TestContext.Current.CancellationToken);
					ctx.Characters.Add(new Character
					{
						Id = CharacterId, Name = "Slottest", Class = ConanClass.Assassin, Level = 20, Race = Race.Stygian,
						Sex = Sex.Female, RealmId = realm, AccountId = account
					});

					foreach (var (slot, template) in worn)
					{
						// ItemInstance.TemplateId FKs Items, so each worn template needs a catalog row.
						ctx.Items.Add(new Item
						{
							Id = template,
							Name = $"Test {slot}"
						});
						ctx.ItemInstances.Add(new ItemInstance
						{
							OwnerCharacterId = CharacterId,
							Container = ContainerKind.Equipped,
							Slot = (int)slot,
							TemplateId = template
						});
					}

					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				await assert(new DatabaseRepository(new Factory(options)));
			}
			finally
			{
				TestDb.Delete(path);
			}
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
