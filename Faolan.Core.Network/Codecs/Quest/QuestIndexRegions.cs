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
		// from its seeded index fields, opening with its own QuestLineId (#464 - there is no trailer chain and no
		// terminal word; those were the next block's/next region's own leading words).
		//
		// `areas` maps areaId -> that area's QuestLine row; `areaCatalog` maps areaId -> every quest in the area,
		// ordered by JournalSequence. Both are needed because a block is NOT self-contained (see Block.AreaQuestIds).
		public static QuestIndexRegion ForQuests(IReadOnlyList<QuestDefinition> ordered, QuestDefinition primary, IReadOnlyDictionary<uint, QuestLine>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null)
		{
			ArgumentNullException.ThrowIfNull(ordered);
			ArgumentNullException.ThrowIfNull(primary);

			var blocks = new List<QuestIndexRegion.Block>(ordered.Count);
			for (var i = 0; i < ordered.Count; i++)
			{
				var q = ordered[i];
				var magic = areas != null && areas.TryGetValue(q.QuestLineId, out var a)
					? a.Magic
					: q.IndexMagic;
				var catalog = areaCatalog != null && areaCatalog.TryGetValue(q.QuestLineId, out var c)
					? c
					: null;

				// StepNumber is the held quest's position in its line and StepsAfter the number of steps AFTER it -
				// per-character progress, drawn by the journal as Roman numerals (Block.StepNumber). Captured: quest
				// 10238 seq 1 of 8 -> (1, 7); the same line held at step 3 -> (3, 5). tf3 is 0 in every captured block.
				var areaCount = catalog?.Count ?? 0;
				var sequence = q.JournalSequence;

				var catalogIds = catalog != null
					? catalog.Select(d => d.QuestId).ToList()
					: ParseAreaQuestIds(q.IndexQuestLineQuestIds);

				blocks.Add(new QuestIndexRegion.Block
				{
					AreaId = q.QuestLineId,
					Magic = magic,
					StepNumber = catalog != null
						? sequence
						: q.IndexTableField1,
					StepsAfter = catalog != null
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
					// The two per-LINE trailer words come off the line row when the caller supplied one (#896): TrailerA
					// is the line's category (1 solo / 4 raid / 8 group dungeon) and TrailerC its flag. Without a row
					// the SOLO defaults stand, which is what the beach/2-quest golden path uses and why it stays
					// byte-exact. TrailerB is 0 on every captured block and has no per-line source.
					TrailerA = areas != null && areas.TryGetValue(q.QuestLineId, out var trailerLine)
						? trailerLine.TrailerCategory
						: 1,
					TrailerB = 0,
					TrailerC = areas != null && areas.TryGetValue(q.QuestLineId, out var flagLine)
						? flagLine.TrailerFlag
						: 0
				});
			}

			return new QuestIndexRegion
			{
				QuestIds = [.. ordered.Select(q => q.QuestId)],
				Blocks = blocks
			};
		}

		public static List<uint> ParseAreaQuestIds(string? csv)
		{
			return [.. (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse)];
		}
	}
}
