using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The COMPLETED-quest journal of the 0x737A6DF9 tails - the region immediately after the active Quest_c journal
	// (QuestcRegionCodec) and before the quest index. It feeds the client's "Completed" tab, which renders a
	// Region > Area > quest tree, and it is grouped by AREA to match. Decoded 2026-07-20, Gitea #390.
	//
	// Wire:
	//   [u32 (areas + 1) * 0x3F1]
	//   per AREA group:
	//     [u32 areaId]
	//     [string areaTitle]                         <remoteformat category=50300 id=areaId>
	//     [string mapName]                           <remoteformat category=52000> the PLAYFIELD (4010 "Tortage Island")
	//     [string regionName]                        <remoteformat category=52000> the COARSER grouping (8 distinct)
	//     [u32 magic]                                the area's goal-table NAME word ('RRL3', 'W6QT', ...)
	//     [u32 lineFlag]                             the line's flag - the same word the quest index's block carries
	//                                                as TrailerC (MainQuest_c +0x50); 1 on one captured line (#926)
	//     [u32 questsInArea]
	//     per QUEST:
	//       [u32 sequence]                           1-based, RESETS per area
	//       [u32 questId]
	//       [u32 (records + 1) * 0x3F1]              always 2 records in the corpus
	//       [string title]                           <remoteformat category=50301 id=questId>
	//       [string summary]                         <remoteformat category=50303 id=questId>
	//
	// Strings are the client's default u16-length framing - the familiar `[00][u8 len]` reading is the same bytes
	// below 256, the lead 00 being the length's high byte (#388).
	//
	// ⚠️ The leading frame counts AREA GROUPS, and that count is the only terminator; the region ends well before the
	// access-window table, with other regions in between. Reading the frame as a QUEST count parses a plausible prefix
	// and stops mid-region, right on another area header, and still round-trips byte-exact - because a reader that
	// re-emits what it consumed proves only self-consistency. The frame was pinned by walking to exhaustion instead:
	// the grammar runs exactly 81 area groups where the word says 81, then leaves the region.
	//
	// ⚠️ Why this went unmodelled for so long: the beach template holds ONE active quest and NO completed ones, so the
	// whole region is absent there. Same degenerate-corpus trap as the snapshot prefix and the @609 interleaving.
	public static class CompletedQuestJournalRegionCodec
	{
		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CompletedQuestJournalRegion value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static CompletedQuestJournalRegion Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CompletedQuestJournalRegionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CompletedQuestJournalRegion Read(ref SpanReader r)
		{
			var areaCount = r.ReadListCount();
			var areas = new List<CompletedQuestJournalRegion.AreaGroup>(areaCount);

			for (var a = 0; a < areaCount; a++)
			{
				var area = new CompletedQuestJournalRegion.AreaGroup
				{
					QuestLineId = r.ReadU32Be(),
					QuestLineTitle = r.ReadStringU16Length(),
					MapName = r.ReadStringU16Length(),
					RegionName = r.ReadStringU16Length(),
					Magic = r.ReadU32Be(),
					LineFlag = r.ReadU32Be()
				};

				var questsInArea = r.ReadU32Be();
				for (var q = 0; q < questsInArea; q++)
				{
					var entry = new CompletedQuestJournalRegion.QuestEntry
					{
						Sequence = r.ReadU32Be(),
						QuestId = r.ReadU32Be()
					};

					var records = r.ReadListCount();
					for (var k = 0; k < records; k++)
						entry.Records.Add(r.ReadStringU16Length());

					area.Quests.Add(entry);
				}

				areas.Add(area);
			}

			return new CompletedQuestJournalRegion
			{
				Areas = areas
			};
		}

		internal static void Write(FaolanStream s, CompletedQuestJournalRegion v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(ListFrame.ForCount(v.Areas.Count));
			foreach (var area in v.Areas)
			{
				s.WriteUInt32(area.QuestLineId);
				s.WriteString(area.QuestLineTitle);
				s.WriteString(area.MapName);
				s.WriteString(area.RegionName);
				s.WriteUInt32(area.Magic);
				s.WriteUInt32(area.LineFlag);
				s.WriteUInt32(area.Quests.Count);

				foreach (var quest in area.Quests)
				{
					s.WriteUInt32(quest.Sequence);
					s.WriteUInt32(quest.QuestId);
					s.WriteUInt32(ListFrame.ForCount(quest.Records.Count));
					foreach (var record in quest.Records)
						s.WriteString(record);
				}
			}
		}
	}

	// The completed-quest journal, grouped by area.
	public sealed record CompletedQuestJournalRegion
	{
		public List<AreaGroup> Areas { get; init; } = [];
		public int QuestCount => Areas.Sum(a => a.Quests.Count);

		// One area's completed quests, with the area/region/map header the client's tree groups them under.
		public sealed record AreaGroup
		{
			public uint QuestLineId { get; init; }
			public List<QuestEntry> Quests { get; init; } = [];
			public string QuestLineTitle { get; init; } = "";

			// MAP comes first on the wire, then REGION. The three tiers, fine to coarse:
			//   QuestLineId    917 distinct   the leaf grouping the journal tree renders
			//   MapName    68 distinct   the playfield - 4010 "Tortage Island", 4020 "City of Tortage"
			//   RegionName  8 distinct   the coarser grouping above it (ids 1,2,3,4,5,8,5000,1000000)
			// So a MAP HAS MANY AREAS, and the region above it holds several maps.
			//
			// ⚠️ This pair got labelled backwards once, on the strength of a miscounted probe, and reverting it broke
			// generation while leaving the parse round-trip green - a round-trip cannot see a swap of two fields it
			// both reads and writes. What caught it was rebuilding a captured region from DB rows, where the map/
			// region split is derived independently ("region = smaller id, map = larger" in QuestDefinitionExtractor).
			// ⚠️ Do not sanity-check a region id against the Maps table: 1/2/4/8 exist there as "QA Test Playfield" /
			// "Mariannek_test_playfield" and will CONFIRM the wrong reading.
			public string MapName { get; init; } = "";
			public string RegionName { get; init; } = "";
			public uint Magic { get; init; } // the AREA's goal-table name word ('RRL3', 'W6QT', ...)

			// The line's flag, stored by the client's record reader (`FUN_00922c90`) at +0x6c of the 0x70-byte
			// completed-line record, right after the 4CC at +0x68. It is the per-LINE flag the quest index block carries
			// as TrailerC (`MainQuest_c +0x50`): the two agree on every line captured in both records, including the one
			// positive - so it is one field with two producers, and generation takes it from QuestLine.TrailerFlag.
			// The client reads neither copy anywhere found (#926); see QuestLine.TrailerFlag for what carries it.
			public uint LineFlag { get; init; }
		}

		public sealed record QuestEntry
		{
			public uint QuestId { get; init; }
			public List<string> Records { get; init; } = []; // title (50301), summary (50303)
			public uint Sequence { get; init; } // 1-based within the area
		}
	}
}
