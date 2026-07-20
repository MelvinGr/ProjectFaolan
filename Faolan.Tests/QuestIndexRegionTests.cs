using System.IO;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact round-trip of the quest INDEX region (the [0x3F1] index + the "W6QT" goal-id table) between the
	// @609 objective region and the access-window table. docs/re/fullplayer-snapshot-reader.md, #186.
	public class QuestIndexRegionTests
	{
		private const int RegionStart = 1168, RegionEnd = 1292; // index region / access-window table boundaries
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");

		[Fact]
		public void Parse_DecodesTheIndexAndGoalTable()
		{
			var region = QuestIndexRegion.Parse(File.ReadAllBytes(TemplatePath), RegionStart, RegionEnd);

			Assert.Equal(0x7E2u, region.Marker);
			Assert.Equal(new[] { 10238u }, region.QuestIds);
			Assert.Single(region.Blocks);
			Assert.Equal(8, region.Blocks[0].AreaQuestIds.Count); // the eight goal-step ids (index 1..8)
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
