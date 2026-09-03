using System;
using System.IO;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact round-trip of the Quest_c (List A) region at the start of the beach snapshot tails - the
	// construct-don't-replay foundation for retiring BeachTailsTemplate.bin's quest journal (Gitea #186).
	// Grammar: docs/re/fullplayer-snapshot-reader.md (recovered from FullPlayerCharacterIIR::ReadSubClass).
	public class QuestcRegionTests
	{
		// Where Quest_c ends in the beach template. 1168, not the old 609: the journal record's own @609 objective
		// region is part of the record (interleaved per quest), so the region does not end until past it (#384).
		// Cross-checked by tools/capture/decode_questc_progress.py, which reports the same boundary.
		private const int RegionEnd = 1168;
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");
		private readonly ITestOutputHelper _o;

		public QuestcRegionTests(ITestOutputHelper o)
		{
			_o = o;
		}

		[Fact]
		public void QuestcRegion_Parses_BeachStarter_Fields()
		{
			var region = QuestcRegionCodec.Parse(File.ReadAllBytes(TemplatePath).AsSpan(0, RegionEnd));

			var q = Assert.Single(region.Records);
			_o.WriteLine($"questId={q.QuestId} level={q.Level} flags={q.QuestLineFlags} questLineId={q.QuestLineId}");
			Assert.Equal(10238u, q.QuestId); // "Shipwrecked"
			// The @+0x10 word is the quest LEVEL, and the beach starter is a level-1 quest. This used to assert the
			// same 1 as "state == active", which passed only by coincidence.
			Assert.Equal(1u, q.Level);
			Assert.Equal(241u, q.QuestLineId); // matches the 50300 quest-line title string id
			Assert.Equal(4u, q.BandLevel);
			// +0x14 - the beach template's value, carried through opaquely. NOT a 0x412 "marker" (asserting it as one
			// rejected every level-80 snapshot, #384) and NOT a goal count either: this quest has TWO goals while the
			// word is 0x412, and NecroL80 rec0 holds 2. Reading it as a count crashed the client - see the rename
			// migration QuestQuestLineFlags.
			Assert.Equal(QuestFullUpdates.LiveQuestLineFlags, q.QuestLineFlags);
			Assert.Equal(2, q.Objectives!.Goals.Count);
			// The record owns its objective region.
			Assert.NotNull(q.Objectives);
			Assert.Contains("category=\"50301\"", q.Title);
			Assert.Contains("category=\"50302\"", q.Summary);
			Assert.Contains("category=\"50300\"", q.Area);
			Assert.Contains("category=\"52000\"", q.Region1);
			Assert.Contains("category=\"52000\"", q.Region2);
			// Beach starter: the numeric objective lists are empty (objective text rides in the strings).
			Assert.Empty(q.Obj1);
			Assert.Empty(q.Obj2);
			Assert.Empty(q.Obj3);
			Assert.Empty(q.Obj4);
		}

		[Fact]
		public void QuestcRegion_RoundTrips_BeachTemplate_ByteExact()
		{
			var template = File.ReadAllBytes(TemplatePath);

			var region = QuestcRegionCodec.Parse(template.AsSpan(0, RegionEnd));
			var built = QuestcRegionCodec.Build(region);

			// The codec consumes + reproduces exactly the version + List A + records prefix of the tails.
			Assert.Equal(RegionEnd, built.Length);
			Assert.Equal(template[..RegionEnd], built);
		}
	}
}
