using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.GameServer.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The pick-one-of-N quest reward exchange, server side. The codec is gated byte-exact against the capture in
	// QuestRewardCodecTests; what this gates is the policy around it - who may claim, how often, and what the offer is
	// built from.
	//
	// The idempotence case is the point. The quest completes exactly once, but the grant is driven by
	// QuestRewardSelectionIIR - an ordinary c2s packet naming its own quest id - so nothing stops a client sending it
	// twice or naming a quest it finished long ago. The reward is closed by the recorded answer, not by the completion.
	public class QuestRewardServiceTests : IDisposable
	{
		private const uint CharacterId = 4242;
		private const uint QuestId = 10928; // "The Trainer of Mages" - the captured offer
		private const uint Dagger = 27655;
		private const uint Longsword = 3145335;
		private const uint Staff = 3394679;

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);

			GC.SuppressFinalize(this);
		}

		[Fact]
		public async Task ACompletedQuestWithOptions_OffersThemInOrder()
		{
			var (service, client, outbound, _) = await Harness();

			await service.OfferOnCompletion(client, QuestId);

			var offer = Assert.Single(outbound.Offers);
			Assert.Equal(QuestId, offer.QuestId);
			Assert.Equal([Dagger, Longsword, Staff], offer.Options.ConvertAll(o => o.ItemTemplateId));

			// Each option must carry the item's real ProtoDecryptionKey - the client decrypts the template from its own
			// RDB with it, and a zero key renders the row as "No name".
			Assert.All(offer.Options, o => Assert.NotEqual(new byte[QuestRewardEntry.KeyLength], o.DecryptionKey));
		}

		[Fact]
		public async Task PickingAnOption_GrantsThatItemAndConfirms()
		{
			var (service, client, outbound, economy) = await Harness();

			Assert.True(await service.TryGrant(client, QuestId, 2));

			Assert.Equal([Staff], economy.Granted); // index 2 = the third option
			Assert.Equal([(QuestId, 2u)], outbound.Confirms);
		}

		// The guard the whole design turns on: the selection is replayable, so a second one must grant nothing.
		[Fact]
		public async Task ASecondSelection_GrantsNothingMore()
		{
			var (service, client, outbound, economy) = await Harness();

			Assert.True(await service.TryGrant(client, QuestId, 0));
			Assert.False(await service.TryGrant(client, QuestId, 1)); // replayed, and for a different option
			Assert.False(await service.TryGrant(client, QuestId, 0)); // replayed verbatim

			Assert.Equal([Dagger], economy.Granted);
			Assert.Single(outbound.Confirms);
		}

		// A client may name any quest id; only one it actually completed may be claimed.
		[Fact]
		public async Task AQuestTheCharacterHasNotCompleted_IsRefused()
		{
			var (service, client, _, economy) = await Harness(completed: false);

			Assert.False(await service.TryGrant(client, QuestId, 0));

			Assert.Empty(economy.Granted);
		}

		[Fact]
		public async Task AnIndexOutsideTheOffer_IsRefused()
		{
			var (service, client, _, economy) = await Harness();

			Assert.False(await service.TryGrant(client, QuestId, 7));

			Assert.Empty(economy.Granted);
		}

		// A quest with no options offers nothing - the ordinary case for almost every quest in the game.
		[Fact]
		public async Task AQuestWithNoOptions_OffersNothing()
		{
			var (service, client, outbound, _) = await Harness();

			await service.OfferOnCompletion(client, 12371);

			Assert.Empty(outbound.Offers);
		}

		// The XP the completion paid, recorded through the delegate seam - the whole reason that seam is a Func and
		// not a LevelUpService, which would need a WorldSimulation behind it to say the same thing.
		private readonly List<uint> _xpAwards = [];

		// A quest pays the experience and money its own record states - which are the numbers the client's
		// journal advertised on its `XP:` and `Money:` lines, established in-client. Anything else would
		// be a divergence the player can see on the panel they took the quest from.
		[Fact]
		public async Task CompletingAQuestThatStatesRewards_PaysBoth()
		{
			var (service, client, _, economy) = await Harness(xpReward: 5553, moneyReward: 111200);
			client.Session.Character.Money = 400;

			await service.GrantCompletionRewards(client, QuestId);

			Assert.Equal([5553u], _xpAwards);

			// Tin in the column, tin on the wire, and an absolute balance rather than a delta - so 400 + 111200.
			var cash = Assert.Single(economy.CashUpdates);
			Assert.Equal((111600ul, UpdateCashReason.Gain), cash);
			Assert.Equal(111600ul, client.Session.Character.Money);
		}

		// And nothing is invented where the record is silent, which is almost every quest: 21 of 5049 carry a money
		// reward. Retail pays experience on quests whose every reward slot is zero, but the magnitude is unmeasurable
		// from what we hold (`CharacterStatId.Exp` is on no stat record in the whole corpus), so a formula
		// here would be a guess shipped into five thousand quests. Paying nothing is the honest answer until a
		// capture says otherwise, and this is what holds that line.
		[Fact]
		public async Task CompletingAQuestThatStatesNoReward_PaysNothing()
		{
			var (service, client, _, economy) = await Harness();
			client.Session.Character.Money = 400;

			await service.GrantCompletionRewards(client, QuestId);

			Assert.Empty(_xpAwards);
			Assert.Empty(economy.CashUpdates);
			Assert.Equal(400ul, client.Session.Character.Money);
		}

		// The two halves are independent - a quest may pay experience only, which is the commoner shape of the pair
		// (the money carriers are a subset of the xp carriers).
		[Fact]
		public async Task AQuestThatPaysExperienceOnly_SendsNoWalletUpdate()
		{
			var (service, client, _, economy) = await Harness(xpReward: 715);

			await service.GrantCompletionRewards(client, QuestId);

			Assert.Equal([715u], _xpAwards);
			Assert.Empty(economy.CashUpdates);
		}

		private async Task<(QuestRewardService Service, INetworkClient Client, RecordingRewardOutbound Outbound, RecordingEconomy Economy)> Harness(bool completed = true,
			uint xpReward = 0, ulong moneyReward = 0)
		{
			var (options, path) = TestDb.NewSeededDatabase(CharacterId);
			_databases.Add(path);

			await using (var ctx = new DatabaseContext(options))
			{
				ctx.QuestLines.Add(new QuestLine { QuestLineId = 386 });
				ctx.QuestDefinitions.Add(new QuestDefinition
				{
					QuestId = QuestId, QuestLineId = 386, Name = "The Trainer of Mages",
					XpReward = xpReward, MoneyReward = moneyReward
				});
				ctx.QuestDefinitions.Add(new QuestDefinition { QuestId = 12371, QuestLineId = 386, Name = "The Quarry" });

				// The three captured options, with the real keys the shipped Items rows carry.
				AddItem(ctx, Dagger, "Acolyte's Dagger", 0x684E0BE9, 0xBEB853FE, 0x041347D7, 0x660CDF13);
				AddItem(ctx, Longsword, "Acolyte's Longsword", 0x1FB3F747, 0x5B6F1297, 0x3DCB757F, 0x7FBC62B5);
				AddItem(ctx, Staff, "Acolyte's Staff", 0x4735FEBC, 0xDCB11519, 0xDED73456, 0xFAC62D21);

				for (var pos = 0u; pos < 3; pos++)
				{
					ctx.QuestRewardOptions.Add(new QuestRewardOption
					{
						QuestId = QuestId, Pos = pos, Level = 5,
						ItemTemplateId = pos switch { 0 => Dagger, 1 => Longsword, _ => Staff }
					});
				}

				ctx.CharacterQuests.Add(new CharacterQuest
				{
					CharacterId = CharacterId, QuestId = QuestId,
					State = completed ? QuestState.Completed : QuestState.Active
				});
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new Factory(options));
			var outbound = new RecordingRewardOutbound();
			var economy = new RecordingEconomy();
			return (new QuestRewardService(database, outbound, economy, (_, xp) =>
			{
				_xpAwards.Add(xp);
				return Task.CompletedTask;
			}, NullLogger.Instance), new RecordingNetworkClient(CharacterId), outbound, economy);
		}

		private static void AddItem(DatabaseContext ctx, uint id, string name, uint k1, uint k2, uint k3, uint k4)
		{
			ctx.Items.Add(new Item
			{
				Id = id, Name = name, Level = 5,
				Key = new Faolan.Core.Protobuf.ProtoDecryptionKey { Key1 = k1, Key2 = k2, Key3 = k3, Key4 = k4 }
			});
		}

		// Records only what these gates read: the offers and confirms that went out.
		private sealed class RecordingRewardOutbound : NoOpQuestOutbound
		{
			public List<(uint QuestId, List<QuestRewardEntry> Options)> Offers { get; } = [];
			public List<(uint QuestId, uint Index)> Confirms { get; } = [];

			public override Task SendQuestRewardOffer(INetworkClient client, uint questId, IReadOnlyList<QuestRewardEntry> options)
			{
				Offers.Add((questId, [.. options]));
				return Task.CompletedTask;
			}

			public override Task SendQuestRewardConfirm(INetworkClient client, uint questId, uint index)
			{
				Confirms.Add((questId, index));
				return Task.CompletedTask;
			}
		}

		// Records which templates actually reached the backpack - the difference between "confirmed" and "granted" -
		// and the wallet pushes, which is how the money award is observed.
		private sealed class RecordingEconomy : NoOpEconomyOutbound
		{
			public List<uint> Granted { get; } = [];
			public List<(ulong BalanceTin, UpdateCashReason Reason)> CashUpdates { get; } = [];

			public override Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null, string? creatorName = null)
			{
				Granted.Add(itemTemplateId);
				return Task.FromResult<int?>(Granted.Count);
			}

			public override Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
			{
				CashUpdates.Add((balanceTin, reason));
				return Task.CompletedTask;
			}
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}
		}
	}
}
