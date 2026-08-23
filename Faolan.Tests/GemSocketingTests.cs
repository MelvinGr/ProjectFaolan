using System;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #754: putting a gem into an item's socket.
	//
	// The whole transaction is pinned by one captured socketing -
	// NecroL80_mage-necromancer_login_gameplay_annotated_8 fr8215-8248, with the player linking the item in /say
	// straight afterwards ("after [Fine Steelsilk Armbands]"):
	//
	//   c2s  Tradeskill (0x27), tail  02 | 00000068 0802e5d4 00000018 | 00000068 0802e5d4 00000017
	//   s2c  DispatchAddToItemSlot  bag slot 24, socket 0, the gem's identity
	//   s2c  DispatchRemoveItem     bag slot 23  (the gem leaving)
	//
	// ⚠️ Socketing CONSUMES the gem, so the refusals matter as much as the success and are tested as such.
	public class GemSocketingTests : IDisposable
	{
		private const uint Owner = 0x0802E5D4;
		private const uint HostTemplate = 220208; // Fine Steelsilk Armbands
		private const uint GemTemplate = 3983088; // Sanguine Radiant Sapphire
		private const int HostSlot = 24;
		private const int GemSlot = 23;

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);
		}

		// The c2s tail, byte-exact against the capture - and the CRAFT form beside it, because the whole point of
		// the decode is that one command carries both and the leading byte is what tells them apart.
		[Fact]
		public void TheTradeskillTail_RoundTripsBothOperations()
		{
			const string socketTail = "02000000680802e5d400000018000000680802e5d400000017";
			const string craftTail = "010000000f00000001";

			var socket = TradeskillCommandCodec.Parse(Convert.FromHexString(socketTail));
			Assert.Equal(TradeskillAction.Socket, socket.Action);
			Assert.Equal(new InventoryAddress(ContainerKind.Bag, Owner, 24), socket.Target);
			Assert.Equal(new InventoryAddress(ContainerKind.Bag, Owner, 23), socket.Gem);
			Assert.Equal(socketTail, Convert.ToHexStringLower(TradeskillCommandCodec.Build(socket)));

			var craft = TradeskillCommandCodec.Parse(Convert.FromHexString(craftTail));
			Assert.Equal(TradeskillAction.Craft, craft.Action);
			Assert.Equal(15u, craft.RecipeId);
			Assert.Equal(1u, craft.Count);
			Assert.Equal(craftTail, Convert.ToHexStringLower(TradeskillCommandCodec.Build(craft)));

			// Strict: an action the client never sends is a throw, not a silently-empty command.
			Assert.Throws<Faolan.Core.Extensions.CodecParseException>(() => TradeskillCommandCodec.Parse(Convert.FromHexString("03")));
		}

		// The reply, byte-exact against retail's own 84 bytes. This is what identifies the layout: the trailing 68
		// bytes are a ProtoAcgItem's fields with the protobuf framing taken off, in the schema's field order.
		[Fact]
		public void TheAddToItemSlotReply_IsByteExactAgainstTheCapture()
		{
			const string captured =
				"000000680802e5d4000000180000000" + "0" +
				"003cc6f0003cc6f10000000000000050" + "00000000" +
				"72e2660ee30229bdb2019308" + "0dc6aefd" +
				"1b3e7a1daf9866bb07a736b7" + "4e022e23" +
				"b290805c29e627cab290805c" + "29e627ca";

			var parsed = AddToItemSlotCodec.Parse(Convert.FromHexString(captured));

			Assert.Equal(ContainerKind.Bag, parsed.Container);
			Assert.Equal(24u, parsed.Slot);
			Assert.Equal(0u, parsed.SocketIndex);
			Assert.Equal(GemTemplate, parsed.Template1);
			Assert.Equal(GemTemplate + 1, parsed.Template2); // the #752 level pair
			Assert.Equal(0u, parsed.Template3);
			Assert.Equal(80u, parsed.Level);
			Assert.Equal(0u, parsed.Hash); // a socketed gem carries no token

			Assert.Equal(captured, Convert.ToHexStringLower(AddToItemSlotCodec.Build(parsed)));
		}

		[Fact]
		public async Task ASocketedGem_IsConsumedAndTheClientIsTold()
		{
			var (service, client, outbound, database) = await Harness(hostSlots: 3);

			Assert.True(await service.Socket(client, Address(HostSlot), Address(GemSlot)));

			// The wire: the cell that received it, which socket, and the cell the gem left.
			var told = Assert.Single(outbound.Socketed);
			Assert.Equal(((uint)HostSlot, 0u, GemTemplate, (uint)GemSlot), told);

			// The rows: the socket exists and the gem is GONE - a socket without the removal would duplicate it.
			var host = await database.GetCharacterItemSlot(Owner, ContainerKind.Bag, HostSlot);
			Assert.Equal(GemTemplate, Assert.Single(host!.Sockets).GemItemId);
			Assert.Null(await database.GetCharacterItemSlot(Owner, ContainerKind.Bag, GemSlot));
		}

		// Sockets fill in order, and a second gem lands in the next free index rather than colliding with the first.
		[Fact]
		public async Task ASecondGem_TakesTheNextFreeSocket()
		{
			var (service, client, outbound, database) = await Harness(hostSlots: 3, secondGemSlot: 22);

			Assert.True(await service.Socket(client, Address(HostSlot), Address(GemSlot)));
			Assert.True(await service.Socket(client, Address(HostSlot), Address(22)));

			Assert.Equal([0u, 1u], outbound.Socketed.Select(x => x.SocketIndex).ToArray());
			var host = await database.GetCharacterItemSlot(Owner, ContainerKind.Bag, HostSlot);
			Assert.Equal([0u, 1u], host!.Sockets.Select(x => x.SocketIndex).Order().ToArray());
		}

		// THE ONE THAT MATTERS. Socketing destroys the gem, so an item with no sockets must refuse rather than eat
		// it - and refuse SILENTLY, which is what retail does for any rejected tradeskill.
		[Fact]
		public async Task AnItemWithNoSockets_RefusesAndKeepsTheGem()
		{
			var (service, client, outbound, database) = await Harness(hostSlots: 0);

			Assert.False(await service.Socket(client, Address(HostSlot), Address(GemSlot)));

			Assert.Empty(outbound.Socketed);
			Assert.NotNull(await database.GetCharacterItemSlot(Owner, ContainerKind.Bag, GemSlot));
		}

		// A full item refuses too - and "full" is measured against the item's own NumGemSlots, so a one-socket item
		// takes exactly one.
		[Fact]
		public async Task AFullItem_RefusesAndKeepsTheGem()
		{
			var (service, client, outbound, database) = await Harness(hostSlots: 1, secondGemSlot: 22);

			Assert.True(await service.Socket(client, Address(HostSlot), Address(GemSlot)));
			Assert.False(await service.Socket(client, Address(HostSlot), Address(22)));

			Assert.Single(outbound.Socketed);
			Assert.NotNull(await database.GetCharacterItemSlot(Owner, ContainerKind.Bag, 22));
		}

		// An empty cell on either side is a refusal, not a crash - a stale client can name a cell the player has
		// since emptied.
		[Fact]
		public async Task AnEmptyCell_Refuses()
		{
			var (service, client, outbound, _) = await Harness(hostSlots: 3);

			Assert.False(await service.Socket(client, Address(HostSlot), Address(2)));
			Assert.False(await service.Socket(client, Address(2), Address(GemSlot)));
			Assert.False(await service.Socket(client, Address(HostSlot), Address(HostSlot))); // itself
			Assert.Empty(outbound.Socketed);
		}

		private static InventoryAddress Address(int slot)
		{
			return new InventoryAddress(ContainerKind.Bag, Owner, (uint)slot);
		}

		private async Task<(TradeskillService Service, RecordingNetworkClient Client, Recorder Outbound, DatabaseRepository Database)> Harness(uint hostSlots, int? secondGemSlot = null)
		{
			var (options, path) = TestDb.NewSeededDatabase(Owner);
			_databases.Add(path);

			await using (var ctx = new DatabaseContext(options))
			{
				ctx.Items.Add(new Item { Id = HostTemplate, Name = "Fine Steelsilk Armbands", GemSlots = hostSlots, Key = new ProtoDecryptionKey() });
				ctx.Items.Add(new Item { Id = GemTemplate, Name = "Sanguine Radiant Sapphire", Level = 80, Key = new ProtoDecryptionKey() });
				ctx.ItemInstances.Add(new ItemInstance { OwnerCharacterId = Owner, Container = ContainerKind.Bag, Slot = HostSlot, TemplateId = HostTemplate, Count = 1 });
				ctx.ItemInstances.Add(new ItemInstance { OwnerCharacterId = Owner, Container = ContainerKind.Bag, Slot = GemSlot, TemplateId = GemTemplate, Count = 1 });
				if (secondGemSlot is { } second)
					ctx.ItemInstances.Add(new ItemInstance { OwnerCharacterId = Owner, Container = ContainerKind.Bag, Slot = second, TemplateId = GemTemplate, Count = 1 });

				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new TestDb.ContextFactory(options));
			var client = new RecordingNetworkClient(Owner);
			client.Session.Character = (await database.GetCharacter(Owner))!;
			client.Session.Character.Name = "NecroL80";

			var outbound = new Recorder();
			return (new TradeskillService(database, outbound, null!, null!, NullLogger.Instance), client, outbound, database);
		}

		// Records what a socketing asked the wire to say, so the test asserts on the TRANSACTION rather than bytes
		// (the bytes have their own golden above).
		private sealed class Recorder : NoOpEconomyOutbound
		{
			public List<(uint TargetSlot, uint SocketIndex, uint GemTemplate, uint GemSlot)> Socketed { get; } = [];

			public override Task SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot)
			{
				Socketed.Add((targetSlot, socket.SocketIndex, socket.GemItemId, gemSlot));
				return Task.CompletedTask;
			}
		}
	}
}
