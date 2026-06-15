using System;
using System.IO;
using System.Linq;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact round-trip of the @609 per-goal objective-detail region (decode_snapshot_609.py,
	// docs/fullplayer-snapshot-reader.md). Proves QuestObjectiveRegion models the region losslessly - the
	// construct-don't-replay foundation for retiring this part of BeachTailsTemplate.bin (Gitea #186), mirroring
	// QuestcRegion for the @0..609 journal region.
	public class QuestObjectiveRegionTests
	{
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan/GameServer/Packets/FullCharSnapshot/BeachTailsTemplate.bin";
		private const int RegionStart = 609, IndexTag = 1168; // Quest_c end / quest index (empirical)

		private static byte[] Region => File.ReadAllBytes(TemplatePath)[RegionStart..IndexTag];

		[Fact]
		public void Parse_ThenToArray_RoundTripsByteExact()
		{
			var template = File.ReadAllBytes(TemplatePath);

			var region = QuestObjectiveRegion.Parse(template, RegionStart, out var consumed);

			Assert.Equal(IndexTag - RegionStart, consumed); // consumed exactly to the index boundary
			Assert.Equal(Region, region.ToArray());
		}

		[Fact]
		public void Parse_DecodesTheTwoBeachGoals()
		{
			var region = QuestObjectiveRegion.Parse(File.ReadAllBytes(TemplatePath), RegionStart);

			Assert.Equal(2, region.Goals.Count);

			var g1 = region.Goals[0];
			Assert.Equal(10238u, g1.QuestId);
			Assert.Equal(1448u, g1.GoalId);
			Assert.Equal((416u, 701u), (g1.MarkerX, g1.MarkerY)); // matches QuestDefinition's marker
			Assert.Contains("category=\"50304\"", System.Text.Encoding.Latin1.GetString(g1.Objective));
			Assert.All(g1.Reserved, w => Assert.Equal(0u, w));

			var g2 = region.Goals[1];
			Assert.Equal(10238u, g2.QuestId);
			Assert.Equal(3490u, g2.GoalId);
			Assert.Equal((521u, 757u), (g2.MarkerX, g2.MarkerY));
			Assert.Contains("<localized", System.Text.Encoding.Latin1.GetString(g2.TextRecord));
		}
	}
}
