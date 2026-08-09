using System;
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
	// A player trade HANDS an item over, and #750 is what makes that expressible: the instance changes owner, so its
	// crafter, its stack and its socketed GEMS come with it because they were never separable from the row.
	//
	// This is the gate on the bug #750 nearly shipped past. `TradeService.Exchange` granted a FRESH instance from the
	// template and then cleared the giver's cell - which deleted the giver's row, cascading its sockets, and handed the
	// receiver a gemless copy with no crafter. Nothing revealed it because before #750 an item had no per-instance state
	// to lose; and the offer window is built from the real instance, so it was already advertising gems the exchange
	// would not deliver.
	public class ItemTransferTests
	{
		private const uint Giver = 7;
		private const uint Receiver = 8;

		[Fact]
		public async Task Transfer_KeepsTheInstanceWithItsCrafterAndGems()
		{
			await WithTwoCharacters(async (repo, instanceId) =>
			{
				var moved = await repo.TransferCharacterItem(instanceId, Receiver, ContainerKind.Bag, 3);

				Assert.NotNull(moved);
				Assert.Equal(instanceId, moved!.Id); // the SAME item, not a copy

				// The receiver holds it, with everything that was attached to it.
				var held = Assert.Single(await repo.GetCharacterItems(Receiver));
				Assert.Equal((instanceId, "NecroL80", 3), (held.Id, held.CreatorName, held.Slot));
				Assert.Equal(new uint[] { 0, 1 }, held.Sockets.OrderBy(x => x.SocketIndex).Select(x => x.SocketIndex));

				// And the giver no longer does - one row moved, nothing was minted or deleted.
				Assert.Empty(await repo.GetCharacterItems(Giver));
			});
		}

		// An OCCUPIED destination is refused rather than overwritten: the caller found a free cell, so an occupied one
		// means the receiver's inventory changed underneath us, and the alternative to refusing is destroying whatever is
		// in it. The item must still be with the giver afterwards - a trade that fails has to fail safely.
		[Fact]
		public async Task Transfer_IntoAnOccupiedCell_IsRefused_AndTheItemStaysPut()
		{
			await WithTwoCharacters(async (repo, instanceId) =>
			{
				await repo.SetCharacterItemSlot(Receiver, ContainerKind.Bag, 3, 4243);

				Assert.Null(await repo.TransferCharacterItem(instanceId, Receiver, ContainerKind.Bag, 3));

				var stillGivers = Assert.Single(await repo.GetCharacterItems(Giver));
				Assert.Equal(instanceId, stillGivers.Id);
				Assert.Single(await repo.GetCharacterItems(Receiver)); // only what was already there
			});
		}

		[Fact]
		public async Task Transfer_OfAnInstanceThatIsGone_IsRefused()
		{
			await WithTwoCharacters(async (repo, instanceId) =>
			{
				await repo.SetCharacterItemSlot(Giver, ContainerKind.Bag, 1, null); // the giver dropped it mid-trade

				Assert.Null(await repo.TransferCharacterItem(instanceId, Receiver, ContainerKind.Bag, 3));
				Assert.Empty(await repo.GetCharacterItems(Receiver));
			});
		}

		// Two characters, and the giver holds a CRAFTED, twice-GEMMED item in bag cell 1. A WRITABLE copy of the shipped
		// fixture rather than a bare EnsureCreated: Character hangs off Account, Realm and Map.
		private static async Task WithTwoCharacters(Func<DatabaseRepository, uint, Task> assert)
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-transfer-{Guid.NewGuid():N}.db");
			File.Copy(TestDb.GameDatabase, path);
			try
			{
				var options = TestDb.Options(path);
				uint instanceId;
				await using (var ctx = new DatabaseContext(options))
				{
					var realm = await ctx.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken);
					var account = await ctx.Accounts.Select(a => a.Id).FirstAsync(TestContext.Current.CancellationToken);
					foreach (var (id, name) in new[] { (Giver, "Giver"), (Receiver, "Receiver") })
					{
						ctx.Characters.Add(new Character
						{
							Id = id, Name = name, Class = ConanClass.Assassin, Level = 20, Race = Race.Stygian,
							Sex = Sex.Female, RealmId = realm, AccountId = account
						});
					}

					ctx.Items.Add(new Item { Id = 4242, Name = "Gemmed Khopesh" });
					ctx.Items.Add(new Item { Id = 4243, Name = "A Gem" });

					var traded = new ItemInstance
					{
						OwnerCharacterId = Giver,
						Container = ContainerKind.Bag,
						Slot = 1,
						TemplateId = 4242,
						CreatorName = "NecroL80"
					};
					traded.Sockets.Add(new ItemInstanceSocket { SocketIndex = 0, GemItemId = 4243 });
					traded.Sockets.Add(new ItemInstanceSocket { SocketIndex = 1, GemItemId = 4243 });
					ctx.ItemInstances.Add(traded);
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
					instanceId = traded.Id;
				}

				await assert(new DatabaseRepository(new Factory(options)), instanceId);
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
