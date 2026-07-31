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
		// rendered ~28 quests from 20 areas as one collapsed group in the Completed tab.
		public static QuestJournalIIR ForQuests(uint playerHandle, IReadOnlyList<QuestDefinition> definitions, TrackedQuestPointer? tracked = null)
		{
			ArgumentNullException.ThrowIfNull(definitions);
			var first = definitions[0];

			return new QuestJournalIIR(
				playerHandle,
				first.AreaId,
				QuestTextTokens.Record(first, QuestTextSlot.AreaTitle),
				QuestTextTokens.Record(first, QuestTextSlot.MapName),
				QuestTextTokens.Record(first, QuestTextSlot.RegionName),
				[.. definitions.Select(d => new QuestJournalEntry(d.QuestId, QuestTextTokens.Record(d, QuestTextSlot.Title), QuestTextTokens.Record(d, QuestTextSlot.ListSubLine)))],
				tracked ?? new TrackedQuestPointer());
		}
	}
}
