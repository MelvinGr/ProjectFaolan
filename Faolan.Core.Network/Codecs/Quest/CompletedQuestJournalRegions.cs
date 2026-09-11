using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     Value factory for the completed-quest journal. Not a codec: it maps database rows onto the region
	///     <see cref="CompletedQuestJournalRegionCodec" /> encodes.
	/// </summary>
	public static class CompletedQuestJournalRegions
	{
		/// <summary>
		///     Builds the region from database rows. <paramref name="groups" /> carries the order - both the area sequence
		///     and each area's quest sequence - because the wire order is per-character and not derivable: two characters
		///     carry the same two areas in opposite order. It is not stored either, since the client re-sorts for display
		///     (retail renders alphabetically while the wire does not), so callers pass any deterministic order and the tests
		///     pass a capture's own order to compare byte-for-byte.
		/// </summary>
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
