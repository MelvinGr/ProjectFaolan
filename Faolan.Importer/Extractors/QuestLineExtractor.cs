using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Harvests QuestLine rows from the COMPLETED-quest journal of captured snapshots
	// (CompletedQuestJournalRegion, Gitea #390) - the only place an area's goal-table name word appears on the wire.
	//
	// The 4CC is a property of the AREA, not of a quest, which is why QuestDefinition.IndexMagic - modelling it
	// per-quest - only ever had two rows filled from the beach/Casilda seeds (#389).
	public static class QuestLineExtractor
	{
		public static List<QuestLine> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var byArea = new Dictionary<uint, QuestLine>();

			// The per-line trailer pair, and the lines whose blocks disagree about it (#896) - see the second pass.
			var trailerSeen = new Dictionary<uint, (uint Category, uint Flag)>();
			var disagreements = new List<string>();
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
					if (byArea.ContainsKey(area.QuestLineId))
						continue;

					var title = Parse(area.QuestLineTitle);
					var map = Parse(area.MapName);
					var region = Parse(area.RegionName);
					if (title == null || map == null || region == null)
						continue;

					byArea[area.QuestLineId] = new QuestLine
					{
						QuestLineId = area.QuestLineId,
						Magic = area.Magic,
						// 0 = "no index block has named this line yet" - see the second pass. The completed journal
						// does not carry the trailer pair, so this source never knows it.
						TrailerCategory = 0,
						TableField1 = area.TableField1,
						TitleRefId = title.RefId,
						TitleCategory = title.Category,
						TitleKey = LdbKey.Decode(title.Key) ?? new ProtoDecryptionKey(),
						MapRefId = map.RefId,
						MapCategory = map.Category,
						MapKey = LdbKey.Decode(map.Key) ?? new ProtoDecryptionKey(),
						RegionRefId = region.RefId,
						RegionCategory = region.Category,
						RegionKey = LdbKey.Decode(region.Key) ?? new ProtoDecryptionKey()
					};
				}
			}

			// SECOND source: the quest INDEX blocks carry the same area 4CC (29 of 29 captured blocks match the
			// completed journal's). An area only reachable through a character's ACTIVE quests never appears in a
			// completed journal, so this covers areas the first pass cannot.
			//
			// It is also the ONLY source of the two per-line trailer words (#896), and those are filled for every
			// line a block names - including the ones the completed journal already produced above. That is why the
			// trailer assignment sits OUTSIDE the "first writer wins" guard: the guard is about the header records,
			// and a line can have those from one source and its trailers from the other.
			foreach (var p2 in packets)
			{
				if (!SnapshotJournalReader.TryRead(p2.Bytes, out _, out var journal, out var index) || index == null)
					continue;

				foreach (var block in index.Blocks)
				{
					if (block.Magic == 0)
						continue;

					var quest = journal.FirstOrDefault(r => r.QuestId == block.TrailerQuestId);
					if (quest == null || quest.QuestLineId == 0)
						continue;

					// Only the 4CC is available here - the area's header records are not part of the index.
					if (!byArea.TryGetValue(quest.QuestLineId, out var line))
						byArea[quest.QuestLineId] = line = new QuestLine
						{
							QuestLineId = quest.QuestLineId,
							Magic = block.Magic,
							// 0 = "no block named this line yet", which is what ImportQuestLines reads as "leave the
							// row's own value alone". The COLUMN's default is 1 (solo); a harvested row says 0 until a
							// block fills it, and 1/4/8 after.
							TrailerCategory = 0
						};

					// ⚠️ First block wins, and a DISAGREEMENT is recorded rather than resolved: the pair is stable
					// per line on all 97 captured blocks, so two blocks of one line differing would mean the
					// per-line reading is wrong - a thing to surface, not to average away.
					if (trailerSeen.TryGetValue(quest.QuestLineId, out var seen))
					{
						if (seen != (block.TrailerA, block.TrailerC))
							disagreements.Add($"line {quest.QuestLineId}: index blocks disagree - ({seen.Category},{seen.Flag}) vs ({block.TrailerA},{block.TrailerC})");

						continue;
					}

					trailerSeen[quest.QuestLineId] = (block.TrailerA, block.TrailerC);
					line.TrailerCategory = block.TrailerA;
					line.TrailerFlag = block.TrailerC;
				}
			}

			foreach (var d in disagreements)
				System.Console.WriteLine($"  ⚠️ QuestLine trailer: {d}");

			return [.. byArea.Values];
		}

		// THIRD source: the header off the line's own member QUESTS. A captured Quest_c record carries the same three
		// header records the completed journal's area group does - slots 5/6/7 = 50300 line title, 52000 region, 52000
		// map - so a line the second pass named only by its 4CC (an index block carries no header) gets its header
		// from any harvested quest on it. Without this, 58 lines shipped with a 4CC and NO title, and the completed
		// tab drew every quest finished on one of them under a nameless "(1)" group (a level-15 character's 15051 on 2237,
		// a level-24 character's 14617 on 2043, reported in-client 2026-09-03). The quest's slot-5 ref IS the line id (2,039 of
		// 2,039 harvested quests agree with their definition's QuestLineId), so the join needs no guesswork.
		//
		// Fills IN only: a line the journal already headed keeps it, and a line no quest carries a header for is left
		// as it was. Lines a quest references that neither pass produced are added header-only (Magic 0), which
		// ImportQuestLines treats as "fill what is non-zero" - so a 4CC already in the database is never overwritten
		// by a header-only line's 0.
		public static IReadOnlyList<QuestLine> WithHeadersFromQuests(IReadOnlyList<QuestLine> areas, IReadOnlyList<QuestDefinition> quests)
		{
			ArgumentNullException.ThrowIfNull(areas);
			ArgumentNullException.ThrowIfNull(quests);

			var byArea = areas.ToDictionary(a => a.QuestLineId);
			foreach (var quest in quests)
			{
				if (quest.QuestLineId == 0)
					continue;

				var title = quest.TextRefs.FirstOrDefault(r => r.Slot == QuestTextSlot.QuestLineTitle);
				var region = quest.TextRefs.FirstOrDefault(r => r.Slot == QuestTextSlot.RegionName);
				var map = quest.TextRefs.FirstOrDefault(r => r.Slot == QuestTextSlot.MapName);
				if (title == null || region == null || map == null || title.RefId != quest.QuestLineId)
					continue;

				if (!byArea.TryGetValue(quest.QuestLineId, out var line))
					byArea[quest.QuestLineId] = line = new QuestLine { QuestLineId = quest.QuestLineId, TrailerCategory = 0 };
				else if (line.TitleRefId != 0)
					continue; // the journal's own header wins; every capture of an area carries the same words

				line.TitleRefId = title.RefId;
				line.TitleCategory = title.Category;
				line.TitleKey = (ProtoDecryptionKey)title.KeyText.Clone();
				line.RegionRefId = region.RefId;
				line.RegionCategory = region.Category;
				line.RegionKey = (ProtoDecryptionKey)region.KeyText.Clone();
				line.MapRefId = map.RefId;
				line.MapCategory = map.Category;
				line.MapKey = (ProtoDecryptionKey)map.KeyText.Clone();
			}

			return [.. byArea.Values];
		}

		private static CompletedQuestExtractor.TextRef? Parse(string xml)
		{
			return CompletedQuestExtractor.ParseRecord(xml);
		}
	}
}
