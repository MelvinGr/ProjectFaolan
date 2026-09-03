using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// VALUE factory for the completed-quest journal. Not a codec - it maps DB rows to the region
	// CompletedQuestJournalRegionCodec encodes (#390).
	public static class CompletedQuestJournalRegions
	{
		// Builds the region from DB rows. `groups` is ORDERED - both the area sequence and each area's quest sequence -
		// because the wire order is per-character and not derivable: two characters carry the same two areas in
		// opposite order (#390). It is not stored either, since the client demonstrably re-sorts for display (retail
		// renders alphabetically while the wire does not), so callers pass whatever deterministic order they like and
		// the tests pass a capture's own order to compare byte-for-byte.
		public static CompletedQuestJournalRegion ForAreas(IReadOnlyList<(QuestLine Area, IReadOnlyList<QuestDefinition> Quests)> groups)
		{
			ArgumentNullException.ThrowIfNull(groups);

			var region = new CompletedQuestJournalRegion();
			foreach (var (area, quests) in groups)
			{
				var group = new CompletedQuestJournalRegion.AreaGroup
				{
					QuestLineId = area.QuestLineId,
					QuestLineTitle = QuestTextTokens.RemoteFormat(area.TitleRefId, area.TitleCategory, area.TitleKey),
					MapName = QuestTextTokens.RemoteFormat(area.MapRefId, area.MapCategory, area.MapKey),
					RegionName = QuestTextTokens.RemoteFormat(area.RegionRefId, area.RegionCategory, area.RegionKey),
					Magic = area.Magic,
					LineFlag = area.TrailerFlag
				};

				foreach (var quest in quests)
				{
					group.Quests.Add(new CompletedQuestJournalRegion.QuestEntry
					{
						Sequence = quest.JournalSequence,
						QuestId = quest.QuestId,
						Records = [Text(quest, QuestTextSlot.Title), Text(quest, QuestTextSlot.ListSubLine)]
					});
				}

				region.Areas.Add(group);
			}

			return region;
		}

		private static string Text(QuestDefinition? def, QuestTextSlot slot)
		{
			var r = def?.TextRefs.FirstOrDefault(t => t.Slot == slot);
			if (r == null)
				return QuestTextTokens.RemoteFormat(0, 0, "");

			return r.IsLocalized
				? QuestTextTokens.Localized(r.RefId, r.Category, r.Flags ?? "", r.KeyText)
				: QuestTextTokens.RemoteFormat(r.RefId, r.Category, r.KeyText);
		}
	}
}
