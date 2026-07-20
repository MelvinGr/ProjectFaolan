using System.Collections.Generic;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Harvests QUEST DEFINITIONS from the completed-quest journal of captured snapshots (#390). That region carries,
	// per completed quest, its title (category 50301) and journal sub-line (50303) plus the area/map/region header of
	// the group it sits in - which is exactly what the client's Completed tab renders, and what generating the region
	// back needs.
	//
	// The corpus holds 1975 distinct completed quest ids this way, against the 107 definitions the
	// QuestFullUpdate/QuestJournal packets yield - those only cover quests touched during a capture session.
	//
	// ⚠️ These definitions are PARTIAL by nature: the completed journal has no summary, objective, goals or level.
	// They must be merged additively, never through ImportQuestDefinitions, which replaces a quest's text refs
	// wholesale and would strip the richer rows the full-update path built.
	public static class CompletedQuestExtractor
	{
		public sealed record CompletedQuest(uint QuestId, uint AreaId, uint MapId, TextRef Title, TextRef SubLine);

		public sealed record TextRef(uint RefId, uint Category, string Key, string? Flags, bool IsLocalized);

		public sealed record Harvest(List<CompletedQuest> Quests, Dictionary<uint, AreaHeader> Areas);

		public sealed record AreaHeader(uint AreaId, uint MapId, TextRef Title, TextRef Region, TextRef Map);

		public static Harvest Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var quests = new Dictionary<uint, CompletedQuest>();
			var areas = new Dictionary<uint, AreaHeader>();
			if (packets == null)
				return new Harvest([], areas);

			foreach (var p in packets)
			{
				if (!SnapshotJournalReader.TryReadCompleted(p.Bytes, out var completed) || completed == null)
					continue;

				foreach (var area in completed.Areas)
				{
					var areaTitle = Parse(area.AreaTitle);
					var region = Parse(area.RegionName);
					var map = Parse(area.MapName);
					if (areaTitle == null || region == null || map == null)
						continue;

					areas.TryAdd(area.AreaId, new AreaHeader(area.AreaId, map.RefId, areaTitle, region, map));

					foreach (var q in area.Quests)
					{
						if (q.Records.Count != 2 || quests.ContainsKey(q.QuestId))
							continue;

						var title = Parse(q.Records[0]);
						var subLine = Parse(q.Records[1]);
						if (title == null || subLine == null)
							continue;

						quests[q.QuestId] = new CompletedQuest(q.QuestId, area.AreaId, map.RefId, title, subLine);
					}
				}
			}

			return new Harvest([.. quests.Values], areas);
		}

		// A <remoteformat .../> or <localized .../> record as the region carries it.
		private static TextRef? Parse(string xml)
		{
			if (string.IsNullOrEmpty(xml))
				return null;

			var key = Attr(xml, "key");
			if (key == null)
				return null;

			return new TextRef(AttrUInt(xml, "id"), AttrUInt(xml, "category"), key, Attr(xml, "flags"),
				xml.Contains("<localized", System.StringComparison.Ordinal));
		}

		private static string? Attr(string xml, string name)
		{
			var m = Regex.Match(xml, name + "=\"([^\"]*)\"");
			return m.Success
				? m.Groups[1].Value
				: null;
		}

		private static uint AttrUInt(string xml, string name)
		{
			var v = Attr(xml, name);
			return v != null && uint.TryParse(v, out var n)
				? n
				: 0u;
		}
	}
}
