using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// The snapshot tails BEHIND the access-window table (Gitea #393) - 1.3-2.7 KB per subclass, the largest region of
	// the login snapshot that was ever carried as an opaque byte[].
	//
	// It is a chain of 0x3F1-framed lists closed by TWO plainly-counted ones, recovered from the client's reader
	// rather than guessed: see SubClassPostAccessBlockCodec for the function-by-function map. Two errors in the old
	// write-up fell out of that - SpellBook_t's wire record is 12 bytes (the documented 0x14 is the in-memory
	// stride), and the final region is two lists, the first with a 17-byte element.
	//
	// These gates used to run against a SECOND codec over the same bytes (`SnapshotTailRegionCodec`, a generic
	// backtracking walk that reported lists as raw words) while the block itself was carried as a flat `uint[]`.
	// #543 collapsed the two into this one typed model, so the census below now reads the same fields production
	// does.
	public class SubClassPostAccessBlockTests
	{
		// The bar for a decoded region: it parses to the exact last byte, and what the reader took apart the writer
		// puts back byte for byte - across every capture that contains it. The block is the sub-class's LAST region
		// and is read sequentially (the byte[] fallback it once sat beside is gone, #464), so "parses" is the
		// sub-class parsing at all, and the byte-exact half is the whole-snapshot gate
		// (ProgressedJournalSnapshotTests.EveryCapturedSnapshot_RoundTrips_ByteExact); what this census keeps is the
		// SHAPE of what it took apart.
		[Fact]
		public void EveryCapturedPostAccessBlock_ParsesAndReEncodesByteExact()
		{
			var shapes = new Dictionary<string, int>();
			var blocks = 0;

			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				blocks++;
				var parsed = sub.PostAccessBlock;

				Assert.Equal(SubClassPostAccessBlockCodec.Build(parsed), SubClassPostAccessBlockCodec.Build(SubClassPostAccessBlockCodec.Parse(SubClassPostAccessBlockCodec.Build(parsed))));

				var shape = $"d{parsed.Displayed.Pairs.Count},s{parsed.Sheet.Pairs.Count},k{parsed.KnownSpells?.Records.Count.ToString() ?? "-"}";
				shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
			}

			Assert.Equal(184, blocks);

			// Both stat lists are FIXED-SIZE across the corpus - 17 displayed stats and the sheet's 74 pairs twice.
			// What varies is only the spell list.
			Assert.All(shapes.Keys, shape => Assert.StartsWith("d17,s148,", shape));
		}

		// The spellbook list is OPTIONAL, not merely sometimes empty - the distinction the flat view could not make.
		// 21 of the 184 blocks end at StatList#2 and go straight to two zero-count tails; the other 163 carry the
		// list, empty (its bare 0x3F1 frame, 9 captures) or not. Absent and empty are different bytes, so they are
		// different in the model.
		//
		// ⚠️ It is the SESSION'S FIRST snapshot that lacks it - the one the create editor is sent - not "a create
		// capture's": 20 of the 21 come from a *create* capture but the twenty-first is the opening snapshot of a
		// login-named one. Both halves are asserted below because the first is what a name-based reading would get
		// wrong. The absence travels with EMPTY TAILS in every case, which is the wire-side statement of the same
		// fact and needs no capture name at all.
		[Fact]
		public void TheSpellBookList_IsAbsentOnlyInTheSessionsFirstSnapshot()
		{
			var firstOfCapture = new Dictionary<string, int>();
			var blocks = new List<(string Capture, int Packet, SubClassPostAccessBlock Block)>();

			foreach (var (source, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				var parsed = sub.PostAccessBlock;

				// `source` is "<capture id>#<packet>" (the opaque id, #465 - only the split on `#` matters here).
				var hash = source.LastIndexOf('#');
				var capture = source[..hash];
				var packet = int.Parse(source[(hash + 1)..]);

				blocks.Add((capture, packet, parsed));
				firstOfCapture[capture] = Math.Min(firstOfCapture.GetValueOrDefault(capture, int.MaxValue), packet);
			}

			var without = blocks.Where(b => b.Block.KnownSpells == null).ToList();
			Assert.Equal(21, without.Count);
			Assert.Equal(163, blocks.Count - without.Count);

			// Every spellbook-less block is its capture's FIRST snapshot, and closes with both tails empty.
			Assert.All(without, b => Assert.Equal(firstOfCapture[b.Capture], b.Packet));
			Assert.All(without, b => Assert.Empty(b.Block.WideTail));
			Assert.All(without, b => Assert.Empty(b.Block.PairTail));

			// And the converse, which is what makes the absence readable off the wire alone: a block that carries the
			// list always carries a non-empty wide tail.
			Assert.All(blocks.Where(b => b.Block.KnownSpells != null), b => Assert.NotEmpty(b.Block.WideTail));
		}

		// ⚠️ THE STAT TABLE LABELS ITSELF. #393 asks for the PostAccessBlock's slots to be labelled; the slots carry
		// their own ids on the wire, so there is nothing to label by position. Every one of the 22,348 stat ids in
		// the corpus is a defined CharacterStatId - and the sheet is literally TWO IDENTICAL COPIES of the same run,
		// which is what SubClassPostAccessBlock.HasRepeatedStatTable reports and why a vitals patch writes both.
		[Fact]
		public void TheStatTable_IsSelfLabellingAndDoubled()
		{
			var ids = 0;
			var unknown = new HashSet<uint>();
			var names = new HashSet<string>();
			var blocks = 0;

			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				var parsed = sub.PostAccessBlock;
				blocks++;

				// 148 pairs = 74 pairs, twice. The halves match in ids AND values on every capture.
				Assert.Equal(148, parsed.Sheet.Pairs.Count);
				Assert.True(parsed.HasRepeatedStatTable);

				foreach (var (id, _) in parsed.Displayed.Pairs.Concat(parsed.Sheet.Pairs))
				{
					ids++;
					if (Enum.IsDefined(typeof(CharacterStatId), id))
						names.Add(((CharacterStatId)id).ToString());
					else
						unknown.Add(id);
				}
			}

			Assert.Equal(184, blocks);
			Assert.Equal(184 * (17 + 148), ids);

			// NOT ONE unknown id across the corpus - the catalog already covers everything these lists name.
			Assert.Empty(unknown);
			Assert.Contains("HealthMax", names);
			Assert.Contains("HealthCurrent", names);
			Assert.Contains("StaminaMax", names);
		}

		// The 12-byte records are the known spells/feats of #74: the middle word is the "no bound bar slot" sentinel
		// and the last is the type. That is what tells the spell list apart from a stat list, and it is why the
		// spellbook's presence is decidable rather than a coin flip.
		[Fact]
		public void TheSpellBookRecords_AreKnownSpellsAndFeats()
		{
			var records = 0;
			var types = new HashSet<byte>();

			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				foreach (var record in sub.PostAccessBlock.KnownSpells?.Records ?? [])
				{
					records++;
					types.Add(record.Type);
				}
			}

			Assert.True(records > 2000, $"only {records} spell records - the corpus slice is wrong");
			Assert.Equal([KnownSpellListCodec.SpellType, KnownSpellListCodec.FeatType], types.Order().ToArray());
		}
	}
}
