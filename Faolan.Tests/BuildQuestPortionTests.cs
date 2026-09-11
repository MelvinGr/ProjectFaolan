using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The multi-quest snapshot journal portion generates from the player's held quests. Golden: the
	// compeltefirstquest login snapshot's quest region (2 quests - beach 10238 + Casilda 13479), extracted
	// byte-exact (QuestPortion2QuestGolden.bin). Proves BuildQuestPortion reproduces a real retail multi-quest
	// region, so driving it from CharacterQuests is safe to feed the client (which rejects a malformed region).
	public class BuildQuestPortionTests
	{
		private const uint CaptureInstance = 0x021663DD; // Stihetfv's client instance (the goal records carry it)

		// The SHA-256 of the compeltefirstquest login snapshot's 2,247-byte quest region. The retail blob itself is
		// not carried in the repo, so the digest is the anchor.
		private const string CapturedRegionSha256 = "9c6dc1223caac6630ed3e1baeddf1b47f0df631f0866686a28c5586bc3121422";

		// The captured region's final word is the access-window table's ListFrame (86 catalog entries), and the digest
		// covers it. That word belongs to the table region, so it is re-appended before comparing against the pin.
		private static readonly byte[] AccessTableFrameWord = BuildFrameWord();

		private static byte[] BuildFrameWord()
		{
			var word = ListFrame.ForCount(AccessWindowCatalog.Entries.Count);
			return [(byte)(word >> 24), (byte)(word >> 16), (byte)(word >> 8), (byte)word];
		}
		private static readonly string DbPath = TestPaths.FaolanDb;

		// A character holding no snapshot quests (a freshly created char, or one who completed/abandoned the beach
		// starter) carries the empty quest region. Byte-exact across all 20 *-create captures (the starter quest is
		// delivered live on create-world entry, not in the snapshot). See tools/capture/decode_snapshot_quests.py.
		[Fact]
		public void BuildEmptyQuestPortion_MatchesTheCreateCaptureRegion()
		{
			byte[] golden =
			[
				0x00, 0x00, 0x00, 0x18, // version 0x18
				0x00, 0x00, 0x03, 0xF1, // empty Quest_c list ((0+1)*0x3F1)
				0x00, 0x00, 0x03, 0xF1, // empty completed-journal frame (the phantom "index tag")
				0x00, 0x00, 0x03, 0xF1, // empty index marker ((0+1)*0x3F1)
				0x00, 0x00, 0x00, 0x00 // index count = 0
				// The capture's next word, 0x000156E7, is not part of this region: it is the access-window
				// table's ListFrame(86), not a terminal constant of this one.
			];
			Assert.Equal(golden, SnapshotTails.EmptyQuestPortion());
			// The empty-quest list/null-primary path must route to the same region.
			Assert.Equal(golden, SnapshotTails.QuestPortion([], null!, 0));
		}

		// The whole 2-quest region (beach + Casilda) must reproduce the captured login snapshot's quest region
		// byte-for-byte - the proof that the multi-quest assembly (record order, the objective region between the
		// primary record and the others, the multi-quest index) is correct + client-valid.
		[Fact]
		public async Task BuildQuestPortion_ReproducesTheTwoQuestCapture_ByteForByte()
		{
			var beach = await LoadDefinition(10238);
			var casilda = await LoadDefinition(13479);
			Assert.NotNull(beach);
			Assert.NotNull(casilda);

			var built = SnapshotTails.QuestPortion([beach, casilda], beach, CaptureInstance);

			// The captured region's SHA-256 rather than the region: the blob is gone from the repo. This gate
			// only ever compared against those bytes - both inputs are DB rows - so the digest is the same assertion,
			// minus the ability to report a first-differing offset. Regenerate the pin only against a capture.
			// The pin covers 2247 bytes: this 2243-byte region plus the access-window table's ListFrame word, which
			// the table region owns. Re-appending it is what keeps the comparison against the retail bytes.
			Assert.Equal(2243, built.Length);
			Assert.Equal(CapturedRegionSha256, Sha256([.. built, .. AccessTableFrameWord]));
		}

		// Single-quest path must still equal the multi-quest portion for one held quest (regression guard for the
		// generalization).
		[Fact]
		public async Task BuildSingleQuestPortion_EqualsMultiQuestForm()
		{
			var beach = await LoadDefinition(10238);
			var built = SnapshotTails.QuestPortion([beach], beach, CaptureInstance);
			var single = QuestPortionOracle.BuildSingle(beach, beach.Level, CaptureInstance);
			Assert.True(single.SequenceEqual(built), "single-quest portion must equal the multi-quest form");
		}

		// Casilda 13479's journal record is a per-quest constant (byte-identical across its active + completed login
		// captures). Generate it via BuildSingle from the DB and assert it matches the record in the captured
		// 2-quest region byte-for-byte - the core proof that a non-beach quest's record generates correctly. (The full
		// region isn't byte-compared: the capture's char completed the beach quest, so its beach record carries
		// obj-list progress a held-but-fresh beach record doesn't - that record is proven separately + in production.)
		[Fact]
		public async Task BuildSingle_Casilda13479_MatchesTheCapturedRecord_ByteForByte()
		{
			// The region the test above pins against retail's digest is the captured region, so searching it for the
			// 13479 record is still a comparison against a capture - one hop, not a self-check. (If that gate fails
			// this one is meaningless, but it fails loudly first.)
			var region = SnapshotTails.QuestPortion([await LoadDefinition(10238), await LoadDefinition(13479)], await LoadDefinition(10238), CaptureInstance);
			Assert.Equal(CapturedRegionSha256, Sha256([.. region, .. AccessTableFrameWord]));

			// the 13479 record's signature in the region: [listKey=0x34A7][ver=1][questId=0x34A7]
			var sig = new byte[] { 0, 0, 0x34, 0xA7, 0, 0, 0, 1, 0, 0, 0x34, 0xA7 };
			var off = IndexOf(region, sig);
			Assert.True(off >= 0, "13479 record signature present in the golden region");

			var casilda = await LoadDefinition(13479);
			Assert.NotNull(casilda);
			var rec = QuestcRecords.ForQuest(casilda, casilda.Level);
			var built = QuestcRecordCodec.Build(rec);

			var slice = region.AsSpan(off, built.Length).ToArray();
			var diff = -1;
			for (var i = 0; i < built.Length; i++)
			{
				if (slice[i] != built[i])
				{
					diff = i;
					break;
				}
			}

			Assert.True(slice.SequenceEqual(built), $"13479 record must match the capture; first diff @{diff} of {built.Length}");
		}

		// The beach starter must be able to ride the login snapshot. Retail ships the journal in the snapshot and sends
		// no quest packet at login, so a quest that is not snapshot-eligible simply does not exist in the J-panel after
		// a relog - which is what "enter the jungle is gone after relogging" was: the quest row was Active the whole
		// time, but IsSnapshotEligible said no, so the region shipped empty and the live restore path had been deleted
		//. Every new character holds this quest, so it is the one that must not regress.
		[Fact]
		public async Task TheBeachStarter_IsSnapshotEligible_SoItSurvivesARelog()
		{
			await using var db = new DatabaseContext(TestDb.Options(DbPath));
			var def = await db.QuestDefinitions.Include(q => q.Goals).Include(q => q.TextRefs)
				.FirstAsync(q => q.QuestId == 10238, TestContext.Current.CancellationToken);
			var area = await db.QuestLines.FirstAsync(a => a.QuestLineId == def.QuestLineId, TestContext.Current.CancellationToken);

			var lineSteps = await db.QuestDefinitions.CountAsync(q => q.QuestLineId == def.QuestLineId, TestContext.Current.CancellationToken);

			Assert.True(def.ObjectiveRegionVerified, "10238's @609 objective region must be verified byte-exact against the captures");
			Assert.True(SnapshotTails.IsSnapshotEligible(def, area, lineSteps), "the beach starter must ride the login snapshot");

			// And the region it produces must be non-empty - eligibility is worthless if the portion comes out as the
			// empty-quest form the create path uses.
			var portion = SnapshotTails.QuestPortion([def], def, CaptureInstance);
			Assert.NotEqual(SnapshotTails.EmptyQuestPortion(), portion);
		}

		// One quest further along the same chain: `The Scavenger's Key` has no captured objective region - only 103 of
		// 5,049 quests do - so `ObjectiveRegionVerified` can never speak for it. Without this path a quest that is
		// Active in CharacterQuests is absent from the client, and the Casilda line reads as finished after a relog.
		[Fact]
		public async Task AQuestWithNoCapturedRegion_StillRidesTheSnapshot_WhenItsGoalsStateTheirKind()
		{
			await using var db = new DatabaseContext(TestDb.Options(DbPath));
			var def = await db.QuestDefinitions.Include(q => q.Goals).Include(q => q.TextRefs)
				.FirstAsync(q => q.QuestId == 13563, TestContext.Current.CancellationToken);
			var area = await db.QuestLines.FirstAsync(a => a.QuestLineId == def.QuestLineId, TestContext.Current.CancellationToken);

			Assert.False(def.ObjectiveRegionVerified, "13563 has no captured region - if this flips, the test is no longer covering the buildable path");
			Assert.True(SnapshotTails.IsObjectiveRegionBuildable(def), "every non-partial goal of 13563 states its wire kind, so its region is buildable");
			var lineSteps = await db.QuestDefinitions.CountAsync(q => q.QuestLineId == def.QuestLineId, TestContext.Current.CancellationToken);
			Assert.True(SnapshotTails.IsSnapshotEligible(def, area, lineSteps), "13563 must ride the login snapshot or its line reads as finished");
			Assert.NotEqual(SnapshotTails.EmptyQuestPortion(), SnapshotTails.QuestPortion([def], def, CaptureInstance));
		}

		// The index block states the held quest's step number in its line, and that number is the capture-sourced
		// JournalSequence - 0 on the 1,009 lines no capture has ordered. A multi-step line with no order would ship as
		// "step 0, all n still to come", which no capture shows, so the gate refuses the quest until its line is ordered.
		// A single-step line has nothing to order, and its quest still rides.
		[Fact]
		public async Task AQuestOnAMultiStepLine_WithNoJournalSequence_WaitsForItsLinesOrder()
		{
			await using var db = new DatabaseContext(TestDb.Options(DbPath));
			var def = await db.QuestDefinitions.Include(q => q.Goals).Include(q => q.TextRefs)
				.FirstAsync(q => q.QuestId == 10238, TestContext.Current.CancellationToken);
			var area = await db.QuestLines.FirstAsync(a => a.QuestLineId == def.QuestLineId, TestContext.Current.CancellationToken);
			var lineSteps = await db.QuestDefinitions.CountAsync(q => q.QuestLineId == def.QuestLineId, TestContext.Current.CancellationToken);
			Assert.True(lineSteps > 1 && def.JournalSequence != 0, "the beach starter sits at a known step of a multi-step line - the control this test needs");
			Assert.True(SnapshotTails.IsSnapshotEligible(def, area, lineSteps));

			def.JournalSequence = 0;
			Assert.False(SnapshotTails.IsSnapshotEligible(def, area, lineSteps), "no order on a multi-step line: the block would state step 0");
			Assert.True(SnapshotTails.IsSnapshotEligible(def, area, 1), "a single-step line has nothing to order");
		}

		// The bar for that widening, and the reason it is not "the builder produced some bytes": a goal with no
		// captured kind builds `K = 0`, which the client rejects - and a region it cannot parse takes the whole login
		// down (statlist.cpp:134), so this must stay strict. Partial goals are excluded, matching what
		// QuestObjectiveRegions.ForQuest leaves out of the region.
		[Fact]
		public void ObjectiveRegionBuildable_NeedsEveryLiveGoalToStateItsKind()
		{
			var withKind = new QuestGoalDefinition { GoalId = 1, Separator = 23 };
			var kindless = new QuestGoalDefinition { GoalId = 2, Separator = null };
			var kindlessButPartial = new QuestGoalDefinition { GoalId = 3, Separator = null, Partial = true };

			Assert.True(SnapshotTails.IsObjectiveRegionBuildable(new QuestDefinition { Goals = [withKind] }));
			Assert.True(SnapshotTails.IsObjectiveRegionBuildable(new QuestDefinition { Goals = [withKind, kindlessButPartial] }),
				"a partial goal is not in the region, so it cannot disqualify the quest");

			Assert.False(SnapshotTails.IsObjectiveRegionBuildable(new QuestDefinition { Goals = [withKind, kindless] }),
				"one kindless live goal builds K=0 and the client rejects the region");
			Assert.False(SnapshotTails.IsObjectiveRegionBuildable(new QuestDefinition { Goals = [kindlessButPartial] }),
				"a quest whose only goal is partial has an empty region");
			Assert.False(SnapshotTails.IsObjectiveRegionBuildable(new QuestDefinition()));
			Assert.False(SnapshotTails.IsObjectiveRegionBuildable(null));
		}

		// @+0x2c carries the series head, not the quest's own id. A standalone quest is its own head, so the
		// two agree for 84 of the 99 captured records and the distinction only shows on a chain member - which is why
		// emitting `def.QuestId` unconditionally looked right for years. 12407 (escort Casilda) is the clearest case:
		// its head is 13479, the "kill Scavenger Tuut" quest that starts that chain.
		[Theory]
		[InlineData(12407u, 13479u)] // escort Casilda -> the chain's first quest
		[InlineData(10915u, 10238u)] // a Shipwrecked step -> the beach starter
		[InlineData(12371u, 10238u)]
		[InlineData(10238u, 10238u)] // the head itself is its own head
		[InlineData(13479u, 13479u)]
		public async Task QuestcRecord_CarriesTheSeriesHead(uint questId, uint expectedHead)
		{
			var def = await LoadDefinition(questId);
			Assert.Equal(expectedHead, def.ChainHeadQuestId);
			Assert.Equal(expectedHead, QuestcRecords.ForQuest(def, def.Level).Field2c);
		}

		private static string Sha256(byte[] body)
		{
			return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
		}

		private static int IndexOf(byte[] hay, byte[] needle)
		{
			for (var i = 0; i + needle.Length <= hay.Length; i++)
			{
				if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
					return i;
			}

			return -1;
		}

		private static async Task<QuestDefinition> LoadDefinition(uint questId)
		{
			var options = TestDb.Options(DbPath);
			await using var ctx = new DatabaseContext(options);
			return (await ctx.QuestDefinitions.Include(q => q.TextRefs).Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == questId))!;
		}
	}
}
