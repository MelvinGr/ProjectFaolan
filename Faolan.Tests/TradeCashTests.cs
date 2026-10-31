using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Economy;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// Cash offered in a trade moves the two players' live wallets, not only their rows. Every other money path (loot,
	// shop, quest reward, mail) adds to the session's wallet and writes the result, so a trade that moved only the
	// rows was undone by the next of those writes: the giver got the money back and the receiver lost it.
	public class TradeCashTests
	{
		private const uint Giver = 0x01000001;
		private const uint Receiver = 0x01000002;

		[Fact]
		public async Task Commit_MovesTheLiveWalletsAndTheRows()
		{
			var (options, path) = TestDb.NewSeededDatabase(Giver);
			try
			{
				await using (var ctx = new DatabaseContext(options))
				{
					var seeded = await ctx.Characters.SingleAsync(TestContext.Current.CancellationToken);
					ctx.Characters.Add(new Character
					{
						Id = Receiver,
						AccountId = seeded.AccountId,
						RealmId = seeded.RealmId,
						MapId = seeded.MapId,
						Name = "Receiver",
						Class = seeded.Class,
						Race = seeded.Race,
						Sex = seeded.Sex
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
					await ctx.Characters.ExecuteUpdateAsync(s => s.SetProperty(c => c.Money, 5000ul), TestContext.Current.CancellationToken);
				}

				var (connections, giver, receiver) = Players(5000, 5000);
				var outbound = new RecordingTradeOutbound();
				var trade = new TradeService(new DatabaseRepository(new TestDb.ContextFactory(options)), connections, NullLogger.Instance, outbound);

				await trade.Request(Giver, Receiver);
				await trade.Start(Receiver, Giver);
				await trade.SetCash(Giver, 1000);
				await trade.Accept(Giver);
				await trade.Accept(Receiver);

				Assert.Equal(4000ul, giver.Money);
				Assert.Equal(6000ul, receiver.Money);
				Assert.Equal([(Giver, 4000ul), (Receiver, 6000ul)], outbound.Wallets);
				await using (var ctx = new DatabaseContext(options))
				{
					Assert.Equal(4000ul, (await ctx.Characters.SingleAsync(c => c.Id == Giver, TestContext.Current.CancellationToken)).Money);
					Assert.Equal(6000ul, (await ctx.Characters.SingleAsync(c => c.Id == Receiver, TestContext.Current.CancellationToken)).Money);
				}

				// The trade is over: a second accept finds nothing to commit and moves nothing again.
				await trade.Accept(Receiver);
				Assert.Equal(4000ul, giver.Money);
				Assert.Equal(2, outbound.Wallets.Count);
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		[Fact]
		public async Task SetCash_IsClampedToTheLiveWallet()
		{
			var (connections, _, _) = Players(300, 0);
			var outbound = new RecordingTradeOutbound();
			var trade = new TradeService(null!, connections, NullLogger.Instance, outbound);

			await trade.Request(Giver, Receiver);
			await trade.Start(Receiver, Giver);
			await trade.SetCash(Giver, 1000);

			Assert.Equal([(Receiver, 300ul)], outbound.PartnerCash);
		}

		private static (ConnectionRegistry Connections, Character Giver, Character Receiver) Players(ulong giverMoney, ulong receiverMoney)
		{
			var connections = new ConnectionRegistry();
			var giver = new RecordingNetworkClient(Giver);
			var receiver = new RecordingNetworkClient(Receiver);
			giver.Session.Character.Money = giverMoney;
			receiver.Session.Character.Money = receiverMoney;
			connections.Add(giver);
			connections.Add(receiver);
			return (connections, giver.Session.Character, receiver.Session.Character);
		}

		private sealed class RecordingTradeOutbound : ITradeOutbound
		{
			public List<(uint, ulong)> PartnerCash { get; } = [];
			public List<(uint, ulong)> Wallets { get; } = [];

			public Task SendTradeRequest(uint targetId, uint requesterId) => Task.CompletedTask;
			public Task SendTradeStarted(uint recipientId, uint partnerId) => Task.CompletedTask;

			public Task SendPartnerCash(uint recipientId, ulong tin)
			{
				PartnerCash.Add((recipientId, tin));
				return Task.CompletedTask;
			}

			public Task SendTradeItem(uint recipientId, ItemInstance offered, uint windowSlot, uint count, bool mine) => Task.CompletedTask;
			public Task SendTradeItemRemoved(uint recipientId, uint slot, bool mine) => Task.CompletedTask;
			public Task SendAcceptAck(uint recipientId) => Task.CompletedTask;
			public Task SendPartnerAccepted(uint recipientId, uint partnerId) => Task.CompletedTask;
			public Task SendTradeCompleted(uint recipientId) => Task.CompletedTask;

			public Task SendWalletUpdate(uint characterId, ulong balanceTin)
			{
				Wallets.Add((characterId, balanceTin));
				return Task.CompletedTask;
			}

			public Task SendItemRemoved(uint characterId, uint slot) => Task.CompletedTask;
			public Task<int?> GrantItemToCharacter(uint characterId, uint itemTemplateId, uint count) => Task.FromResult<int?>(null);
			public Task<int?> TransferItemToCharacter(uint characterId, ItemInstance instance) => Task.FromResult<int?>(null);
		}
	}
}
