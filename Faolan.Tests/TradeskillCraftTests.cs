using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Economy;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// Crafting, against the one captured craft: NecroL80_mage-necromancer_login_gameplay_annotated_5, 18:45:25-18:45:30.
	//
	// The capture is unusually complete because the player narrated it and linked the item in /say before and after.
	// Its s2c completion names the recipe ID (DispatchTradeskillComplete 0x0000000F = recipe 15) and its removals name
	// the quantities, so the whole transaction is checkable against our own shipped rows rather than against a
	// re-implementation of it:
	//
	//   recipe 15 = Plain Hilt Piece x1 + Copper x4 + Silver x4 -> Bloodsilver Khopesh
	//   removals  = bag pos 19 cleared (the hilt), resource pos 13 cleared (copper 4 -> 0),
	//               resource pos 10 StackSize 2 (silver 6 -> 2)
	public class TradeskillCraftTests : IDisposable
	{
		private const uint Crafter = 0x0802E5D4; // NecroL80, the character id the captured craft is addressed to
		private const uint RecipeId = 15;
		private const uint PlainHiltPiece = 3987673;
		private const uint Copper = 161197;
		private const uint Silver = 161202;
		private const uint BloodsilverKhopesh = 157123;

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);
		}

		// The shipped catalog has to still be the capture's recipe. This is the gate that would catch a re-harvest
		// renumbering the recipes or dropping a quantity - everything else here is only meaningful if this holds.
		[Fact]
		public async Task Recipe15_IsTheCapturedBloodsilverKhopeshCraft()
		{
			await using var ctx = new DatabaseContext(TestDb.ReadOnly(TestPaths.FaolanDb));
			var recipe = await ctx.Recipes.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == RecipeId, TestContext.Current.CancellationToken);
			Assert.NotNull(recipe);

			var ingredients = recipe!.Items.Where(i => i.Role == RecipeItemRole.Ingredient).OrderBy(i => i.Position).Select(i => (i.ItemId, i.Trailing)).ToArray();
			Assert.Equal([(PlainHiltPiece, 1u), (Copper, 4u), (Silver, 4u)], ingredients);

			var result = Assert.Single(recipe.Items, i => i.Role == RecipeItemRole.Result);
			Assert.Equal(BloodsilverKhopesh, result.ItemId);
		}

		// The craft itself: the product arrives carrying its crafter, and the materials go exactly as the capture's
		// removals show - the two 4-stacks paid in full, the 6-stack left holding 2.
		[Fact]
		public async Task Craft_ConsumesTheRecipe_AndGrantsTheProductWithItsCrafter()
		{
			var h = await Harness(silverHeld: 6);

			Assert.Equal(RecipeId, await h.Service.Craft(h.Client, RecipeId, 1));

			Assert.Equal([(BloodsilverKhopesh, 1u, "NecroL80")], h.Outbound.Granted);

			// Order is the recipe's, not something to assert on - what matters is that each material is taken once,
			// for the quantity the recipe states.
			Assert.Equal([(PlainHiltPiece, 1u), (Copper, 4u), (Silver, 4u)], h.Outbound.Taken.OrderBy(t => t.Count).ThenBy(t => t.Item).ToArray());
		}

		// A player one silver short must keep everything. Taking as we go would leave them having paid the copper for
		// nothing, which is the failure the check-everything-first ordering exists to prevent.
		[Fact]
		public async Task Craft_ShortOnOneMaterial_TakesNothingAndGrantsNothing()
		{
			var h = await Harness(silverHeld: 3);

			Assert.Null(await h.Service.Craft(h.Client, RecipeId, 1));

			Assert.Empty(h.Outbound.Granted);
			Assert.Empty(h.Outbound.Taken);
		}

		// A full bag refuses the craft with the materials still in the player's hands - the same grant-before-consume
		// order the shop's buy path and the harvest bag-full refusal use.
		[Fact]
		public async Task Craft_WithNowhereToPutTheProduct_KeepsTheMaterials()
		{
			var h = await Harness(silverHeld: 6);
			h.Outbound.BagFull = true;

			Assert.Null(await h.Service.Craft(h.Client, RecipeId, 1));

			Assert.Empty(h.Outbound.Taken);
		}

		// Quantities scale with the requested count, and they are summed per template before the check - so asking for
		// two of a recipe needs two lots of every material, not one.
		[Fact]
		public async Task Craft_OfTwo_NeedsTwiceTheMaterials()
		{
			var h = await Harness(silverHeld: 6);

			// 6 silver covers one craft (4) but not two (8).
			Assert.Null(await h.Service.Craft(h.Client, RecipeId, 2));
			Assert.Empty(h.Outbound.Taken);
		}

		// An unknown recipe id is refused rather than crafted into nothing.
		[Fact]
		public async Task Craft_OfAnUnknownRecipe_IsRefused()
		{
			var h = await Harness(silverHeld: 6);

			Assert.Null(await h.Service.Craft(h.Client, 999999, 1));
			Assert.Empty(h.Outbound.Granted);
		}

		// Retail paces the transaction. The client's own duration getter (`FUN_0114bf50`) reads the recipe
		// record's `+0x24` for a craft - the mid-header word the codec validates - and hardcodes 500 for a gem. Both
		// are 5.00 s, and both are confirmed server-side on the wire (5.07 s craft, 4.98 s socket).
		[Fact]
		public void StagedTradeskill_IsDueFiveSecondsOn_ForBothOperations()
		{
			Assert.Equal(500u, TradeskillReplyCodec.CraftDurationCentiseconds);
			Assert.Equal(500u, TradeskillService.GemSocketCentiseconds);
		}

		// Nothing happens until the bar has run out: staging alone must not grant, take or complete anything, or the
		// pacing would only be a delayed duplicate of an instant craft.
		[Fact]
		public async Task StagedCraft_GrantsNothingBeforeItsDeadline()
		{
			var h = await Harness(silverHeld: 6);
			var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

			var due = h.Service.Stage(h.Client, TradeskillCommands.Craft(RecipeId, 1), now);
			Assert.Equal(now.AddSeconds(5), due);

			await h.Service.CompleteDue(now.AddSeconds(4.9));
			Assert.Empty(h.Outbound.Granted);
			Assert.Empty(h.Outbound.Completed);

			await h.Service.CompleteDue(now.AddSeconds(5));
			Assert.Equal([(BloodsilverKhopesh, 1u, "NecroL80")], h.Outbound.Granted);
			Assert.Equal([RecipeId], h.Outbound.Completed);
		}

		// The completion runs once. The tick calls CompleteDue ten times a second, so an entry that survived its own
		// completion would re-craft for as long as the player stayed logged in.
		[Fact]
		public async Task CompletedCraft_DoesNotRunAgainOnTheNextTick()
		{
			var h = await Harness(silverHeld: 6);
			var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

			h.Service.Stage(h.Client, TradeskillCommands.Craft(RecipeId, 1), now);
			await h.Service.CompleteDue(now.AddSeconds(5));
			await h.Service.CompleteDue(now.AddSeconds(5.1));

			Assert.Single(h.Outbound.Completed);
		}

		// A refusal stays silent, five seconds late as it is now: retail has no failure form here, and a completion
		// over a craft that did not happen tells the client to close its window on an item it never received.
		[Fact]
		public async Task StagedCraft_ThatCannotBeAfforded_CompletesSilently()
		{
			var h = await Harness(silverHeld: 3);
			var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

			h.Service.Stage(h.Client, TradeskillCommands.Craft(RecipeId, 1), now);
			await h.Service.CompleteDue(now.AddSeconds(5));

			Assert.Empty(h.Outbound.Granted);
			Assert.Empty(h.Outbound.Taken);
			Assert.Empty(h.Outbound.Completed);
		}

		// A player who logs out mid-bar takes nothing with them and is granted nothing: the staged entry is dropped,
		// so the tick after their disconnect must find no work.
		[Fact]
		public async Task ForgottenCraft_NeverCompletes()
		{
			var h = await Harness(silverHeld: 6);
			var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

			h.Service.Stage(h.Client, TradeskillCommands.Craft(RecipeId, 1), now);
			Assert.Equal(1, h.Service.Forget(h.Client));

			await h.Service.CompleteDue(now.AddSeconds(5));
			Assert.Empty(h.Outbound.Granted);
			Assert.Empty(h.Outbound.Completed);
		}

		private async Task<Harnessed> Harness(uint silverHeld)
		{
			var (options, path) = TestDb.NewSeededDatabase(Crafter);
			_databases.Add(path);

			await using (var ctx = new DatabaseContext(options))
			{
				foreach (var (id, name) in new[] { (PlainHiltPiece, "Plain Hilt Piece"), (Copper, "Copper"), (Silver, "Silver"), (BloodsilverKhopesh, "Bloodsilver Khopesh") })
					ctx.Items.Add(new Item { Id = id, Name = name, Level = 40, Key = new ProtoDecryptionKey() });

				ctx.Recipes.Add(new Recipe
				{
					Id = RecipeId,
					Items =
					[
						new RecipeItem { RecipeId = RecipeId, Role = RecipeItemRole.Ingredient, Position = 0, ItemId = PlainHiltPiece, Trailing = 1 },
						new RecipeItem { RecipeId = RecipeId, Role = RecipeItemRole.Ingredient, Position = 1, ItemId = Copper, Trailing = 4 },
						new RecipeItem { RecipeId = RecipeId, Role = RecipeItemRole.Ingredient, Position = 2, ItemId = Silver, Trailing = 4 },
						new RecipeItem { RecipeId = RecipeId, Role = RecipeItemRole.Result, Position = 0, ItemId = BloodsilverKhopesh, Trailing = 0 }
					]
				});

				// Where the capture had them: the hilt in the bag, the ores in the resource container.
				ctx.ItemInstances.Add(new ItemInstance { OwnerCharacterId = Crafter, Container = ContainerKind.Bag, Slot = 19, TemplateId = PlainHiltPiece, Count = 1 });
				ctx.ItemInstances.Add(new ItemInstance { OwnerCharacterId = Crafter, Container = ContainerKind.Resource, Slot = 13, TemplateId = Copper, Count = 4 });
				ctx.ItemInstances.Add(new ItemInstance { OwnerCharacterId = Crafter, Container = ContainerKind.Resource, Slot = 10, TemplateId = Silver, Count = silverHeld });
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new Factory(options));
			var client = new RecordingNetworkClient(Crafter);
			client.Session.Character = (await database.GetCharacter(Crafter))!;
			client.Session.Character.Name = "NecroL80";

			var outbound = new RecordingEconomyOutbound();
			return new Harnessed(new TradeskillService(database, database, database, outbound, null!, null!, NullLogger.Instance), client, outbound);
		}

		private sealed record Harnessed(TradeskillService Service, RecordingNetworkClient Client, RecordingEconomyOutbound Outbound);

		// Records what the craft asked the wire/inventory side to do, so the test asserts on the transaction rather
		// than on bytes: which template was granted to whom, and what was taken.
		private sealed class RecordingEconomyOutbound : NoOpEconomyOutbound
		{
			public bool BagFull { get; set; }
			public List<uint> Completed { get; } = [];
			public List<(uint Item, uint Count, string? Creator)> Granted { get; } = [];
			public List<(uint TargetSlot, uint SocketIndex, uint GemTemplate, uint GemSlot)> Socketed { get; } = [];
			public List<(uint Item, uint Count)> Taken { get; } = [];

			public override Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null, string? creatorName = null)
			{
				if (BagFull)
					return Task.FromResult<int?>(null);

				Granted.Add((itemTemplateId, count, creatorName));
				return Task.FromResult<int?>(0);
			}

			public override Task SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot)
			{
				Socketed.Add((targetSlot, socket.SocketIndex, socket.GemItemId, gemSlot));
				return Task.CompletedTask;
			}

			public override Task SendTradeskillComplete(INetworkClient client, uint recipeId)
			{
				Completed.Add(recipeId);
				return Task.CompletedTask;
			}

			public override Task<uint> TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count)
			{
				Taken.Add((itemTemplateId, count));
				return Task.FromResult(count);
			}
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new DatabaseContext(options);
			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new DatabaseContext(options));
		}
	}
}
