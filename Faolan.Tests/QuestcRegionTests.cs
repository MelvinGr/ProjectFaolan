using System.IO;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// Byte-exact round-trip of the Quest_c (List A) region at the start of the beach snapshot tails - the
	// construct-don't-replay foundation for retiring BeachTailsTemplate.bin's quest journal (Gitea #186).
	// Grammar: docs/re/fullplayer-snapshot-reader.md (recovered from FullPlayerCharacterIIR::ReadSubClass).
	public class QuestcRegionTests
	{
		// Where Quest_c ends in the beach template. 1168, not the old 609: the journal record's own @609 objective
		// region is part of the record (interleaved per quest), so the region does not end until past it (#384).
		// Cross-checked by Captures/scripts/decode_questc_progress.py, which reports the same boundary.
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
			var region = QuestcRegion.Parse(File.ReadAllBytes(TemplatePath));

			var q = Assert.Single(region.Records);
			_o.WriteLine($"questId={q.QuestId} state={q.State} unknown14={q.Unknown14} areaId={q.AreaId}");
			Assert.Equal(10238u, q.QuestId); // "Shipwrecked"
			Assert.Equal(1u, q.State); // active
			Assert.Equal(241u, q.AreaId); // matches the 50300 area string id
			Assert.Equal(4u, q.Field118);
			// +0x14 - the beach template's value, carried through opaquely. NOT a 0x412 "marker" (asserting it as one
			// rejected every level-80 snapshot, #384) and NOT a goal count either: this quest has TWO goals while the
			// word is 0x412, and NecroL80 rec0 holds 2. Reading it as a count crashed the client - see the rename
			// migration QuestSnapshotUnknown14.
			Assert.Equal(QuestcRegion.BeachN, q.Unknown14);
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

			var region = QuestcRegion.Parse(template);
			var built = region.ToArray();

			// The codec consumes + reproduces exactly the version + List A + records prefix of the tails.
			Assert.Equal(RegionEnd, built.Length);
			Assert.Equal(template[..RegionEnd], built);
		}
	}
}
