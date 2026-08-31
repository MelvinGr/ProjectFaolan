using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// VALUE factory for the J-panel journal list. Not a codec.
	public static class QuestJournals
	{
		// The journal for a set of definitions. The shared area header (title/map/region) is taken from the FIRST
		// definition.
		//
		// ⚠️ That means every entry is filed under ONE area, which is only correct for a single-area call - it is what
		// rendered ~28 quests from 20 areas as one collapsed group in the Completed tab. The wire can say it properly:
		// QuestJournalIIR.MoreAreas is a list of FURTHER area blocks (empty in every capture we have, but it is the
		// client's own structure, not an invention - see the codec).
		public static QuestJournalIIR ForQuests(uint playerHandle, IReadOnlyList<QuestDefinition> definitions, QuestJournalCurrent? current = null)
		{
			ArgumentNullException.ThrowIfNull(definitions);
			var first = definitions[0];

			var area = new QuestJournalArea(
				first.QuestLineId,
				QuestTextTokens.Record(first, QuestTextSlot.QuestLineTitle),
				QuestTextTokens.Record(first, QuestTextSlot.MapName),
				QuestTextTokens.Record(first, QuestTextSlot.RegionName),
				[.. definitions.Select(d => new QuestJournalEntry(d.QuestId, QuestTextTokens.Record(d, QuestTextSlot.Title), QuestTextTokens.Record(d, QuestTextSlot.ListSubLine)))])
			{
				// The area's ACGHash token, harvested per quest into QuestDefinition.IndexMagic. It is a property of the
				// AREA, so the first definition's is the block's; 0 for an area no capture has shown a journal for.
				AcgHash = first.IndexMagic
			};

			// MergeType 0 = append: what retail's own one-quest journal carries, and the right op for a client that
			// holds nothing yet (we always send the whole journal fresh).
			return new QuestJournalIIR(playerHandle, area, 0, [], current ?? QuestJournalCurrent.None);
		}
	}
}
