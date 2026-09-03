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
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #528: the quest-triggered AMBUSH - the third trigger shape, after quest-gated visibility (#261/#262) and the
	// proximity lure (#502).
	//
	// Decoded from `xxx_mage-necromancer_login_play2_tortage_city_first-quests`: the player picks up the FIRST Quarry
	// Stonebrick for quest 12371 at t=315.6 and a `Bat Demon` arrives at t=318.6, 3.6 u away, already hating them.
	// They go on to pick up all three bricks and get exactly ONE Bat Demon, which is what pins the trigger to the
	// goal's first advance rather than to every pickup.
	//
	// What this gates is the TRIGGER (does the right goal spring it, once); the spawn itself is the world layer's and
	// shares its machinery with the encounter swap.
	public class QuestAmbushTests : IDisposable
	{
		private const uint CharacterId = 0x01000001;
		private const uint QuestId = 12371;
		private const uint CollectGoal = 3495;
		private const uint TalkGoal = 3497;
		private const uint BrickItemId = 193400;
		private const uint MapId = 4020;
		private const uint BatDemon = 13952;

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);

			GC.SuppressFinalize(this);
		}

		[Fact]
		public async Task TheFirstPickup_SpringsTheAmbush()
		{
			var (service, client, outbound) = await Harness();

			await service.AdvanceCollectGoals(client, BrickItemId);

			Assert.Equal([BatDemon], outbound.Ambushes);
		}

		// One Bat Demon for three bricks: the trigger is the goal's FIRST advance, not every tick of it.
		[Fact]
		public async Task TheSecondAndThirdPickups_SpringNothingMore()
		{
			var (service, client, outbound) = await Harness();

			await service.AdvanceCollectGoals(client, BrickItemId);
			await service.AdvanceCollectGoals(client, BrickItemId);
			await service.AdvanceCollectGoals(client, BrickItemId);

			Assert.Equal([BatDemon], outbound.Ambushes);
		}

		// A goal with no ambush row springs nothing - the quest's own talk goal, ticked on the same quest.
		[Fact]
		public async Task AGoalWithNoAmbush_SpringsNothing()
		{
			var (service, client, outbound) = await Harness();

			await service.AdvanceTalkGoals(client, "Turach");

			Assert.Empty(outbound.Ambushes);
		}

		private async Task<(QuestProgressionService Service, INetworkClient Client, RecordingQuestOutbound Outbound)> Harness()
		{
			var (options, path) = TestDb.NewSeededDatabase(CharacterId);
			_databases.Add(path);

			await using (var ctx = new DatabaseContext(options))
			{
				ctx.Maps.Add(new Map { Id = MapId, Name = "Tortage - The Quarry" });
				ctx.NpcTemplates.Add(new NpcTemplate { TemplateId = BatDemon, Name = "Bat Demon" });
				ctx.QuestLines.Add(new QuestLine { QuestLineId = 9100 });
				ctx.QuestDefinitions.Add(new QuestDefinition { QuestId = QuestId, QuestLineId = 9100, Name = "Get 3 stone bricks", GoalId = CollectGoal });
				ctx.QuestGoalDefinitions.Add(new QuestGoalDefinition
				{
					GoalId = CollectGoal, QuestDefinitionId = QuestId, Sequence = 1, GoalType = QuestGoalType.CollectItem,
					RequiredCount = 3, TrailingRefId = BrickItemId, TrailingCategory = QuestProgress.ItemNameCategory
				});
				ctx.QuestGoalDefinitions.Add(new QuestGoalDefinition
				{
					GoalId = TalkGoal, QuestDefinitionId = QuestId, Sequence = 2, GoalType = QuestGoalType.TalkToNpc,
					RequiredCount = 1, TargetName = "Turach"
				});
				ctx.NpcAmbushes.Add(new NpcAmbush
				{
					MapId = MapId, NpcTemplateId = BatDemon, QuestDefinitionId = QuestId, GoalId = CollectGoal,
					DelaySeconds = 3.0f, AnchorDistance = 3.6f
				});
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new Factory(options));
			var outbound = new RecordingQuestOutbound();
			var service = new QuestProgressionService(database, outbound, null!, null!, new ChatServerDispatcher(), new QuestRewardService(database, outbound, null!, (_, _) => Task.CompletedTask, NullLogger.Instance), new DailyChallengeService(database, NullLogger.Instance), NullLogger.Instance);

			var client = new RecordingNetworkClient(CharacterId);
			await service.Grant(client, QuestId, nameof(QuestAmbushTests));
			return (service, client, outbound);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(System.Threading.CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
