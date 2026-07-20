using System.IO;
using Faolan.Core.Network.Codecs;
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
		private const int RegionStart = 1172, RegionEnd = 1292;
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");

		[Fact]
		public void Parse_DecodesTheIndexAndGoalTable()
		{
			var region = QuestIndexRegion.Parse(File.ReadAllBytes(TemplatePath), RegionStart, RegionEnd);

			Assert.Equal(0x7E2u, region.Marker);
			Assert.Equal(new[] { 10238u }, region.QuestIds);
			Assert.Equal(241u, region.LeadAreaId); // the beach area id (== 0xF1, the old "sentinel")
			Assert.Single(region.Blocks);
			Assert.Equal(8, region.Blocks[0].AreaQuestIds.Count); // the eight area-catalog ids (sequence 1..8)
			Assert.Equal(10238u, region.Blocks[0].AreaQuestIds[0]);
		}

		[Fact]
		public void Parse_ThenToArray_RoundTripsByteExact()
		{
			var template = File.ReadAllBytes(TemplatePath);

			var region = QuestIndexRegion.Parse(template, RegionStart, RegionEnd);

			Assert.Equal(template[RegionStart..RegionEnd], region.ToArray());
		}
	}
}
