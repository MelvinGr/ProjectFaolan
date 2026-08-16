using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The marker byte over a quest giver's head, as a function of what the player is carrying.
	//
	// The oracle is `Blablatest_mage-necromancer-create_casilda_quest_first_stage`, a capture that walks Casilda's
	// whole chain on a fresh character. Her handle is 0x315cc and DispatchSetQuestMarker addresses it five times:
	//
	//   t=191.7  QuestFullUpdate grants 13479 "The Scavenger"
	//   t=191.9  marker 0x00  <- ACCEPTING THE QUEST CLEARS THE '!', it does not grey it
	//   t=213.8  marker 0x00     (Scavenger Tuut killed; 13479 completes, 13563 granted)
	//   t=223.3  marker 0x04     (key looted; 13480 makes "talk to Casilda" the goal -> gold '?')
	//   t=230.1  marker 0x00     (13480 handed in, 12407 granted)
	//
	// and 0x02 before any of it. Across all 275 captures Casilda is 0x00 x178 / 0x02 x170 / 0x04 x21 - never 0x01.
	// Until this gate existed the giver of a quest you were carrying wore the SILVER '!' for the quest's whole
	// lifetime, which is the one state retail never puts her in.
	public class QuestMarkerStateTests : IDisposable
	{
		private const uint Player = 0x01000001;
		private const uint GiverTemplate = 9411; // Casilda
		private const uint GiverQuest = 13479; // "The Scavenger", step 1 of line 1054
		private const uint GiverLine = 1054;
		private const uint GiverHandle = 0x000315cc; // her handle in the capture, for provenance only

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);
		}

		[Fact]
		public async Task AQuestNotYetTaken_IsTheGoldExclamation()
		{
			var h = await Harness();

			Assert.Equal(QuestMarkerResolver.Available, await h.Resolver.MarkerStateFor(h.Client, h.Npc));
		}

		[Fact]
		public async Task AQuestBeingCARRIED_DrawsNoMarkerAtAll()
		{
			// The regression: accepting must clear the marker, not swap it for the silver '!'. Retail sends 0x00
			// 0.2s after the grant and holds it there for the whole objective.
			var h = await Harness();
			await h.Database.SetCharacterQuest(Player, GiverQuest, QuestState.Active, goalProgress: 0, goalRequired: 1);

			Assert.Equal(QuestMarkerResolver.None, await h.Resolver.MarkerStateFor(h.Client, h.Npc));
		}

		[Fact]
		public async Task AnObjectiveDone_IsTheGoldQuestionMark()
		{
			var h = await Harness();
			await h.Database.SetCharacterQuest(Player, GiverQuest, QuestState.Active, goalProgress: 1, goalRequired: 1);

			Assert.Equal(QuestMarkerResolver.TurnIn, await h.Resolver.MarkerStateFor(h.Client, h.Npc));
		}

		[Fact]
		public async Task AQuestALREADYCOMPLETED_DrawsNoMarkerAtAll()
		{
			// Same answer as the carried case, and the one that matters on a character deep into a line: a finished
			// giver is quiet, not permanently "available soon".
			var h = await Harness();
			await h.Database.SetCharacterQuest(Player, GiverQuest, QuestState.Completed);

			Assert.Equal(QuestMarkerResolver.None, await h.Resolver.MarkerStateFor(h.Client, h.Npc));
		}

		[Fact]
		public async Task AGiverTheLineHasNotREACHEDYet_KeepsTheSilverExclamation()
		{
			// #559 stays: silver means "there is something here you cannot take yet", which is exactly a giver whose
			// step the chain has not arrived at. It is the not-held case, and only the not-held case.
			var h = await Harness(giverSequence: 2, earlierStep: (GiverQuest + 1, 1));

			Assert.Equal(QuestMarkerResolver.Unavailable, await h.Resolver.MarkerStateFor(h.Client, h.Npc));
		}

		private async Task<Harnessed> Harness(uint giverSequence = 1, (uint QuestId, uint Sequence)? earlierStep = null)
		{
			var (options, path) = TestDb.NewSeededDatabase(Player);
			_databases.Add(path);
			await using (var ctx = new DatabaseContext(options))
			{
				ctx.NpcTemplates.Add(new NpcTemplate { TemplateId = GiverTemplate, Name = "Casilda" });
				ctx.QuestLines.Add(new QuestLine { QuestLineId = GiverLine });
				// A step only counts as part of its line once it has journal TEXT - that is the filter
				// GetQuestDefinitionsByLine applies to keep the client's catalog-only quests out of a line (see there).
				ctx.QuestDefinitions.Add(Step(GiverQuest, "The Scavenger", giverSequence));
				if (earlierStep is { } earlier)
					ctx.QuestDefinitions.Add(Step(earlier.QuestId, "An earlier step", earlier.Sequence));

				// The giver map is derived from the dialogue graph, so the NPC only counts as a giver once some branch
				// of theirs grants the quest. One self-looping edge is the smallest shape that says so.
				var node = new ConversationNode { ContentKey = "50002:1", NpcName = "Casilda", NpcTemplateId = GiverTemplate };
				ctx.ConversationNodes.Add(node);
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

				ctx.ConversationEdges.Add(new ConversationEdge { FromNodeId = node.Id, ToNodeId = node.Id, OptionId = 0, GrantQuestId = GiverQuest });
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new Factory(options));
			var client = new RecordingNetworkClient(Player);
			var npc = new WorldEntity(GiverHandle, Map.TortageBeachId, false, "Casilda", client)
			{
				NpcTemplateId = GiverTemplate
			};

			return new Harnessed(new QuestMarkerResolver(database, NullLogger.Instance), database, client, npc);
		}

		private static QuestDefinition Step(uint questId, string name, uint sequence) =>
			new()
			{
				QuestId = questId, QuestLineId = GiverLine, Name = name,
				MapId = Map.TortageBeachId, JournalSequence = sequence,
				TextRefs = [new QuestTextRef { QuestId = questId, RefId = questId, Slot = QuestTextSlot.Title, Category = 50301 }]
			};

		private sealed record Harnessed(QuestMarkerResolver Resolver, IDatabaseRepository Database, RecordingNetworkClient Client, WorldEntity Npc);

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new(options);
		}
	}
}
