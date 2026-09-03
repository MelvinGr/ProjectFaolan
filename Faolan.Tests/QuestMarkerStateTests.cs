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
		private const uint SecondQuest = 13563; // a second grant from the same NPC, for the Alyssa-shaped case
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

		[Fact]
		public async Task ATwoQuestGiver_STAYSSilverWhileYouCarryTheFirst()
		{
			// Casilda and Alyssa are both "carrying the giver's quest" and get DIFFERENT markers, because what is left
			// to grant differs. Casilda grants one quest and goes blank; Alyssa sits on a four-step line ending in
			// "Report back to Alyssa" and goes silver eight frames after granting hers
			// (Hoxxling_..._login_gameplay2: grant at frame 9603, 0x01 at frame 9611).
			var h = await Harness(alsoGrants: (SecondQuest, 2));
			await h.Database.SetCharacterQuest(Player, GiverQuest, QuestState.Active, goalProgress: 0, goalRequired: 1);

			Assert.Equal(QuestMarkerResolver.Unavailable, await h.Resolver.MarkerStateFor(h.Client, h.Npc));
		}

		[Fact]
		public async Task ATwoQuestGiver_GoesGoldAgainOnceTheFirstIsDone()
		{
			// …and the second grant becoming reachable is what turns them gold again, rather than the NPC being
			// permanently spent after one quest.
			var h = await Harness(alsoGrants: (SecondQuest, 2));
			await h.Database.SetCharacterQuest(Player, GiverQuest, QuestState.Completed);

			Assert.Equal(QuestMarkerResolver.Available, await h.Resolver.MarkerStateFor(h.Client, h.Npc));
		}

		[Fact]
		public async Task AGiverStandingOffTheQuestSOwnMap_StillWearsTheMarker()
		{
			// `QuestDefinition.MapId` is the objective's marker PIN, not where the giver stands, so it must not gate.
			// It blocks 15 of the 28 giver quests in the shipped data - Turach and Laranga (quests on 4010, they stand
			// on 4020), Alyssa (10800 vs 4020), Caor, Oracle Vateesa, Hadrathus - and the captures show retail marking
			// every one of them.
			// The NPC stands on Tortage Island (4010) while the quest pins White Sands (10800) - Alyssa's real shape.
			var h = await Harness(questMapId: 10800);

			Assert.Equal(QuestMarkerResolver.Available, await h.Resolver.MarkerStateFor(h.Client, h.Npc));
		}

		private async Task<Harnessed> Harness(uint giverSequence = 1, (uint QuestId, uint Sequence)? earlierStep = null, (uint QuestId, uint Sequence)? alsoGrants = null,
			uint? questMapId = null)
		{
			var (options, path) = TestDb.NewSeededDatabase(Player);
			_databases.Add(path);
			await using (var ctx = new DatabaseContext(options))
			{
				ctx.NpcTemplates.Add(new NpcTemplate { TemplateId = GiverTemplate, Name = "Casilda" });
				// The seeded DB only carries Tortage Beach; a quest pinned elsewhere needs that map to exist for the FK.
				if (questMapId is { } pin && pin != Map.TortageBeachId)
					ctx.Maps.Add(new Map { Id = pin, Name = "Somewhere else" });
				ctx.QuestLines.Add(new QuestLine { QuestLineId = GiverLine });
				// A step only counts as part of its line once it has journal TEXT - that is the filter
				// GetQuestDefinitionsByLine applies to keep the client's catalog-only quests out of a line (see there).
				ctx.QuestDefinitions.Add(Step(GiverQuest, "The Scavenger", giverSequence, questMapId));
				if (earlierStep is { } earlier)
					ctx.QuestDefinitions.Add(Step(earlier.QuestId, "An earlier step", earlier.Sequence));
				if (alsoGrants is { } second)
					ctx.QuestDefinitions.Add(Step(second.QuestId, "A second quest from the same giver", second.Sequence));

				// The giver map is derived from the dialogue graph, so the NPC only counts as a giver once some branch
				// of theirs grants the quest. One self-looping edge is the smallest shape that says so.
				var node = new ConversationNode { ContentKey = "50002:1", NpcName = "Casilda", NpcTemplateId = GiverTemplate };
				ctx.ConversationNodes.Add(node);
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

				// One SaveChanges per edge: the derived key is stamped ON INSERT, so two unsaved edges both sit at
				// Id 0 in the change tracker and collide before either is written.
				ctx.ConversationEdges.Add(new ConversationEdge { FromNodeId = node.Id, ToNodeId = node.Id, OptionId = 0, GrantQuestId = GiverQuest });
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				if (alsoGrants is { } secondGrant)
				{
					ctx.ConversationEdges.Add(new ConversationEdge { FromNodeId = node.Id, ToNodeId = node.Id, OptionId = 1, GrantQuestId = secondGrant.QuestId });
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}
			}

			var database = new DatabaseRepository(new Factory(options));
			var client = new RecordingNetworkClient(Player);
			var npc = new WorldEntity(GiverHandle, Map.TortageBeachId, false, "Casilda", client)
			{
				NpcTemplateId = GiverTemplate
			};

			return new Harnessed(new QuestMarkerResolver(database, database, database, NullLogger.Instance), database, client, npc);
		}

		private static QuestDefinition Step(uint questId, string name, uint sequence, uint? mapId = null) =>
			new()
			{
				QuestId = questId, QuestLineId = GiverLine, Name = name,
				MapId = mapId ?? Map.TortageBeachId, JournalSequence = sequence,
				TextRefs = [new QuestTextRef { QuestId = questId, RefId = questId, Slot = QuestTextSlot.Title, Category = 50301 }]
			};

		private sealed record Harnessed(QuestMarkerResolver Resolver, IDatabaseRepository Database, RecordingNetworkClient Client, WorldEntity Npc);

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new(options);
		}
	}
}
