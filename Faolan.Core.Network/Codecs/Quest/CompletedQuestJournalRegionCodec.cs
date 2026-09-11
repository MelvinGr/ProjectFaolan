using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     The completed-quest journal region of the snapshot tails - immediately after the active Quest_c journal
	///     (<see cref="QuestcRegionCodec" />) and before the quest index. It feeds the client's Completed tab, which renders
	///     a region, area, quest tree, and it is grouped by area to match.
	/// </summary>
	/// <remarks>
	///     <code>
	///     [ListFrame(areaGroups)]
	///     per area group:
	///       [u32 areaId]
	///       [string areaTitle]     remoteformat category 50300, id areaId
	///       [string mapName]       remoteformat category 52000 - the playfield, e.g. 4010 "Tortage Island"
	///       [string regionName]    remoteformat category 52000 - the coarser grouping, 8 distinct
	///       [u32 magic]            the area's goal-table name word ('RRL3', 'W6QT', ...)
	///       [u32 lineFlag]         the per-line flag the quest index block also carries as TrailerC
	///       [u32 questsInArea]
	///       per quest:
	///         [u32 sequence]       1-based, resetting per area
	///         [u32 questId]
	///         [ListFrame(records)] always 2 records in the corpus
	///         [string title]       remoteformat category 50301, id questId
	///         [string summary]     remoteformat category 50303, id questId
	///     </code>
	///     Strings use the client's default u16-length framing; below 256 bytes those are the same bytes as a
	///     <c>[00][u8 len]</c> reading, the lead 00 being the length's high byte.
	///     <para>
	///         The leading frame counts area groups, and that count is the only terminator - the region ends well before the
	///         access-window table, with other regions in between. Reading the frame as a quest count parses a plausible
	///         prefix, stops mid-region on another area header, and still round-trips byte-exact, because a reader that
	///         re-emits what it consumed proves only self-consistency. The frame is pinned by walking to exhaustion instead:
	///         the grammar runs exactly as many area groups as the word says, then leaves the region.
	///     </para>
	///     <para>
	///         The beach template holds one active quest and no completed ones, so this whole region is absent from it. A
	///         gate that only reads the beach template proves nothing about this region.
	///     </para>
	/// </remarks>
	public static class CompletedQuestJournalRegionCodec
	{
		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The completed-quest journal, grouped by area.</summary>
	public sealed record CompletedQuestJournalRegion
	{
		public List<AreaGroup> Areas { get; init; } = [];
		public int QuestCount => Areas.Sum(a => a.Quests.Count);

		/// <summary>One area's completed quests, with the area, region and map header the client's tree groups them under.</summary>
		public sealed record AreaGroup
		{
			public uint QuestLineId { get; init; }
			public List<QuestEntry> Quests { get; init; } = [];
			public string QuestLineTitle { get; init; } = "";

			/// <summary>
			///     The map comes first on the wire, then the region. The three tiers run fine to coarse: QuestLineId (917
			///     distinct) is the leaf grouping the journal tree renders, MapName (68 distinct) the playfield - 4010
			///     "Tortage Island", 4020 "City of Tortage" - and RegionName (8 distinct, ids 1, 2, 3, 4, 5, 8, 5000,
			///     1000000) the coarser grouping above it. A map holds many areas, and a region holds several maps.
			/// </summary>
			/// <remarks>
			///     A parse round-trip cannot detect these two being swapped, since it both reads and writes them. What does is
			///     rebuilding a captured region from database rows, where the map and region split is derived independently
			///     (region is the smaller id and map the larger, in QuestDefinitionExtractor). Do not sanity-check a region id
			///     against the Maps table either: 1, 2, 4 and 8 exist there as test playfields and will confirm the wrong
			///     reading.
			/// </remarks>
			public string MapName { get; init; } = "";
			public string RegionName { get; init; } = "";
			public uint Magic { get; init; } // the AREA's goal-table name word ('RRL3', 'W6QT', ...)

			/// <summary>
			///     The line's flag, stored by the client's record reader (FUN_00922c90) at +0x6c of the 0x70-byte
			///     completed-line record, right after the 4CC at +0x68.
			/// </summary>
			/// <remarks>
			///     It is the per-line flag the quest index block carries as TrailerC (MainQuest_c +0x50): the two agree on
			///     every line captured in both records, including the one positive, so it is one field with three producers -
			///     the same FUN_00922c90 is also the area reader QuestJournalIIR_c::ReadSubClass calls, making
			///     QuestJournalArea.TrailerFlag this word too. Generation takes it from QuestLine.TrailerFlag. The client
			///     reads none of the copies; see QuestLine.TrailerFlag for the sweep.
			/// </remarks>
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
