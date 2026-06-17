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
	// #186: the WHOLE beach 0x737A6DF9 tails build FROM SCRATCH - no captured-tails template. BuildFromScratch
	// assembles the three regions (generated quest portion + the game-static access-window catalog asset + the code
	// stat skeleton) and reproduces BeachTailsTemplate.bin BYTE-FOR-BYTE for the captured beach character + instance.
	// Proving this means the .bin is no longer needed to author a beach world-entry snapshot's tails.
	public class BeachTailsFromScratchTests
	{
		private const string DbPath = "/home/melvin/Development/C#/Project Faolan/faolan.db";
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan/GameServer/Packets/FullCharSnapshot/BeachTailsTemplate.bin";
		private const uint BeachStarterQuestId = 10238;
		private const uint TemplateInstance = 0x02165EFF;

		[Fact]
		public async Task BuildFromScratch_ReproducesTheTemplate_ByteForByte()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			// character = null -> the stat skeleton stays at its (Strie) baseline, instance = the template's, so no
			// re-addressing: the assembly must equal the captured tails exactly.
			var built = BeachTailsBuilder.BuildFromScratch(def, character: null, TemplateInstance);

			var template = File.ReadAllBytes(TemplatePath);
			Assert.Equal(template.Length, built.Length);
			Assert.True(template.SequenceEqual(built), "from-scratch tails must equal BeachTailsTemplate.bin byte-for-byte");
		}

		// The captured-byte tests above only exercise the Strie baseline. The production flip (authoring the tails
		// instead of replaying the captured burst) relies on BuildFromScratch staying STRUCTURALLY valid for any
		// character - a different class/level patches different vitals into the stat block, and the snapshot framing
		// (the in-client hang risk) must survive that. This proves: the assembly re-parses, the three regions keep
		// their boundaries (quest portion = 1292, access-window catalog parses), the live vitals/XP land in the
		// right PostAccessBlock slots, and the round-trip is lossless - for a character that is NOT the capture.
		[Fact]
		public async Task BuildFromScratch_StaysValidForANonBaselineCharacter()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var character = new Character
			{
				Level = 7,
				Experience = 98760,
				MaxStats = new CharacterMaxStats { Health = 4321, Mana = 765, Stamina = 543 }
			};
			const uint live = 0x0802ABCD;

			var built = BeachTailsBuilder.BuildFromScratch(def, character, live);

			// Re-parses without throwing, and is byte-lossless through the typed model.
			var tails = SnapshotTails.Deserialize(built);
			Assert.True(built.SequenceEqual(tails.ToArray()), "non-baseline from-scratch tails must round-trip losslessly");

			// Region boundaries intact: the quest portion is the fixed beach length, the static access-window catalog parsed.
			Assert.Equal(BeachTailsBuilder.QuestPortionLength, tails.KnownHeaderLength + QuestRegionTrailerLength(tails));
			Assert.NotEmpty(tails.AccessWindowEntries);

			// The live character's vitals/XP landed in the decoded stat slots (the part the production flip changes).
			Assert.NotNull(tails.PostAccessBlock);
			Assert.Equal(4321u, tails.PostAccessBlock!.CurrentHp);
			Assert.Equal(765u, tails.PostAccessBlock.CurrentMana);
			Assert.Equal(543u, tails.PostAccessBlock.CurrentStamina);
			Assert.Equal(98760u, tails.PostAccessBlock.CurrentXp);
		}

		// The quest portion = the Quest_c journal (KnownHeaderLength) + the @609 objective region + the index region.
		private static int QuestRegionTrailerLength(SnapshotTails tails) =>
			(tails.QuestObjectives?.ToArray().Length ?? 0) + (tails.QuestIndex?.ToArray().Length ?? 0);

		[Fact]
		public async Task BuildFromScratch_ReAddressesInstance()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			const uint live = 0x0802ABCD;
			var built = BeachTailsBuilder.BuildFromScratch(def, character: null, live);

			// the template instance must be gone, the live one present (raw big-endian)
			var needleOld = new byte[] { 0x02, 0x16, 0x5E, 0xFF };
			var needleNew = new byte[] { 0x08, 0x02, 0xAB, 0xCD };
			Assert.False(Contains(built, needleOld), "template instance should be re-addressed away");
			Assert.True(Contains(built, needleNew), "live instance should be present");
		}

		private static bool Contains(byte[] hay, byte[] needle)
		{
			for (var i = 0; i + needle.Length <= hay.Length; i++)
				if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
					return true;
			return false;
		}

		private static async Task<QuestDefinition> LoadDefinition(uint questId)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={DbPath}").Options;
			var repo = new DatabaseRepository(new Factory(options));
			return await repo.GetQuestDefinition(questId);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new(options);
		}
	}
}
