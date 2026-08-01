using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The QUEST INDEX region of the 0x737A6DF9 tails - the bytes between the COMPLETED-quest journal
	// (CompletedQuestJournalRegionCodec) and the access-window table. Decoded byte-exact from the beach template, the
	// 2-quest capture (#255) AND every progressed journal in the corpus (5..30 active quests, #391):
	//   [u32 (count + 1) * 0x3F1]                 the id-list framing (count = number of held/active quests)
	//   [u32 questId × count]                     the held quest ids
	//   [u32 count]
	//   if count == 0:  [u32 terminal 0x000156E7]   (empty index - a char with no snapshot quests)
	//   else:
	//     [u32 leadAreaId]                        the FIRST block's quest QuestLineId (was mis-modelled as a 0x000000F1
	//                                             "sentinel" - constant only because every golden is a beach char,
	//                                             QuestLineId 241 == 0xF1; #391)
	//     ONE goal-table BLOCK per held quest, in the same order as the ids:
	//       [u32 magic]                           the AREA's goal-table NAME word ("W6QT" 0x57365154, "RRL3" ...)
	//       [u32 tf1][u32 tf2][u32 tf3]           tf2 == count-1, tf3 == 0 across the corpus
	//       [u32 n][ (questId, sequence) × n ]    the AREA's full quest catalog; sequence is 1-based but NOT strictly
	//                                             monotonic (branch/chapter quests repeat a sequence - so the old
	//                                             "idx == i+1" assertion was a beach-only accident)
	//       [u32 trailerQuestId][u32 a][u32 b][u32 c][u32 chainWord]
	//                                             trailerQuestId = the AREA's anchor quest; a/b/c vary per quest
	//                                             (beach 1/0/0); chainWord = the NEXT block's leadAreaId, or the
	//                                             terminal 0x156E7 for the last block (the chain of area ids)
	//
	// ⚠️ There is NO leading 0x3F1 "IndexTag": what looked like one is the EMPTY completed-journal frame
	// (ListFrame.ForCount(0) == 0x3F1) that always precedes the index - see FullCharSnapshotSubClass.
	public static class QuestIndexRegionCodec
	{
		// The word the LAST block's trailer carries, terminating the chain. Constant across every captured index and
		// identical to the terminal of the empty quest portion.
		public const uint IndexTerminalWord = 0x000156E7;

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
			// below 0x1000 (or a lead area id above it) cannot mis-split the list.
			var declared = r.ReadListCount();
			var questIds = new List<uint>(declared);
			for (var i = 0; i < declared; i++)
				questIds.Add(r.ReadU32Be());

			var at = r.Position;
			var count = r.ReadU32Be();
			if (count != questIds.Count)
				throw CodecParseException.Unexpected(nameof(QuestIndexRegionCodec), at, $"count == the frame-derived {questIds.Count}", count.ToString());

			// An EMPTY index terminates on IndexTerminalWord - what a character with no snapshot quests carries.
			if (count == 0)
			{
				r.ExpectU32Be(IndexTerminalWord, "empty-index terminator");
				return new QuestIndexRegion();
			}

			var leadAreaId = r.ReadU32Be();
			var blocks = new List<QuestIndexRegion.Block>((int)count);
			for (uint b = 0; b < count; b++)
			{
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
					Magic = magic,
					TableField1 = f1,
					TableField2 = f2,
					TableField3 = f3,
					AreaQuestIds = areaQuestIds,
					AreaQuestSequences = areaQuestSequences,
					TrailerQuestId = r.ReadU32Be(),
					TrailerA = r.ReadU32Be(),
					TrailerB = r.ReadU32Be(),
					TrailerC = r.ReadU32Be(),
					ChainWord = r.ReadU32Be()
				});
			}

			return new QuestIndexRegion
			{
				QuestIds = questIds,
				LeadAreaId = leadAreaId,
				Blocks = blocks
			};
		}

		internal static void Write(FaolanStream s, QuestIndexRegion v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(ListFrame.ForCount(v.QuestIds.Count));
			foreach (var id in v.QuestIds)
				s.WriteUInt32(id);
			s.WriteUInt32(v.QuestIds.Count);

			if (v.QuestIds.Count == 0)
			{
				s.WriteUInt32(IndexTerminalWord);
				return;
			}

			s.WriteUInt32(v.LeadAreaId);

			foreach (var b in v.Blocks)
			{
				if (b.AreaQuestSequences.Count != b.AreaQuestIds.Count)
				{
					throw new InvalidOperationException(
						$"Quest index block {b.Magic:X8}: {b.AreaQuestIds.Count} catalog ids but {b.AreaQuestSequences.Count} sequences - the pair is written interleaved, so a generator must supply both (QuestIndexRegions.ForQuests does)");
				}

				s.WriteUInt32(b.Magic);
				s.WriteUInt32(b.TableField1);
				s.WriteUInt32(b.TableField2);
				s.WriteUInt32(b.TableField3);
				s.WriteUInt32(b.AreaQuestIds.Count);
				for (var i = 0; i < b.AreaQuestIds.Count; i++)
				{
					s.WriteUInt32(b.AreaQuestIds[i]);
					s.WriteUInt32(b.AreaQuestSequences[i]);
				}

				s.WriteUInt32(b.TrailerQuestId);
				s.WriteUInt32(b.TrailerA);
				s.WriteUInt32(b.TrailerB);
				s.WriteUInt32(b.TrailerC);
				s.WriteUInt32(b.ChainWord);
			}
		}
	}

	// The quest index: the held quest ids plus one chained area-catalog block per held quest.
	public sealed record QuestIndexRegion
	{
		// The first block's quest QuestLineId (the head of the block chain). Only meaningful when the index is non-empty;
		// for a beach char this is 241 == 0xF1, which is why it read as a constant "sentinel" until #391.
		public uint LeadAreaId { get; init; }

		public List<Block> Blocks { get; init; } = [];
		public List<uint> QuestIds { get; init; } = [];

		// One held quest's area-catalog table + chained trailer.
		public sealed record Block
		{
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
			public uint ChainWord { get; init; } // next held quest's QuestLineId, or IndexTerminalWord (last)
			public uint Magic { get; init; } // the area's goal-table name word (W6QT / RRL3 / ...)
			public uint TableField1 { get; init; }
			public uint TableField2 { get; init; }
			public uint TableField3 { get; init; }

			// The block's trailer words after the catalog: [trailerQuestId][a][b][c][chainWord]. The beach block has
			// (a,b,c) == (1,0,0); they vary per quest across the corpus (a up to 8, c 0 or 1) - so they are carried,
			// not assumed constant (#391).
			public uint TrailerA { get; init; } = 1;
			public uint TrailerB { get; init; }
			public uint TrailerC { get; init; }
		}
	}
}
