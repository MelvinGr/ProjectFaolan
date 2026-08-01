using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// VALUE factory for the quest index: turns the held QuestDefinitions (plus their areas' catalogs) into the region
	// QuestIndexRegionCodec encodes. Not a codec - #186/#255/#304/#391.
	public static class QuestIndexRegions
	{
		// The index for the held quests, already ordered (primary first). Each quest contributes a goal-table block
		// from its seeded index fields; the block trailers chain to the next quest's AreaId, terminating on
		// IndexTerminalWord.
		//
		// `areas` maps areaId -> that area's QuestArea row; `areaCatalog` maps areaId -> every quest in the area,
		// ordered by JournalSequence. Both are needed because a block is NOT self-contained (see Block.AreaQuestIds).
		public static QuestIndexRegion ForQuests(IReadOnlyList<QuestDefinition> ordered, QuestDefinition primary, IReadOnlyDictionary<uint, QuestArea>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null)
		{
			ArgumentNullException.ThrowIfNull(ordered);
			ArgumentNullException.ThrowIfNull(primary);

			var blocks = new List<QuestIndexRegion.Block>(ordered.Count);
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

				var catalogIds = catalog != null
					? catalog.Select(d => d.QuestId).ToList()
					: ParseAreaQuestIds(q.IndexAreaQuestIds);

				blocks.Add(new QuestIndexRegion.Block
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
					AreaQuestIds = catalogIds,
					// GENERATION has no captured sequences and emits the 1-based position. That is what the wire
					// carried on every golden (the beach catalog is monotonic); a real catalog is NOT always monotonic,
					// which is why a PARSED block carries the captured sequences instead of regenerating them (#391).
					AreaQuestSequences = [.. Enumerable.Range(1, catalogIds.Count).Select(n => (uint)n)],
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
					// word - a constant 0x156E7 in every capture, the same word the empty quest portion ends on.
					ChainWord = i + 1 < ordered.Count
						? ordered[i + 1].AreaId
						: QuestIndexRegionCodec.IndexTerminalWord
				});
			}

			return new QuestIndexRegion
			{
				QuestIds = [.. ordered.Select(q => q.QuestId)],
				// The head of the block chain is the first (primary) quest's area; for a beach char this is 241 (0xF1).
				LeadAreaId = ordered.Count > 0
					? ordered[0].AreaId
					: 0,
				Blocks = blocks
			};
		}

		public static List<uint> ParseAreaQuestIds(string? csv)
		{
			return [.. (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse)];
		}
	}
}
