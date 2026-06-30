using System.IO;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// Byte-exact round-trip of the Quest_c (List A) region at the start of the beach snapshot tails - the
	// construct-don't-replay foundation for retiring BeachTailsTemplate.bin's quest journal (Gitea #186).
	// Grammar: docs/fullplayer-snapshot-reader.md (recovered from FullPlayerCharacterIIR::ReadSubClass).
	public class QuestcRegionTests
	{
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan.Tests/Assets/BeachTailsTemplate.bin";
		private const int RegionEnd = 609; // where Quest_c ends in the beach template (decode_questc.py)
		private readonly ITestOutputHelper _o;
		public QuestcRegionTests(ITestOutputHelper o) => _o = o;

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

		[Fact]
		public void QuestcRegion_Parses_BeachStarter_Fields()
		{
			var region = QuestcRegion.Parse(File.ReadAllBytes(TemplatePath));

			var q = Assert.Single(region.Records);
			_o.WriteLine($"questId={q.QuestId} state={q.State} N={q.N} areaId={q.AreaId}");
			Assert.Equal(10238u, q.QuestId);     // "Shipwrecked"
			Assert.Equal(1u, q.State);           // active
			Assert.Equal(241u, q.AreaId);        // matches the 50300 area string id
			Assert.Equal(4u, q.N);
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
	}
}
