using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #527: the ORDER of quest packets a multi-goal quest emits, against what retail actually sends.
	//
	// Retail has three shapes and no others, and the difference between them is entirely in what the QUEST is doing -
	// not what the goal is:
	//
	//   goal short of its count            QuestGoal(Progress)
	//   goal met, quest still has a goal   QuestGoal(Satisfied), QuestGoal(Advanced)
	//   goal met, quest done               QuestGoal(Satisfied), QuestIIR(Active 2), QuestIIR(Completed 4)
	//
	// Advanced is the hand-off marker on the goal being LEFT BEHIND - it rides in the same frame as that goal's own
	// Satisfied, and the successor's Satisfied lands whenever it is solved (34 s to 8 min later in the corpus). And a
	// QuestIIR appears ONLY on the third shape: the corpus has 14 Progress/Advanced frames and not one carries one.
	//
	// [V2: 12371 g3495 (Progress x2 -> Satisfied+Advanced) then g3497 in
	// xxx_mage-necromancer_login_play2_tortage_city_first-quests fr4247/4815/4872/5488 and
	// Hoxxling_mage-heraldofxotli_login_gameplay2 fr4310/4367/4447/5102; the same pair on 10238 g1448, 12369 g7134 and
	// 10287 g400 across Hoxxling_..._login_gameplay, xxx_mage-demo_login_play_beach-to-enter-tortage_city,
	// xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city and loot-drag-1538]
	public class QuestGoalHandoffTests : IDisposable
	{
		private const uint CharacterId = 0x01000001;
		private const uint QuestId = 90101;

		// A two-goal quest shaped like Shipwrecked V: collect three of a thing, then hand it over by talking.
		private const uint CollectGoal = 3495;
		private const uint TalkGoal = 3497;
		private const uint BrickItemId = 193400;
		private const string Recipient = "Turach";

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);
		}

		[Fact]
		public async Task AGoalShortOfItsCount_SendsProgressAndNothingElse()
		{
			var (service, client, outbound) = await Harness();

			await service.AdvanceCollectGoals(client, BrickItemId);

			Assert.Equal(["goal 3495 Progress"], outbound.Calls);
		}

		[Fact]
		public async Task TheGoalThatMeetsItsCount_ClosesWithAdvancedBecauseTheQuestHasAnotherGoal()
		{
			var (service, client, outbound) = await Harness();

			await service.AdvanceCollectGoals(client, BrickItemId, 3);

			Assert.Equal(["goal 3495 Satisfied", "goal 3495 Advanced"], outbound.Calls);
		}

		[Fact]
		public async Task TheLastGoal_CompletesTheQuestWithTheJournalFlipAndNoAdvanced()
		{
			var (service, client, outbound) = await Harness();
			await service.AdvanceCollectGoals(client, BrickItemId, 3);
			outbound.Calls.Clear();

			await service.AdvanceTalkGoals(client, Recipient);

			Assert.Equal(["goal 3497 Satisfied", $"quest state {QuestCodec.StateActive}", $"quest state {QuestCodec.StateCompleted}"], outbound.Calls);
		}

		// The whole quest end to end, as one sequence - the shape the capture shows and the reason the three cases
		// above are worth having separately: the pair boundaries are where an extra packet would hide.
		[Fact]
		public async Task TheWholeQuest_MatchesTheCapturedPacketOrder()
		{
			var (service, client, outbound) = await Harness();

			await service.AdvanceCollectGoals(client, BrickItemId);
			await service.AdvanceCollectGoals(client, BrickItemId);
			await service.AdvanceCollectGoals(client, BrickItemId);
			await service.AdvanceTalkGoals(client, Recipient);

			Assert.Equal([
				"goal 3495 Progress",
				"goal 3495 Progress",
				"goal 3495 Satisfied",
				"goal 3495 Advanced",
				"goal 3497 Satisfied",
				$"quest state {QuestCodec.StateActive}",
				$"quest state {QuestCodec.StateCompleted}"
			], outbound.Calls);
		}

		private async Task<(QuestProgressionService Service, INetworkClient Client, RecordingQuestOutbound Outbound)> Harness()
		{
			var (options, path) = TestDb.NewSeededDatabase(CharacterId);
			_databases.Add(path);

			await using (var ctx = new DatabaseContext(options))
			{
				ctx.QuestLines.Add(new QuestLine { QuestLineId = 9100 });
				var quest = new QuestDefinition { QuestId = QuestId, QuestLineId = 9100, Name = "Two goals", GoalId = CollectGoal };
				ctx.QuestDefinitions.Add(quest);
				ctx.QuestGoalDefinitions.Add(new QuestGoalDefinition
				{
					GoalId = CollectGoal, QuestDefinitionId = QuestId, Sequence = 1, GoalType = QuestGoalType.CollectItem,
					RequiredCount = 3, TrailingRefId = BrickItemId, TrailingCategory = QuestProgress.ItemNameCategory
				});
				ctx.QuestGoalDefinitions.Add(new QuestGoalDefinition
				{
					GoalId = TalkGoal, QuestDefinitionId = QuestId, Sequence = 2, GoalType = QuestGoalType.TalkToNpc,
					RequiredCount = 1, TargetName = Recipient
				});
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new Factory(options));
			var outbound = new RecordingQuestOutbound();
			var service = new QuestProgressionService(database, outbound, null!, null!, new ChatServerDispatcher(), NullLogger.Instance);

			var client = new RecordingNetworkClient(CharacterId);
			await service.Grant(client, QuestId, nameof(QuestGoalHandoffTests));
			outbound.Calls.Clear();
			return (service, client, outbound);
		}

		// Records the two packets whose ORDER is under test, and nothing else - the world-effect and marker ops the
		// engine also fires are not part of the wire grammar this gates.
		private sealed class RecordingQuestOutbound : IQuestOutbound
		{
			public List<string> Calls { get; } = [];

			public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update)
			{
				Calls.Add($"goal {goalId} {update}");
				return Task.CompletedTask;
			}

			public Task SendQuestIir(INetworkClient client, uint questId, uint state)
			{
				Calls.Add($"quest state {state}");
				return Task.CompletedTask;
			}

			public Task ApplyQuestWorldEffects(INetworkClient client) => Task.CompletedTask;
			public void InvalidateReachGoals(INetworkClient client) { }
			public Task RefreshQuestMarkerByName(INetworkClient client, string npcName) => Task.CompletedTask;
			public Task RefreshQuestVisibility(INetworkClient client) => Task.CompletedTask;

			// #528: the ambusher TYPES the engine sprang, so a gate can assert one fired without a live zone.
			public List<uint> Ambushes { get; } = [];

			public void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
			{
				Ambushes.Add(ambush.NpcTemplateId);
			}

			public Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null) => Task.CompletedTask;
			public Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds) => Task.CompletedTask;
			public bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow) => false;
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new(options);
			public Task<DatabaseContext> CreateDbContextAsync(System.Threading.CancellationToken ct = default) => Task.FromResult(new DatabaseContext(options));
		}
	}
}
