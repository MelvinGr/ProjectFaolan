using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using System.Linq;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// The snapshot tails BEHIND the access-window table (Gitea #393) - `UnknownAfterAccessWindowTable`, 1.3-2.7 KB
	// per subclass, the largest region of the login snapshot that was still carried as an opaque byte[].
	//
	// It is a chain of 0x3F1-framed lists closed by TWO plain-counted ones, recovered from the client's reader
	// rather than guessed: see SnapshotTailRegion for the function-by-function map. Two errors in the old write-up
	// fell out of that - SpellBook_t's wire record is 12 bytes (the documented 0x14 is the in-memory stride), and
	// the final region is two lists, the first with a 17-byte element.
	public class SnapshotTailRegionTests
	{
		// The bar for a decoded region: it walks to the exact last byte, and what the reader took apart the writer
		// puts back byte for byte - across every capture that contains it.
		[Fact]
		public void EveryCapturedTailRegion_WalksAndReEncodesByteExact()
		{
			var shapes = new Dictionary<string, int>();
			var regions = 0;
			var unparsed = 0;

			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				var region = sub.UnknownAfterAccessWindowTable ?? [];
				if (region.Length == 0)
					continue;

				regions++;
				if (!SnapshotTailRegion.TryParse(region, out var parsed))
				{
					unparsed++;
					continue;
				}

				Assert.Equal(region, parsed.ToArray());
				shapes[string.Join(",", parsed.Lists.Select(l => $"f{l.Stride}x{l.Words.Count / (l.Stride / 4)}"))] =
					shapes.GetValueOrDefault(string.Join(",", parsed.Lists.Select(l => $"f{l.Stride}x{l.Words.Count / (l.Stride / 4)}"))) + 1;
			}

			Assert.Equal(151, regions);
			Assert.Equal(0, unparsed);

			// Every capture opens the same way - two empty lists (D and E, which carry variable-width records and are
			// empty in every capture), then List F's 17 records, then the first StatList_t's 148 pairs. What varies
			// after that is the second stat list and the spell list, which is the per-character part.
			Assert.All(shapes.Keys, shape => Assert.StartsWith("f8x0,f8x0,f8x17,f8x148", shape));
			Assert.Equal(10, shapes.Count);
		}

		// ⚠️ THE STAT TABLE LABELS ITSELF. #393 asks for the PostAccessBlock's slots to be labelled; the slots carry
		// their own ids on the wire, so there is nothing to label by position. Every one of the 22,348 stat ids in
		// the corpus is a defined CharacterStatId - and the table is literally TWO IDENTICAL COPIES of the same run,
		// which is what SubClassPostAccessBlock.HasRepeatedStatTable detects and why a vitals patch writes both.
		[Fact]
		public void TheStatTable_IsSelfLabellingAndDoubled()
		{
			var ids = 0;
			var unknown = new HashSet<uint>();
			var names = new HashSet<string>();
			var tables = 0;

			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				if (!SnapshotTailRegion.TryParse(sub.UnknownAfterAccessWindowTable ?? [], out var parsed))
					continue;

				var table = parsed.StatTable;
				Assert.NotNull(table);
				tables++;

				// 148 pairs = 74 pairs, twice. The halves match in ids AND values on every capture.
				Assert.Equal(296, table.Words.Count);
				var half = table.Words.Count / 2;
				for (var i = 0; i < half; i++)
					Assert.Equal(table.Words[i], table.Words[half + i]);

				foreach (var pair in table.Pairs)
				{
					ids++;
					if (Enum.IsDefined(typeof(CharacterStatId), pair.Id))
						names.Add(((CharacterStatId)pair.Id).ToString());
					else
						unknown.Add(pair.Id);
				}
			}

			Assert.Equal(151, tables);
			Assert.Equal(151 * 148, ids);

			// NOT ONE unknown id across the corpus - the catalog already covers everything this table names.
			Assert.Empty(unknown);
			Assert.Contains("HealthMax", names);
			Assert.Contains("HealthCurrent", names);
			Assert.Contains("StaminaMax", names);
		}

		// The 12-byte lists are the known-spell/feat records of #74: the middle word is the "no bound bar slot"
		// sentinel and the last is the type. That is what tells a 12-byte list apart from an 8-byte stat list, and
		// it is why the walk's stride choice is decidable rather than a coin flip.
		[Fact]
		public void TheTwelveByteLists_AreKnownSpellRecords()
		{
			var records = 0;
			var types = new HashSet<uint>();

			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				if (!SnapshotTailRegion.TryParse(sub.UnknownAfterAccessWindowTable ?? [], out var parsed))
					continue;

				foreach (var list in parsed.Lists.Where(l => l.Stride == 12))
				{
					for (var i = 0; i < list.Words.Count; i += 3)
					{
						records++;
						Assert.Equal(KnownSpellList.Unbound, list.Words[i + 1]);
						types.Add(list.Words[i + 2]);
					}
				}
			}

			Assert.True(records > 2000, $"only {records} spell records - the corpus slice is wrong");
			Assert.Equal([KnownSpellList.SpellType, KnownSpellList.FeatType], types.Order().ToArray());
		}
	}
}
