using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.FullCharSnapshot;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #74/#71: the per-character KNOWN-SPELL list carried in the 0x737A6DF9 FullChar snapshot tails (the spellbook
	// region). Ground truth = the Faolanx (clean fresh necromancer) login snapshot, whose list is exactly its 2
	// create-spells (count 0x0BD3, records 0x42CA77 + 0x125D9B, both type 2). See
	// docs/handoff-2026-06-20-snapshot-known-spell-list.md.
	public class KnownSpellListTests
	{
		private const string DbPath = "/home/melvin/Development/C#/Project Faolan/faolan.db";
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan.Tests/Assets/BeachTailsTemplate.bin";
		private const uint BeachStarterQuestId = 10238;
		private const uint TemplateInstance = 0x02165EFF;

		// The captured count word + 2 records from Faolanx's snapshot (pkt51 @0x25F6): the byte-exact target shape.
		private static readonly byte[] FaolanxKnownListGolden = Convert.FromHexString("00000bd30042ca77ffffffff0000000200125d9bffffffff00000002");

		private static readonly uint[] FaolanxSpellIds = [0x42CA77, 0x125D9B];

		// Injecting an empty list must leave the from-scratch tails byte-identical to the captured beach template, so
		// the existing golden (and a fresh character with no spells) is never disturbed.
		[Fact]
		public async Task BuildFromScratch_NoSpells_StillReproducesTheTemplate()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var built = SnapshotTailsBuilder.BuildFromScratch(def, null, TemplateInstance, null, []);

			var template = File.ReadAllBytes(TemplatePath);
			Assert.True(template.SequenceEqual(built), "an empty known-list must equal BeachTailsTemplate.bin byte-for-byte");
		}

		// Splicing the 2 necro create-spells into the from-scratch tails: the known-list count + records must equal the
		// captured Faolanx shape, the tails grow by exactly 2 records (24 bytes), and everything outside the spliced
		// region is unchanged from the empty build (so we only touched the spellbook slot).
		[Fact]
		public async Task BuildFromScratch_WithNecroSpells_SplicesTheCapturedList()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var empty = SnapshotTailsBuilder.BuildFromScratch(def, null, TemplateInstance, null, []);
			var withSpells = SnapshotTailsBuilder.BuildFromScratch(def, null, TemplateInstance, null, FaolanxSpellIds);

			// Two 12-byte records spliced in.
			Assert.Equal(empty.Length + 24, withSpells.Length);

			// The empty list's count word (0x3F1) is found in the empty build by its 16-byte landmark
			// [0x566][0][0x290][0]; in the populated build that slot carries the captured count + 2 records.
			var landmark = Convert.FromHexString("00000566" + "00000000" + "00000290" + "00000000" + "000003f1");
			var countOffset = IndexOf(empty, landmark);
			Assert.True(countOffset >= 0, "the empty known-list slot must be locatable by its landmark");
			var slotStart = countOffset + 16; // past the 16-byte landmark to the count word

			// Everything BEFORE the count word is identical between the two builds.
			Assert.True(empty.AsSpan(0, slotStart).SequenceEqual(withSpells.AsSpan(0, slotStart)), "bytes before the known-list must be unchanged");

			// The populated build carries the captured count + records at the slot.
			Assert.True(withSpells.AsSpan(slotStart, FaolanxKnownListGolden.Length).SequenceEqual(FaolanxKnownListGolden), "the spliced known-list must equal the captured Faolanx bytes");

			// Everything AFTER the list (the empty build's trailing bytes) is preserved, just shifted by 24.
			Assert.True(empty.AsSpan(slotStart + 4).SequenceEqual(withSpells.AsSpan(slotStart + FaolanxKnownListGolden.Length)), "bytes after the known-list must be preserved (shifted by the records)");
		}

		[Fact]
		public void EmptyList_IsJustTheFrameCountWord()
		{
			var empty = KnownSpellList.FromSpellIds([]).ToArray();
			Assert.Equal(Convert.FromHexString("000003f1"), empty);
		}

		[Fact]
		public void RoundTrip_CapturedList_IsLossless()
		{
			var parsed = KnownSpellList.Parse(FaolanxKnownListGolden, 0, out var consumed);
			Assert.Equal(FaolanxKnownListGolden.Length, consumed);
			Assert.Equal(2, parsed.Records.Count);
			Assert.Equal(new KnownSpellRecord(0x42CA77, 2), parsed.Records[0]);
			Assert.Equal(new KnownSpellRecord(0x125D9B, 2), parsed.Records[1]);
			Assert.Equal(FaolanxKnownListGolden, parsed.ToArray());
		}

		[Fact]
		public void ToArray_FreshNecro_MatchesTheCapturedFaolanxList()
		{
			var built = KnownSpellList.FromSpellIds(FaolanxSpellIds).ToArray();
			Assert.Equal(FaolanxKnownListGolden, built);
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
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={DbPath}").Options;
			var repo = new DatabaseRepository(new Factory(options));
			return await repo.GetQuestDefinition(questId);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}
		}
	}
}
