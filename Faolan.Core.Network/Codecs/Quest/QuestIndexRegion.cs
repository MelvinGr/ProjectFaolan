using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The QUEST INDEX region of the 0x737A6DF9 tails - the bytes between the COMPLETED-quest journal
	// (CompletedQuestJournalRegion) and the access-window table. Decoded byte-exact from the beach template, the
	// 2-quest capture (#255) AND every progressed journal in the corpus (5..30 active quests, #391):
	//   [u32 marker=(count+1)*0x3F1]              the id-list framing (count = number of held/active quests)
	//   [u32 questId × count]                     the held quest ids
	//   [u32 count]
	//   if count == 0:  [u32 terminal 0x000156E7]   (empty index - a char with no snapshot quests)
	//   else:
	//     [u32 leadAreaId]                        the FIRST block's quest AreaId (was mis-modelled as a 0x000000F1
	//                                             "sentinel" - constant only because every golden is a beach char,
	//                                             AreaId 241 == 0xF1; #391/[[list_frame_0x3f1_count_word]])
	//     ONE goal-table BLOCK per held quest, in the same order as the ids:
	//       [u32 magic]                           the AREA's goal-table NAME word ("W6QT" 0x57365154, "RRL3" ...)
	//       [u32 tf1][u32 tf2][u32 tf3]           tf2 == count-1, tf3 == 0 across the corpus
	//       [u32 n][ (questId, sequence) × n ]    the AREA's full quest catalog; sequence is 1-based but NOT strictly
	//                                             monotonic (branch/chapter quests repeat a sequence - so the old
	//                                             "idx == i+1" assertion was a beach-only accident)
	//       [u32 trailerQuestId][u32 a][u32 b][u32 c][u32 chainWord]
	//                                             trailerQuestId = this block's held quest; a/b/c vary per quest
	//                                             (beach 1/0/0); chainWord = the NEXT block's leadAreaId, or the
	//                                             terminal 0x156E7 for the last block (the chain of area ids)
	//
	// ⚠️ There is NO leading 0x3F1 "IndexTag": what looked like one is the EMPTY completed-journal frame
	// (ListFrame.ForCount(0) == 0x3F1) that always precedes the index - see FullCharSnapshotSubClass.Deserialize.
	// Typed codec (Parse/ToArray round-trips byte-exact) so FullCharSnapshotSubClass models it instead of the opaque
	// blob (#186/#304/#391). Build generates it from the held QuestDefinitions.
	public sealed class QuestIndexRegion : ICodec<QuestIndexRegion>
	{
		// Retained for callers that locate the empty completed-journal frame by this value (it IS ListFrame.Unit).
		public const uint IndexTag = ListFrame.Unit;

		// The word the LAST block's trailer carries, terminating the chain. Constant across every captured index and
		// identical to the terminal of the empty quest portion.
		public const uint IndexTerminalWord = 0x000156E7;

		// The first block's quest AreaId (the head of the block chain). Only present when count > 0; for a beach char
		// this is 241 == 0xF1, which is why it read as a constant "sentinel" until the corpus-wide decode (#391).
		public uint LeadAreaId { get; init; }

		// The serialized byte length, measured via Write without materializing the array (reuses the one Write path,
		// so it can't drift from ToArray).
		public int ByteLength
		{
			get
			{
				using var w = new FaolanStream();
				Write(w);
				return (int)w.Length;
			}
		}

		public List<Block> Blocks { get; init; } = [];
		public List<uint> QuestIds { get; init; } = [];

		// The id-list count framing, (QuestIds.Count + 1) * 0x3F1 - Build derives it. The default is the 1-quest
		// word; it is NOT a constant (0x7E2 just IS ListFrame.ForCount(1), which is what makes it look like one).
		public uint Marker { get; init; } = ListFrame.ForCount(1);

		public static QuestIndexRegion Read(FaolanStream r)
		{
			var marker = r.ReadUInt32();
			if (marker == 0 || marker % ListFrame.Unit != 0)
				throw new InvalidOperationException($"Quest index: marker 0x{marker:X} is not a list-frame word");

			// The count is the marker's frame count - read exactly that many ids rather than peeking, so a held quest
			// id below 0x1000 (or a lead area id above it) cannot mis-split the list ([[tolerant_readers_hide_wire_bugs]]).
			var declared = ListFrame.CountOf(marker);
			var questIds = new List<uint>(declared);
			for (uint i = 0; i < declared; i++)
				questIds.Add(r.ReadUInt32());

			var count = r.ReadUInt32();
			if (count != questIds.Count)
				throw new InvalidOperationException($"Quest index: count {count} != marker-derived {questIds.Count}");

			// An EMPTY index terminates on IndexTerminalWord - the shape BuildEmptyQuestPortion writes, and what a
			// character with no snapshot quests carries.
			if (count == 0)
			{
				var terminal = r.ReadUInt32();
				if (terminal != IndexTerminalWord)
					throw new InvalidOperationException($"Empty quest index: terminator 0x{terminal:X} != 0x{IndexTerminalWord:X}");

				return new QuestIndexRegion
				{
					Marker = marker
				};
			}

			var leadAreaId = r.ReadUInt32();

			var blocks = new List<Block>((int)count);
			for (uint b = 0; b < count; b++)
			{
				var magic = r.ReadUInt32();
				var f1 = r.ReadUInt32();
				var f2 = r.ReadUInt32();
				var f3 = r.ReadUInt32();
				var n = r.ReadUInt32();
				var areaQuestIds = new List<uint>((int)n);
				var areaQuestSequences = new List<uint>((int)n);
				for (uint i = 0; i < n; i++)
				{
					areaQuestIds.Add(r.ReadUInt32());
					areaQuestSequences.Add(r.ReadUInt32());
				}

				var trailerQuestId = r.ReadUInt32();
				var a = r.ReadUInt32();
				var bb = r.ReadUInt32();
				var c = r.ReadUInt32();
				var chainWord = r.ReadUInt32();

				blocks.Add(new Block
				{
					Magic = magic,
					TableField1 = f1,
					TableField2 = f2,
					TableField3 = f3,
					AreaQuestIds = areaQuestIds,
					AreaQuestSequences = areaQuestSequences,
					TrailerQuestId = trailerQuestId,
					TrailerA = a,
					TrailerB = bb,
					TrailerC = c,
					ChainWord = chainWord
				});
			}

			return new QuestIndexRegion
			{
				Marker = marker,
				QuestIds = questIds,
				LeadAreaId = leadAreaId,
				Blocks = blocks
			};
		}

		public void Write(FaolanStream w)
		{
			w.WriteUInt32(Marker);
			foreach (var id in QuestIds)
				w.WriteUInt32(id);
			w.WriteUInt32(QuestIds.Count);

			if (QuestIds.Count == 0)
			{
				w.WriteUInt32(IndexTerminalWord);
				return;
			}

			w.WriteUInt32(LeadAreaId);

			foreach (var b in Blocks)
			{
				w.WriteUInt32(b.Magic);
				w.WriteUInt32(b.TableField1);
				w.WriteUInt32(b.TableField2);
				w.WriteUInt32(b.TableField3);
				w.WriteUInt32(b.AreaQuestIds.Count);
				for (var i = 0; i < b.AreaQuestIds.Count; i++)
				{
					w.WriteUInt32(b.AreaQuestIds[i]);
					// Round-trip carries the captured sequence; generation (Build) has none and falls back to i+1
					// (the beach catalog is monotonic, so byte-identical there).
					w.WriteUInt32(i < b.AreaQuestSequences.Count
						? b.AreaQuestSequences[i]
						: (uint)(i + 1));
				}

				w.WriteUInt32(b.TrailerQuestId);
				w.WriteUInt32(b.TrailerA);
				w.WriteUInt32(b.TrailerB);
				w.WriteUInt32(b.TrailerC);
				w.WriteUInt32(b.ChainWord);
			}
		}

		// Generates the index region for the held quests (already ordered - primary first). Each quest contributes a
		// goal-id block from its seeded index fields; the block trailers chain to the next quest's AreaId, terminating
		// on the primary's IndexTrailerWord. The marker/count/sentinel follow from the quest count. #186/#255/#304.
		// `areas` maps areaId -> that area's QuestArea row; `areaCatalog` maps areaId -> every quest in the area,
		// ordered by JournalSequence. Both are needed because a block is NOT self-contained (see Block.AreaQuestIds).
		public static QuestIndexRegion Build(IReadOnlyList<QuestDefinition> ordered, QuestDefinition primary, IReadOnlyDictionary<uint, QuestArea>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null)
		{
			ArgumentNullException.ThrowIfNull(ordered);
			ArgumentNullException.ThrowIfNull(primary);

			var blocks = new List<Block>(ordered.Count);
			for (var i = 0; i < ordered.Count; i++)
			{
				var q = ordered[i];
				var magic = areas != null && areas.TryGetValue(q.AreaId, out var a)
					? a.Magic
					: q.IndexMagic;
				var catalog = areaCatalog != null && areaCatalog.TryGetValue(q.AreaId, out var c)
					? c
					: null;

				// tf1 is the quest's position in its area and tf2 the number of area quests AFTER it, so the pair is
				// (sequence, remaining). Captured: quest 10238 seq 1 of 8 -> (1, 7); quest 13479 seq 1 of 4 -> (1, 3).
				// tf3 is 0 in every captured block.
				var areaCount = catalog?.Count ?? 0;
				var sequence = q.JournalSequence;

				blocks.Add(new Block
				{
					Magic = magic,
					TableField1 = catalog != null
						? sequence
						: q.IndexTableField1,
					TableField2 = catalog != null
						? areaCount > sequence
							? (uint)areaCount - sequence
							: 0
						: q.IndexTableField2,
					TableField3 = catalog != null
						? 0
						: q.IndexTableField3,
					AreaQuestIds = catalog != null
						? [.. catalog.Select(d => d.QuestId)]
						: ParseGoalSteps(q.IndexGoalSteps),
					// Generation has no captured sequences; Write falls back to i+1 (the beach catalog is monotonic).
					AreaQuestSequences = [],
					// The block's trailer id is the AREA's anchor quest (catalog sequence 1), NOT the held quest - they
					// coincide only when the held quest is the area's first (#391). Beach: held 12371 (seq 5) but the
					// block trailer is 10238 (seq 1). Falls back to the held quest when no catalog is supplied (the
					// beach/2-quest golden path, where they are the same quest anyway).
					TrailerQuestId = catalog is { Count: > 0 }
						? catalog[0].QuestId
						: q.QuestId,
					// The beach block's trailer is (1,0,0); the fields vary per quest on the wire but generation is
					// beach-only today (IsSnapshotEligible is gated off), so the beach values keep it byte-exact.
					TrailerA = 1,
					TrailerB = 0,
					TrailerC = 0,
					// The trailers CHAIN: each carries the NEXT held quest's AreaId, and the LAST carries the terminal
					// word - a constant 0x156E7 in every capture, the same word BuildEmptyQuestPortion ends on.
					ChainWord = i + 1 < ordered.Count
						? ordered[i + 1].AreaId
						: IndexTerminalWord
				});
			}

			return new QuestIndexRegion
			{
				Marker = ListFrame.ForCount(ordered.Count),
				QuestIds = ordered.Select(q => q.QuestId).ToList(),
				// The head of the block chain is the first (primary) quest's area; for a beach char this is 241 (0xF1).
				LeadAreaId = ordered.Count > 0
					? ordered[0].AreaId
					: 0,
				Blocks = blocks
			};
		}

		// Offset-bounded wrapper for parsing the index as an embedded sub-region: reads it at `offset` and asserts it
		// lands exactly at `endOffset` (the access-window table start).
		public static QuestIndexRegion Parse(byte[] data, int offset, int endOffset)
		{
			ArgumentNullException.ThrowIfNull(data);
			using var r = new FaolanStream(data, false)
			{
				Position = checked((uint)offset)
			};
			var region = Read(r);
			if (r.Position != endOffset)
				throw new InvalidOperationException($"Quest index: parsed to {r.Position}, expected {endOffset}");

			return region;
		}

		public static List<uint> ParseGoalSteps(string csv)
		{
			return (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList();
		}

		// One held quest's area-catalog table + chained trailer.
		public sealed class Block
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
			// it must be carried, not regenerated as i+1 (#391).
			public List<uint> AreaQuestIds { get; init; } = [];
			public List<uint> AreaQuestSequences { get; init; } = [];
			public uint ChainWord { get; init; } // next held quest's AreaId, or the primary's IndexTrailerWord (last)
			public uint Magic { get; init; } // the quest's goal-table name word (W6QT / Casilda / ...)
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
