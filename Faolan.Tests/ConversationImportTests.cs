using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The conversation import writing the folded graph, against a real database: the screens, the branches
	// between them, the roots that open them, and the delete order that lets a rebuild reuse the same derived keys.
	//
	// ConversationGraphTests pins the fold itself; this pins that what the fold produced survives the round-trip -
	// including the FK shape, which is where an edge naming two nodes is easy to get wrong.
	//
	// The fold is a global partition over every harvested walk, so a dump cannot be merged into a database that has
	// forgotten the walks the rest came from; the merge rule ("the most complete capture of a screen wins") runs
	// inside the fold.
	public class ConversationImportTests
	{
		private const uint QuestLineId = 241;
		private const uint GrantedQuestId = 13479;
		private const uint MapId = 4010;
		private const uint TypeId = 42008;

		// Casilda's real NPC template - the screen's identity, and a real FK, so the row has to exist.
		private const uint NpcTemplate = 9411;

		[Fact]
		public async Task TheFoldedGraphSurvivesTheRoundTrip()
		{
			await WithDatabase(async db =>
			{
				var result = await ConversationImportOps.ImportConversations(db, [Walk(""), Walk("0", grant: GrantedQuestId)]);

				Assert.Equal(2, result.Nodes);
				Assert.Equal(1, result.Edges);
				Assert.Equal(1, result.Entries);

				var root = await db.ConversationEntries.SingleAsync(TestContext.Current.CancellationToken);
				Assert.Equal(ConversationTrigger.Talk, root.Trigger);
				Assert.Equal("Casilda", root.NpcName);

				// The branch leaves the root's screen, is named by the captured option's own LDB id, and carries the
				// grant - none of which is a property of the screen it lands on.
				var edge = await db.ConversationEdges.SingleAsync(TestContext.Current.CancellationToken);
				Assert.Equal(root.NodeId, edge.FromNodeId);
				Assert.NotEqual(root.NodeId, edge.ToNodeId);
				Assert.Equal(GrantedQuestId, edge.GrantQuestId);
			});
		}

		// A rebuild reuses the very same derived ids, so it must drop the wiring before the screens: an edge names two
		// nodes and the inbound end deliberately does not cascade, so deleting a screen underneath a live edge is a
		// foreign-key violation rather than a silent orphan.
		[Fact]
		public async Task ARebuildOverTheSameCorpusIsIdempotent()
		{
			await WithDatabase(async db =>
			{
				await ConversationImportOps.ImportConversations(db, [Walk(""), Walk("0")]);
				var before = await db.ConversationNodes.Select(n => n.Id).OrderBy(i => i).ToListAsync(TestContext.Current.CancellationToken);

				await ConversationImportOps.ImportConversations(db, [Walk(""), Walk("0")]);

				Assert.Equal(before, await db.ConversationNodes.Select(n => n.Id).OrderBy(i => i).ToListAsync(TestContext.Current.CancellationToken));
				Assert.Equal(1, await db.ConversationEdges.CountAsync(TestContext.Current.CancellationToken));
			});
		}

		// The rebuild is what the corpus says: a screen the new set does not produce goes, together with the branch
		// that reached it.
		[Fact]
		public async Task ARebuildDropsWhatTheCorpusNoLongerProduces()
		{
			await WithDatabase(async db =>
			{
				await ConversationImportOps.ImportConversations(db, [Walk(""), Walk("0")]);
				await ConversationImportOps.ImportConversations(db, [Walk("")]);

				Assert.Equal(1, await db.ConversationNodes.CountAsync(TestContext.Current.CancellationToken));
				Assert.Equal(0, await db.ConversationEdges.CountAsync(TestContext.Current.CancellationToken));
			});
		}

		// Every shipped node's id must be the function of its own columns - the same contract DatabaseConventionTests
		// enforces over faolan.db, checked here on a freshly written graph so a broken ComputeId fails at the import
		// rather than at the next full harvest.
		[Fact]
		public async Task EveryWrittenRowAgreesWithItsDerivedKey()
		{
			await WithDatabase(async db =>
			{
				await ConversationImportOps.ImportConversations(db, [Walk(""), Walk("0")]);

				foreach (var n in await db.ConversationNodes.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
					Assert.Equal(n.ComputeId(), n.Id);
				foreach (var e in await db.ConversationEdges.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
					Assert.Equal(e.ComputeId(), e.Id);
				foreach (var e in await db.ConversationEntries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
					Assert.Equal(e.ComputeId(), e.Id);
			});
		}

		// One harvested walk. The root is a spoken line plus the captured answer list, which is what names the branch out
		// of it; the child is a terminal close, so the two screens differ in content and stay two nodes (identical
		// content would - correctly - fold into one). Real captured records: the importer rejects a packet it cannot
		// decode and rebuild byte-for-byte, so a synthetic blob would not survive the import.
		private static ConversationWalk Walk(string answerPath, uint grant = 0)
		{
			return new ConversationWalk(TypeId, answerPath, 0x4DEE9, answerPath.Length == 0
				? [Packet(KnubotCodecTestData.AppendTextBody), Packet(KnubotCodecTestData.AnswerListBody)]
				: [Packet(KnubotCodecTestData.CloseChatWindowBody)], "Casilda", NpcTemplate, GrantQuestId: grant);
		}

		// A captured record wrapped in the carrier the decoder scans by offset (12-byte frame head + an empty rpcHeader).
		private static byte[] Packet(string recordHex)
		{
			var record = Convert.FromHexString(recordHex);
			var packet = new byte[12 + record.Length];
			packet.WriteU32Be(8, 0); // rpcHeader length
			Buffer.BlockCopy(record, 0, packet, 12, record.Length);
			return packet;
		}

		private static async Task WithDatabase(Func<DatabaseContext, Task> body)
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-convgraph-{Guid.NewGuid():N}.db");
			try
			{
				await using var db = new DatabaseContext(TestDb.Options(path));
				await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

				// GrantQuestId is a real FK, and a QuestDefinition FKs its area and map in turn - so the grant a branch
				// carries needs that whole chain present before it can be referenced. The NPC template is a non-null FK
				// on both the node and the root, so it joins that set.
				db.Maps.Add(new Map
				{
					Id = MapId,
					Name = "Tortage Island"
				});
				db.NpcTemplates.Add(new NpcTemplate
				{
					TemplateId = NpcTemplate,
					Name = "Casilda"
				});
				db.QuestLines.Add(new QuestLine
				{
					QuestLineId = QuestLineId
				});
				db.QuestDefinitions.Add(new QuestDefinition
				{
					QuestId = GrantedQuestId,
					QuestLineId = QuestLineId,
					MapId = MapId
				});
				await db.SaveChangesAsync(TestContext.Current.CancellationToken);
				await body(db);
			}
			finally
			{
				TestDb.Delete(path);
			}
		}
	}
}
