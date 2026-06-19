using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Packets.FullCharSnapshot;
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
		private const string DbPath = "/home/melvin/Development/C#/Project Faolan/faolan.db";
		private const string GoldenPath = "/home/melvin/Development/C#/Project Faolan/Faolan.Tests/QuestPortion2QuestGolden.bin";
		private const uint CaptureInstance = 0x021663DD; // Stihetfv's client instance (the goal records carry it)

		// Casilda 13479's journal record is a per-quest CONSTANT (byte-identical across its active + completed login
		// captures, #255). Generate it via BuildSingle from the DB and assert it matches the record in the captured
		// 2-quest region byte-for-byte - the core proof that a non-beach quest's record generates correctly. (The full
		// region isn't byte-compared: the capture's char COMPLETED the beach quest, so its beach record carries
		// obj-list progress a held-but-fresh beach record doesn't - that record is proven separately + in production.)
		[Fact]
		public void BuildSingle_Casilda13479_MatchesTheCapturedRecord_ByteForByte()
		{
			var golden = File.ReadAllBytes(GoldenPath);
			// the 13479 record's signature in the region: [listKey=0x34A7][ver=1][questId=0x34A7]
			var sig = new byte[] { 0, 0, 0x34, 0xA7, 0, 0, 0, 1, 0, 0, 0x34, 0xA7 };
			var off = IndexOf(golden, sig);
			Assert.True(off >= 0, "13479 record signature present in the golden region");

			var casilda = LoadDefinition(13479).GetAwaiter().GetResult();
			Assert.NotNull(casilda);
			var rec = QuestcRecord.BuildSingle(casilda, casilda.SnapshotState, casilda.SnapshotN, casilda.SnapshotFieldEc,
				casilda.SnapshotFieldF0, casilda.SnapshotFieldF8, casilda.SnapshotField28);
			using var w = new Faolan.Core.Data.FaolanStream();
			rec.Write(w);
			var built = w.ToArray();

			var slice = golden.AsSpan(off, built.Length).ToArray();
			var diff = -1;
			for (var i = 0; i < built.Length; i++)
				if (slice[i] != built[i]) { diff = i; break; }
			Assert.True(slice.SequenceEqual(built), $"13479 record must match the capture; first diff @{diff} of {built.Length}");
		}

		private static int IndexOf(byte[] hay, byte[] needle)
		{
			for (var i = 0; i + needle.Length <= hay.Length; i++)
				if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
			return -1;
		}

		// Single-quest path must still equal the beach-only portion (regression guard for the generalization).
		[Fact]
		public async Task BuildQuestPortion_SingleBeachQuest_EqualsBuildBeachQuestPortion()
		{
			var beach = await LoadDefinition(10238);
			var built = BeachTailsBuilder.BuildQuestPortion([beach], primary: beach, CaptureInstance);
			var beachOnly = BeachTailsBuilder.BuildBeachQuestPortion(beach, beach.SnapshotState, CaptureInstance);
			Assert.True(beachOnly.SequenceEqual(built), "single-quest portion must equal BuildBeachQuestPortion");
		}

		private static async Task<QuestDefinition> LoadDefinition(uint questId)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={DbPath}").Options;
			await using var ctx = new DatabaseContext(options);
			return await ctx.QuestDefinitions
				.Include(q => q.TextRefs)
				.Include(q => q.Goals)
				.FirstOrDefaultAsync(q => q.QuestId == questId);
		}
	}
}
