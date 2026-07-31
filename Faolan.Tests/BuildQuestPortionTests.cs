using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #255: the multi-quest snapshot journal portion generates from the player's held quests. Golden: the
	// compeltefirstquest login snapshot's quest region (2 quests - beach 10238 + Casilda 13479), extracted
	// byte-exact (QuestPortion2QuestGolden.bin). Proves BuildQuestPortion reproduces a real retail multi-quest
	// region, so driving it from CharacterQuests is safe to feed the client (which rejects a malformed region).
	public class BuildQuestPortionTests
	{
		private const uint CaptureInstance = 0x021663DD; // Stihetfv's client instance (the goal records carry it)
		private static readonly string DbPath = TestPaths.FaolanDb;

		// A character holding NO snapshot quests (a freshly created char, or one who completed/abandoned the beach
		// starter) carries the empty quest region. Byte-exact across all 20 *-create captures (the starter quest is
		// delivered LIVE on create-world entry, not in the snapshot). See tools/capture/decode_snapshot_quests.py.
		[Fact]
		public void BuildEmptyQuestPortion_MatchesTheCreateCaptureRegion()
		{
			byte[] golden =
			[
				0x00, 0x00, 0x00, 0x18, // version 0x18
				0x00, 0x00, 0x03, 0xF1, // empty Quest_c list ((0+1)*0x3F1)
				0x00, 0x00, 0x03, 0xF1, // index tag
				0x00, 0x00, 0x03, 0xF1, // empty index marker ((0+1)*0x3F1)
				0x00, 0x00, 0x00, 0x00, // index count = 0
				0x00, 0x01, 0x56, 0xE7 // terminal constant
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
			var golden = Golden.Bytes("QuestPortion2QuestGolden");

			var diff = -1;
			for (var i = 0; i < Math.Min(golden.Length, built.Length); i++)
			{
				if (golden[i] != built[i])
				{
					diff = i;
					break;
				}
			}

			Assert.True(golden.SequenceEqual(built), $"2-quest region must equal the capture; lengths golden={golden.Length} built={built.Length}; first diff @{diff}");
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

		// Casilda 13479's journal record is a per-quest CONSTANT (byte-identical across its active + completed login
		// captures, #255). Generate it via BuildSingle from the DB and assert it matches the record in the captured
		// 2-quest region byte-for-byte - the core proof that a non-beach quest's record generates correctly. (The full
		// region isn't byte-compared: the capture's char COMPLETED the beach quest, so its beach record carries
		// obj-list progress a held-but-fresh beach record doesn't - that record is proven separately + in production.)
		[Fact]
		public async Task BuildSingle_Casilda13479_MatchesTheCapturedRecord_ByteForByte()
		{
			var golden = Golden.Bytes("QuestPortion2QuestGolden");
			// the 13479 record's signature in the region: [listKey=0x34A7][ver=1][questId=0x34A7]
			var sig = new byte[] { 0, 0, 0x34, 0xA7, 0, 0, 0, 1, 0, 0, 0x34, 0xA7 };
			var off = IndexOf(golden, sig);
			Assert.True(off >= 0, "13479 record signature present in the golden region");

			var casilda = await LoadDefinition(13479);
			Assert.NotNull(casilda);
			var rec = QuestcRecords.ForQuest(casilda, casilda.Level);
			var built = QuestcRecordCodec.Build(rec);

			var slice = golden.AsSpan(off, built.Length).ToArray();
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
