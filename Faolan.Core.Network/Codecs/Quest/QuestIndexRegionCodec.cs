using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The QUEST INDEX region of the 0x737A6DF9 tails - the bytes between the COMPLETED-quest journal
	// (CompletedQuestJournalRegionCodec) and the access-window table. Decoded byte-exact from the beach template, the
	// 2-quest capture (#255) AND every progressed journal in the corpus (5..30 active quests, #391), and re-carved
	// to the CLIENT's own reader in #464 (`MainQuest_c`, `FUN_00922210` - five head words, the pair list, then a
	// four-word trailer):
	//   [u32 (count + 1) * 0x3F1]                 the id-list framing (count = number of held/active quests)
	//   [u32 questId × count]                     the held quest ids
	//   [u32 count]                               a PLAIN count (the client's MainQuest_c list uses one)
	//   ONE goal-table BLOCK per held quest, in the same order as the ids:
	//     [u32 areaId]                            the quest's QuestLineId - the block's OWN first word. The old carve
	//                                             hung it off the PREVIOUS block as a "chainWord" (and block 1's off
	//                                             the region head as "leadAreaId"), which manufactured a chain grammar
	//                                             and a phantom "terminal word": the last block's "chainWord" was the
	//                                             ACCESS-WINDOW TABLE's ListFrame(86) == 0x156E7, constant only
	//                                             because every capture carries the same 86-entry catalog.
	//     [u32 magic]                             the AREA's goal-table NAME word ("W6QT" 0x57365154, "RRL3" ...)
	//     [u32 stepNumber][u32 stepsAfter][u32 tf3]
	//                                             the held quest's 1-based step in its line and the number of steps
	//                                             AFTER it (their sum is the line's length); the client's journal
	//                                             draws them as Roman numerals (#926). PER-CHARACTER progress, not a
	//                                             line constant - it read as `[1][count-1]` for as long as every
	//                                             captured character stood on step 1. tf3 == 0 across the corpus
	//     [u32 n][ (questId, sequence) × n ]      the AREA's full quest catalog; sequence is 1-based but NOT strictly
	//                                             monotonic (branch/chapter quests repeat a sequence - so the old
	//                                             "idx == i+1" assertion was a beach-only accident)
	//     [u32 trailerQuestId][u32 a][u32 b][u32 c]
	//                                             trailerQuestId = the AREA's anchor quest; a/b/c vary per quest
	//                                             (beach 1/0/0)
	//
	// ⚠️ There is NO leading 0x3F1 "IndexTag" (that phantom was the empty completed-journal frame) and NO terminal
	// word: an EMPTY index is `[0x3F1][u32 0]` and the region simply ends. The next bytes belong to the
	// access-window table (PfAccessWindowEntryCodec), whose frame word the old grammar consumed as its "terminal".
	public static class QuestIndexRegionCodec
	{
		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestIndexRegion value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestIndexRegion Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestIndexRegionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestIndexRegion Read(ref SpanReader r)
		{
			// The count is the frame's count - read exactly that many ids rather than peeking, so a held quest id
			// below 0x1000 (or an area id above it) cannot mis-split the list.
			var declared = r.ReadListCount();
			var questIds = new List<uint>(declared);
			for (var i = 0; i < declared; i++)
				questIds.Add(r.ReadU32Be());

			var at = r.Position;
			var count = r.ReadU32Be();
			if (count != questIds.Count)
				throw CodecParseException.Unexpected(nameof(QuestIndexRegionCodec), at, $"count == the frame-derived {questIds.Count}", count.ToString());

			var blocks = new List<QuestIndexRegion.Block>((int)count);
			for (uint b = 0; b < count; b++)
			{
				var areaId = r.ReadU32Be();
				var magic = r.ReadU32Be();
				var f1 = r.ReadU32Be();
				var f2 = r.ReadU32Be();
				var f3 = r.ReadU32Be();

				var n = r.ReadU32Be();
				var areaQuestIds = new List<uint>((int)n);
				var areaQuestSequences = new List<uint>((int)n);
				for (uint i = 0; i < n; i++)
				{
					areaQuestIds.Add(r.ReadU32Be());
					areaQuestSequences.Add(r.ReadU32Be());
				}

				blocks.Add(new QuestIndexRegion.Block
				{
					AreaId = areaId,
					Magic = magic,
					StepNumber = f1,
					StepsAfter = f2,
					TableField3 = f3,
					AreaQuestIds = areaQuestIds,
					AreaQuestSequences = areaQuestSequences,
					TrailerQuestId = r.ReadU32Be(),
					TrailerA = r.ReadU32Be(),
					TrailerB = r.ReadU32Be(),
					TrailerC = r.ReadU32Be()
				});
			}

			return new QuestIndexRegion
			{
				QuestIds = questIds,
				Blocks = blocks
			};
		}

		internal static void Write(FaolanStream s, QuestIndexRegion v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(ListFrame.ForCount(v.QuestIds.Count));
			foreach (var id in v.QuestIds)
				s.WriteUInt32(id);

			s.WriteUInt32((uint)v.Blocks.Count);

			foreach (var b in v.Blocks)
			{
				if (b.AreaQuestIds.Count != b.AreaQuestSequences.Count)
					throw new InvalidOperationException($"Block {b.TrailerQuestId}: {b.AreaQuestIds.Count} area quest ids but {b.AreaQuestSequences.Count} sequences.");

				s.WriteUInt32(b.AreaId);
				s.WriteUInt32(b.Magic);
				s.WriteUInt32(b.StepNumber);
				s.WriteUInt32(b.StepsAfter);
				s.WriteUInt32(b.TableField3);

				s.WriteUInt32((uint)b.AreaQuestIds.Count);
				for (var i = 0; i < b.AreaQuestIds.Count; i++)
				{
					s.WriteUInt32(b.AreaQuestIds[i]);
					s.WriteUInt32(b.AreaQuestSequences[i]);
				}

				s.WriteUInt32(b.TrailerQuestId);
				s.WriteUInt32(b.TrailerA);
				s.WriteUInt32(b.TrailerB);
				s.WriteUInt32(b.TrailerC);
			}
		}
	}

	// The quest index: the held quest ids plus one area-catalog block per held quest.
	public sealed record QuestIndexRegion
	{
		public List<Block> Blocks { get; init; } = [];
		public List<uint> QuestIds { get; init; } = [];

		// One held quest's area-catalog table. The client's MainQuest_c (FUN_00922210).
		public sealed record Block
		{
			// The quest's QuestLineId - the block's first wire word. Read as the previous block's "chainWord" (and,
			// for block 1, as the region's "leadAreaId" - the beach's 241 == 0xF1 "sentinel") until #464.
			public uint AreaId { get; init; }

			// The line's seq-1 quest on every captured block. The client reader (`FUN_00922210`) matches it into the
			// pair list and stores it at MainQuest_c +0x38 - the line's current-quest slot, the SAME one the
			// MainQuestUpdateIIR's TrackedQuestId fills on the incremental path (#464).
			public uint TrailerQuestId { get; init; }

			// The AREA's full quest catalog, ordered by each quest's JournalSequence - NOT this quest's goal ids, which
			// is what "GoalSteps" claimed until 2026-07-20. Proven: quest 10238's list is
			// [10238,12369,12373,10320,12371,10286,10287,10915], exactly area 241's 8 quests, while its own goals are
			// [1448,3490]. A FRESH character with no completed quests carries the whole list, so it is static area
			// data rather than anything journal-derived.
			//
			// On the wire each entry is a (questId, sequence) pair. AreaQuestSequences holds the sequence parallel to
			// AreaQuestIds; the sequence is 1-based but NOT strictly monotonic (branch/chapter quests repeat one), so
			// it must be carried, not regenerated as i+1 (#391). Write requires the two lists to be the same length.
			public List<uint> AreaQuestIds { get; init; } = [];
			public List<uint> AreaQuestSequences { get; init; } = [];
			public uint Magic { get; init; } // the area's goal-table name word (W6QT / RRL3 / ...)

			// `MainQuest_c +0x30` / `+0x34` (#926): the HELD quest's 1-based step number within its line, and the number of
			// steps after it - so StepNumber + StepsAfter == AreaQuestIds.Count, and on every one of the 2,626 captured
			// blocks StepNumber is exactly the held quest's own AreaQuestSequences entry. The reader is the journal's
			// line panel (`FUN_01617260`): it takes +0x30 as a running counter, formats it through the Roman-numeral
			// converter (`FUN_010cfbc0`, the I/II/III the journal shows for a line's chapters) for the current step, then
			// draws +0x34 more placeholder rows for the steps still to come and hides that section when +0x34 is 0.
			//
			// ⚠️ These are per-CHARACTER progress, the only two words in the block that are. They were recorded as
			// per-quest constants (`[1][count-1]`) because every captured character stood on step 1 of the lines the
			// early corpus covered; the beach line alone now shows 1/3/5/8 across characters. Generation derives them
			// from the held quest's JournalSequence (QuestIndexRegions.ForQuests), which is why it stayed byte-exact.
			public uint StepNumber { get; init; }
			public uint StepsAfter { get; init; }

			// MainQuest_c +0x44 - the SAME slot MainQuestUpdateIIR.Unknown1 lands in (the IIR path routes it through
			// the `FUN_00921980` ctor's third argument; the snapshot filler `FUN_00922210` stores this word there).
			// Stored, written back out by `MainQuest_c::WriteToStream`, read by nothing; 0 on all 97 captured blocks
			// and all 46 captured IIR lists (#464).
			public uint TableField3 { get; init; }

			// The block's trailer words after the catalog: [trailerQuestId][a][b][c]. Each is a `MainQuest_c` field
			// (`FUN_00922210` stores them at +0x38 / +0x48 / +0x4c / +0x50), and the last three are the slots the
			// incremental MainQuestUpdateIIR fills for the same line - TrailerA == its Unknown0,
			// TrailerC == its Unknown2 (the IIR carries no TrailerB; the wire ctor parks the area token there).
			// So the per-field facts live with the IIR record (MainQuestUpdateIIR remarks): TrailerA is the line's
			// solo(1)/raid(4)/dungeon(8) category, consumed as the quest tracker's sort tiebreaker; TrailerB is 0 on
			// every captured block; TrailerC is a per-line flag, 1 on 6 of 97 captured lines, and the SAME flag the
			// completed journal's line group carries (CompletedQuestJournalRegion.AreaGroup.LineFlag - the two agree on
			// every line seen in both, #926). Constant per LINE, not per quest - carried, not assumed (#391/#464).
			public uint TrailerA { get; init; } = 1;
			public uint TrailerB { get; init; }
			public uint TrailerC { get; init; }
		}
	}
}
