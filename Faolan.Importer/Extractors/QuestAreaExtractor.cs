using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Harvests QuestArea rows from the COMPLETED-quest journal of captured snapshots
	// (CompletedQuestJournalRegion, Gitea #390) - the only place an area's goal-table name word appears on the wire.
	//
	// The 4CC is a property of the AREA, not of a quest, which is why QuestDefinition.IndexMagic - modelling it
	// per-quest - only ever had two rows filled from the beach/Casilda seeds (#389).
	public static class QuestAreaExtractor
	{
		private static CompletedQuestExtractor.TextRef? Parse(string xml)
		{
			return CompletedQuestExtractor.ParseRecord(xml);
		}


		public static List<QuestArea> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var byArea = new Dictionary<uint, QuestArea>();
			if (packets == null)
				return [];

			foreach (var p in packets)
			{
				if (!SnapshotJournalReader.TryReadCompleted(p.Bytes, out var completed) || completed == null)
					continue;

				foreach (var area in completed.Areas)
				{
					// First writer wins: every capture of the same area carries the same words, and a later partial
					// read should not overwrite a good one.
					if (byArea.ContainsKey(area.AreaId))
						continue;

					var title = Parse(area.AreaTitle);
					var map = Parse(area.MapName);
					var region = Parse(area.RegionName);
					if (title == null || map == null || region == null)
						continue;

					byArea[area.AreaId] = new QuestArea
					{
						AreaId = area.AreaId,
						Magic = area.Magic,
						TableField1 = area.TableField1,
						AreaTitleRefId = title.RefId,
						AreaTitleCategory = title.Category,
						AreaTitleKey = title.Key,
						MapRefId = map.RefId,
						MapCategory = map.Category,
						MapKey = map.Key,
						RegionRefId = region.RefId,
						RegionCategory = region.Category,
						RegionKey = region.Key
					};
				}
			}

			// SECOND source: the quest INDEX blocks carry the same area 4CC (29 of 29 captured blocks match the
			// completed journal's). An area only reachable through a character's ACTIVE quests never appears in a
			// completed journal, so this covers areas the first pass cannot.
			foreach (var p2 in packets)
			{
				if (!SnapshotJournalReader.TryRead(p2.Bytes, out _, out var journal, out var index) || index == null)
					continue;

				foreach (var block in index.Blocks)
				{
					if (block.Magic == 0)
						continue;

					var quest = journal.FirstOrDefault(r => r.QuestId == block.TrailerQuestId);
					if (quest == null || quest.AreaId == 0 || byArea.ContainsKey(quest.AreaId))
						continue;

					// Only the 4CC is available here - the area's header records are not part of the index.
					byArea[quest.AreaId] = new QuestArea { AreaId = quest.AreaId, Magic = block.Magic };
				}
			}

			return [.. byArea.Values];
		}
	}
}
