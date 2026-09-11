using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>Value factory for the J-panel journal list. Not a codec.</summary>
	public static class QuestJournals
	{
		/// <summary>
		///     The journal for a set of definitions. The shared area header - title, map and region - is taken from the first
		///     definition.
		/// </summary>
		/// <remarks>
		///     Every entry is therefore filed under one area, which is only correct for a single-area call: quests from
		///     several areas render as one collapsed group. The wire can express it properly through
		///     QuestJournalIIR.MoreAreas, a list of further area blocks; it is the client's own structure, empty in every
		///     capture we hold.
		/// </remarks>
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
				// area, so the first definition's is the block's; 0 for an area no capture has shown a journal for.
				AcgHash = first.IndexMagic,
				// The line's flag - the third producer of QuestLine.TrailerFlag beside the index block and the completed
				// journal, so all three agree on the wire. Line is an Included navigation where it is loaded; lazy
				// loading is off, so null means not loaded and the block carries 0, the value every captured journal
				// area has.
				TrailerFlag = first.Line?.TrailerFlag ?? 0
			};

			// MergeType 0 is append: what retail's own one-quest journal carries, and the right op for a client that
			// holds nothing yet, since we always send the whole journal fresh.
			return new QuestJournalIIR(playerHandle, area, 0, [], current ?? QuestJournalCurrent.None);
		}
	}
}
