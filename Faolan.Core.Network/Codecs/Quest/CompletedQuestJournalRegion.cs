using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The COMPLETED-quest journal of the 0x737A6DF9 tails - the region immediately after the active Quest_c journal
	// (QuestcRegion) and before the access-window table. It feeds the client's "Completed" tab, which renders a
	// Region > Area > quest tree, and it is grouped by AREA to match. Decoded 2026-07-20, Gitea #390.
	//
	// Wire:
	//   [u32 frame]                                  region framing word (see Frame below)
	//   per AREA group:
	//     [u32 areaId]
	//     [string areaTitle]                         <remoteformat category=50300 id=areaId>
	//     [string mapName]                           <remoteformat category=52000> the PLAYFIELD (4010 Tortage Island)
	//     [string regionName]                        <remoteformat category=52000> the CONTINENT (1,2,3,4,8, ...)
	//     [u32 magic]                                the area's goal-table NAME word ('RRL3', 'W6QT', ...)
	//     [u32 tf1]                                  0 in every captured group
	//     [u32 questsInArea]
	//     per QUEST:
	//       [u32 sequence]                           1-based, RESETS per area
	//       [u32 questId]
	//       [u32 (records + 1) * 0x3F1]              always 2 records in the corpus
	//       [string title]                           <remoteformat category=50301 id=questId>
	//       [string summary]                         <remoteformat category=50303 id=questId>
	//
	// Strings are the client's default u16-length framing (FaolanStream.ReadString/WriteString) - the familiar
	// `[00][u8 len]` reading is the same bytes below 256, the lead 00 being the length's high byte (#388).
	//
	// ⚠️ The area `magic` 4CC is a property of the AREA, not of a quest. QuestDefinition.IndexMagic models it
	// per-quest, which is why only the two fully-captured quests ever carried one (#389/#390).
	//
	// ⚠️ Why this went unmodelled for so long: the beach template holds ONE active quest and NO completed ones, so
	// this whole region is absent there. QuestIndexRegion parsed only for 1-2 record snapshots; every real journal
	// (17/19/21/23/30 records) threw and fell silently into the opaque UnknownBeforeAccessWindowTable blob, because
	// TryParseQuestIndex swallows the exception. Same degenerate-corpus trap as the snapshot prefix and the @609
	// objective interleaving.
	public sealed class CompletedQuestJournalRegion
	{
		public List<AreaGroup> Areas { get; init; } = [];

		public int QuestCount
		{
			get { return Areas.Sum(a => a.Quests.Count); }
		}

		// Parses the region starting at `offset`. The leading frame counts AREA GROUPS - it is an ordinary ListFrame
		// word, (areas + 1) * 0x3F1 - and that count is the only terminator; the region ends well before the
		// access-window table, with other regions in between, so `limit` is a safety bound only.
		//
		// ⚠️ Reading the frame as a QUEST count parses a plausible prefix and stops mid-region, right on another area
		// header, and still round-trips byte-exact - because a reader that re-emits what it consumed proves only
		// self-consistency. The frame was pinned by walking to exhaustion instead: the grammar runs exactly 81 area
		// groups where the word says 81, then leaves the region.
		public static CompletedQuestJournalRegion Parse(byte[] data, int offset, int limit, out int afterRegion)
		{
			ArgumentNullException.ThrowIfNull(data);

			var r = new FaolanStream(data, false)
			{
				Position = checked((uint)offset)
			};

			var areaCount = ListFrame.CountOf(r.ReadUInt32());
			var areas = new List<AreaGroup>();

			for (var a = 0; a < areaCount; a++)
			{
				if (r.Position >= limit)
					throw new InvalidOperationException($"Completed journal: ran past {limit} with {a}/{areaCount} areas read");

				var area = new AreaGroup
				{
					AreaId = r.ReadUInt32(),
					AreaTitle = r.ReadString(),
					RegionName = r.ReadString(),
					MapName = r.ReadString(),
					Magic = r.ReadUInt32(),
					TableField1 = r.ReadUInt32()
				};

				var questsInArea = r.ReadUInt32();
				for (var q = 0; q < questsInArea; q++)
				{
					var entry = new QuestEntry
					{
						Sequence = r.ReadUInt32(),
						QuestId = r.ReadUInt32()
					};

					var recordFrame = r.ReadUInt32();
					for (var k = 0; k < ListFrame.CountOf(recordFrame); k++)
						entry.Records.Add(r.ReadString());

					area.Quests.Add(entry);
				}

				areas.Add(area);
			}

			afterRegion = checked((int)r.Position);
			return new CompletedQuestJournalRegion { Areas = areas };
		}

		public void Write(FaolanStream w)
		{
			ArgumentNullException.ThrowIfNull(w);

			w.WriteUInt32(ListFrame.ForCount(Areas.Count));
			foreach (var area in Areas)
			{
				w.WriteUInt32(area.AreaId);
				w.WriteString(area.AreaTitle);
				w.WriteString(area.RegionName);
				w.WriteString(area.MapName);
				w.WriteUInt32(area.Magic);
				w.WriteUInt32(area.TableField1);
				w.WriteUInt32(area.Quests.Count);

				foreach (var quest in area.Quests)
				{
					w.WriteUInt32(quest.Sequence);
					w.WriteUInt32(quest.QuestId);
					w.WriteUInt32(ListFrame.ForCount(quest.Records.Count));
					foreach (var record in quest.Records)
						w.WriteString(record);
				}
			}
		}

		public byte[] ToArray()
		{
			using var w = new FaolanStream();
			Write(w);
			return w.ToArray();
		}

		// One area's completed quests, with the area/region/map header the client's tree groups them under.
		public sealed class AreaGroup
		{
			public uint AreaId { get; init; }
			public string AreaTitle { get; init; } = "";

			// ⚠️ MAP comes FIRST on the wire, then REGION - the larger 52000 id is the playfield (4010 "Tortage
			// Island", 4020 "City of Tortage"), the smaller one the continent. Labelling them in wire order gets it
			// backwards; the corpus settles it: 917 distinct areas resolve to only 8 distinct region ids, each area
			// to exactly one. An AREA is NOT a map - it is the finer, leaf grouping the journal tree renders.
			public string MapName { get; init; } = "";
			public string RegionName { get; init; } = "";
			public uint Magic { get; init; } // the AREA's goal-table name word ('RRL3', 'W6QT', ...)
			public uint TableField1 { get; init; }
			public List<QuestEntry> Quests { get; init; } = [];
		}

		public sealed class QuestEntry
		{
			public uint Sequence { get; init; } // 1-based within the area
			public uint QuestId { get; init; }
			public List<string> Records { get; init; } = []; // title (50301), summary (50303)
		}
	}
}
