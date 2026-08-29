using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact round-trip of the quest INDEX region (the marker + the "W6QT" area-catalog table) between the
	// COMPLETED-quest journal and the access-window table. docs/re/fullplayer-snapshot-reader.md, #186/#391.
	public class QuestIndexRegionTests
	{
		// The index opens at its MARKER, not the 0x3F1 at 1168 - that word is the empty completed-journal frame (the
		// beach char has no completed quests), decoded as a phantom "IndexTag" until #391. The index is 1172..1292.
		private const int RegionStart = 1172, RegionEnd = 1288; // 1288, not 1292: the last four bytes are the access-window table's ListFrame word (#464)
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");

		[Fact]
		public void Parse_DecodesTheIndexAndGoalTable()
		{
			var template = File.ReadAllBytes(TemplatePath);
			var region = QuestIndexRegionCodec.Parse(template.AsSpan(RegionStart, RegionEnd - RegionStart));

			// The region opens on the id-list FRAME - a one-element list, not the 0x7E2 "constant" it reads as.
			Assert.Equal(ListFrame.ForCount(1), template.ReadU32Be(RegionStart));
			Assert.Equal(new[] { 10238u }, region.QuestIds);
			Assert.Equal(241u, region.Blocks[0].AreaId); // the beach area id (== 0xF1, the old "sentinel"/"leadAreaId")
			Assert.Single(region.Blocks);
			Assert.Equal(8, region.Blocks[0].AreaQuestIds.Count); // the eight area-catalog ids (sequence 1..8)
			Assert.Equal(10238u, region.Blocks[0].AreaQuestIds[0]);
		}

		// #896: the two per-LINE MainQuest_c trailer words come off the QuestLine row, and the SAME pair has to reach
		// the client through BOTH producers - the login snapshot's index block and the incremental
		// MainQuestUpdateIIR - or the client's quest tracker sorts one way at login and another after an accept.
		[Theory]
		[InlineData(1u, 0u)] // solo, the majority and the column's default
		[InlineData(4u, 0u)] // raid
		[InlineData(8u, 1u)] // group dungeon, with the per-line flag set
		public void TheLinesTrailerWords_ReachBothProducers(uint category, uint flag)
		{
			var line = new QuestLine { QuestLineId = 900, Magic = 0x57365154, TrailerCategory = category, TrailerFlag = flag };
			var def = new QuestDefinition { QuestId = 4242, QuestLineId = line.QuestLineId, JournalSequence = 1 };
			var areas = new Dictionary<uint, QuestLine> { [line.QuestLineId] = line };

			// Producer 1: the snapshot index block.
			var block = Assert.Single(QuestIndexRegions.ForQuests([def], def, areas).Blocks);
			Assert.Equal(category, block.TrailerA);
			Assert.Equal(flag, block.TrailerC);
			Assert.Equal(0u, block.TrailerB); // 0 on every captured block; no per-line source

			// Producer 2: the incremental grant - and it must agree with the block, which is the point.
			var iir = MainQuestUpdateCodec.Parse(MainQuestUpdateCodec.Build(0x01316E71, line.QuestLineId, def.QuestId, [def.QuestId], "", line));
			Assert.Equal(block.TrailerA, iir.Unknown0);
			Assert.Equal(block.TrailerC, iir.Unknown2);
		}

		// A line the caller cannot supply - no row loaded, or a line no captured index block ever named - keeps the
		// SOLO values. That is what both paths sent before the columns existed, so the beach goldens stay byte-exact.
		[Fact]
		public void WithNoLineRow_BothProducersKeepTheSoloValues()
		{
			var def = new QuestDefinition { QuestId = 4242, QuestLineId = 900, JournalSequence = 1 };

			var block = Assert.Single(QuestIndexRegions.ForQuests([def], def).Blocks);
			Assert.Equal(1u, block.TrailerA);
			Assert.Equal(0u, block.TrailerC);

			var iir = MainQuestUpdateCodec.Parse(MainQuestUpdateCodec.Build(0x01316E71, 900, def.QuestId, [def.QuestId], ""));
			Assert.Equal(1u, iir.Unknown0);
			Assert.Equal(0u, iir.Unknown2);
		}

		[Fact]
		public void Parse_ThenToArray_RoundTripsByteExact()
		{
			var template = File.ReadAllBytes(TemplatePath);

			var region = QuestIndexRegionCodec.Parse(template.AsSpan(RegionStart, RegionEnd - RegionStart));

			Assert.Equal(template[RegionStart..RegionEnd], QuestIndexRegionCodec.Build(region));
		}
	}
}
