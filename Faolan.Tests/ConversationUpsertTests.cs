using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Incremental conversation import (#419): dropping ONE new dump in adds what it saw without disturbing what is
	// already stored. This is the property the whole feature exists for, and it is not something the corpus can
	// demonstrate - a full import sees everything at once - so it is pinned here against a real database.
	//
	// The case that matters: two captures talk to the SAME NPC but walk DIFFERENT options. Screens are keyed by
	// answer PATH, so the two contribute different rows and must UNION, not overwrite each other.
	public class ConversationUpsertTests
	{
		private const uint AreaId = 241;
		private const uint GrantedQuestId = 13479;
		private const uint MapId = 4010;
		private const uint TypeId = 42008;

		[Fact]
		public async Task TwoDumpsWalkingDifferentOptions_UnionIntoOneDialogueTree()
		{
			await WithDatabase(async db =>
			{
				// Dump 1 walked option 0: it saw the greeting and the "0" screen.
				await ImportOps.ImportConversations(db, [Entry(""), Entry("0")], overwrite: false);

				// Dump 2 walked option 1 instead: same greeting, a screen dump 1 never reached.
				var second = await ImportOps.ImportConversations(db, [Entry(""), Entry("1")], overwrite: false);

				var paths = await db.ConversationNodes.Select(n => n.AnswerPath).ToListAsync(TestContext.Current.CancellationToken);
				Assert.Equal(["", "0", "1"], paths.OrderBy(p => p).ToArray());

				// "1" is new; the greeting was already stored just as completely, so it is left alone.
				Assert.Equal(1, second.Inserted);
				Assert.Equal(1, second.Kept);
				Assert.Equal(0, second.Replaced);
			});
		}

		// A dump that saw a screen less completely must NEVER displace a fuller capture of it - that would make
		// importing a partial session actively destructive.
		[Fact]
		public async Task AThinnerDumpDoesNotDisplaceAFullerScreen()
		{
			await WithDatabase(async db =>
			{
				await ImportOps.ImportConversations(db, [Entry("", packets: 3)], overwrite: false);
				var thin = await ImportOps.ImportConversations(db, [Entry("", packets: 1)], overwrite: false);

				Assert.Equal(1, thin.Kept);
				Assert.Equal(0, thin.Inserted);
				Assert.Equal(3, await db.ConversationPackets.CountAsync(TestContext.Current.CancellationToken));
			});
		}

		// ...and the reverse: a richer capture DOES replace the stored screen, or coverage could never improve.
		[Fact]
		public async Task ARicherDumpReplacesTheStoredScreen()
		{
			await WithDatabase(async db =>
			{
				await ImportOps.ImportConversations(db, [Entry("", packets: 1)], overwrite: false);
				var rich = await ImportOps.ImportConversations(db, [Entry("", packets: 3)], overwrite: false);

				Assert.Equal(1, rich.Replaced);
				Assert.Equal(3, await db.ConversationPackets.CountAsync(TestContext.Current.CancellationToken));
			});
		}

		// The sparse fields survive in BOTH directions. Only the capture that walked the accept branch sees the
		// grant, so neither replacing a screen nor keeping one may drop it - otherwise re-importing a dump that only
		// browsed the dialogue would silently un-grant its quest.
		[Fact]
		public async Task AGrantSurvivesWhicheverDumpSawIt()
		{
			await WithDatabase(async db =>
			{
				// Stored first WITH the grant, then a richer dump that never saw it -> the replacement keeps it.
				await ImportOps.ImportConversations(db, [Entry("", packets: 1, grant: GrantedQuestId)], overwrite: false);
				await ImportOps.ImportConversations(db, [Entry("", packets: 3)], overwrite: false);
				Assert.Equal(GrantedQuestId, (await db.ConversationNodes.SingleAsync(TestContext.Current.CancellationToken)).GrantQuestId);
			});

			await WithDatabase(async db =>
			{
				// Stored first WITHOUT it, then a thinner dump that did see it -> the kept row learns it.
				await ImportOps.ImportConversations(db, [Entry("", packets: 3)], overwrite: false);
				await ImportOps.ImportConversations(db, [Entry("", packets: 1, grant: GrantedQuestId)], overwrite: false);
				Assert.Equal(GrantedQuestId, (await db.ConversationNodes.SingleAsync(TestContext.Current.CancellationToken)).GrantQuestId);
			});
		}

		// Overwrite is still a clean rebuild - it must DROP what the corpus no longer produces, which is exactly what
		// upsert must not do.
		[Fact]
		public async Task OverwriteDropsScreensTheNewSetNoLongerHas()
		{
			await WithDatabase(async db =>
			{
				await ImportOps.ImportConversations(db, [Entry(""), Entry("0")], overwrite: false);
				await ImportOps.ImportConversations(db, [Entry("")], overwrite: true);

				Assert.Equal([""], await db.ConversationNodes.Select(n => n.AnswerPath).ToArrayAsync(TestContext.Current.CancellationToken));
			});
		}

		private static ConversationEntry Entry(string answerPath, int packets = 1, uint grant = 0)
		{
			// A REAL captured record per packet - the importer columnizes it and rejects anything it cannot
			// round-trip, so a synthetic blob would not survive the import.
			return new ConversationEntry(TypeId, answerPath, 0x4DEE9, [.. Enumerable.Range(0, packets).Select(_ => AppendTextPacket())], "Casilda", GrantQuestId: grant);
		}

		private static byte[] AppendTextPacket()
		{
			return Convert.FromHexString(CapturedPacketHex);
		}

		private static async Task WithDatabase(Func<DatabaseContext, Task> body)
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-convupsert-{Guid.NewGuid():N}.db");
			try
			{
				await using var db = new DatabaseContext(TestDb.Options(path));
				await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

				// GrantQuestId is a real FK, and a QuestDefinition FKs its area and map in turn - so the grant a screen
				// carries needs that whole chain present before it can be referenced.
				db.Maps.Add(new Map
				{
					Id = MapId,
					Name = "Tortage Island"
				});
				db.QuestAreas.Add(new QuestArea
				{
					AreaId = AreaId
				});
				db.QuestDefinitions.Add(new QuestDefinition
				{
					QuestId = GrantedQuestId,
					AreaId = AreaId,
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

		// A REAL captured conversation packet - the SetDialogueCamera (0x2015) that ConversationConstructTests uses as
		// its round-trip oracle. Real rather than synthetic because the importer REJECTS a packet it cannot decode and
		// rebuild byte-for-byte, so these tests exercise the actual columnization path rather than a stub.
		private const string CapturedPacketHex =
			"000000397100ae7d000000170a070d5db9eca91058120a0d91f7878b10ffbdd9102015" + "0000c3500004de8a0000c35002165eff05010119000000000000";
	}
}
