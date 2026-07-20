using System.Collections.Generic;
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

			return [.. byArea.Values];
		}
	}
}
