using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     Value factory for the quest index: turns the held QuestDefinitions, plus their areas' catalogs, into the region
	///     <see cref="QuestIndexRegionCodec" /> encodes. Not a codec.
	/// </summary>
	public static class QuestIndexRegions
	{
		/// <summary>
		///     The index for the held quests, already ordered with the primary first. Each quest contributes a goal-table
		///     block from its seeded index fields, opening with its own QuestLineId; there is no trailer chain and no
		///     terminal word.
		/// </summary>
		/// <remarks>
		///     <paramref name="areas" /> maps an area id to that area's QuestLine row, and
		///     <paramref name="areaCatalog" /> maps it to every quest in the area, ordered by JournalSequence. Both are
		///     needed because a block is not self-contained; see QuestIndexRegion.Block.AreaQuestIds.
		/// </remarks>
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

				// StepNumber is the held quest's position in its line and StepsAfter the number of steps after it -
				// per-character progress, drawn by the journal as Roman numerals. Captured: quest 10238 at sequence 1 of
				// 8 gives (1, 7), and the same line held at step 3 gives (3, 5). tf3 is 0 in every captured block.
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
					// Generation has no captured sequences and emits the 1-based position, which is what the wire carries
					// on every golden because the beach catalog is monotonic. A real catalog is not always monotonic,
					// which is why a parsed block carries the captured sequences instead of regenerating them.
					AreaQuestSequences = [.. Enumerable.Range(1, catalogIds.Count).Select(n => (uint)n)],
					// The block's trailer id is the area's anchor quest (catalog sequence 1) rather than the held quest;
					// the two coincide only when the held quest is the area's first. On the beach, held 12371 (sequence
					// 5) still carries trailer 10238 (sequence 1). It falls back to the held quest when no catalog is
					// supplied - the beach and two-quest golden path, where they are the same quest anyway.
					TrailerQuestId = catalog is { Count: > 0 }
						? catalog[0].QuestId
						: q.QuestId,
					// The two per-line trailer words come off the line row when the caller supplied one: TrailerA is the
					// line's category (1 solo, 4 raid, 8 group dungeon) and TrailerC its flag. Without a row the solo
					// defaults stand, which is what the beach and two-quest golden path uses. TrailerB is 0 on every
					// captured block and has no per-line source.
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
