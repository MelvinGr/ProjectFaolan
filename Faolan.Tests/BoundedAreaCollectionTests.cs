using System.Linq;
using System.Text;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// Grammar gates for the client's bounded-area trigger table (RDB type 1000053) -
	// docs/rdb/bounded-areas-1000053.md.
	//
	// Synthetic documents rather than a real record: the shapes below are the ones that actually broke the parser, and
	// each is three lines instead of a 200 KB blob of client data. The geometry-vs-wire evidence lives in
	// MapHazardGeometryTests, which carries a real polygon.
	public class BoundedAreaCollectionTests
	{
		private static bool Parse(string xml, out BoundedAreaCollection? collection)
		{
			return BoundedAreaCollection.TryParse(Encoding.Latin1.GetBytes(xml), out collection);
		}

		// Dialect 1: the element opens with an attribute list. Ymir's Pass and the other newer exports.
		[Fact]
		public void AttributedDialect_Parses()
		{
			Assert.True(Parse(
				"""
				<boundedareas><boundedarea StaticCollisionEnabled="false" IsLocked="true">
					<name>	BA_Lava7	</name>
					<scryid>	0	</scryid>
					<heights>	235.000000 241.699997	</heights>
					<vertice>
						<vertex>	1286.014648 465.011475	</vertex>
						<vertex>	1288.386719 463.399994	</vertex>
						<vertex>	1290.000000 470.000000	</vertex>
					</vertice>
				</boundedarea></boundedareas>
				""", out var c));

			var area = Assert.Single(c!.Areas);
			Assert.Equal("BA_Lava7", area.Name);
			Assert.Equal(3, area.Vertices.Count);
			Assert.Equal(235.0f, area.MinY);
			Assert.Equal(241.699997f, area.MaxY);
			Assert.Equal(0u, area.ScryId);
			Assert.True(area.IsPolygon);
		}

		// Dialect 2: a bare `<boundedarea>` with no attributes and no whitespace between elements. Tortage Volcano and
		// the Tortage Island / Acheronian Ruins records.
		//
		// The scan must not be anchored on the string "<boundedarea " (with the space): that parses dialect 1
		// perfectly and returns zero areas for dialect 2, silently costing 9 playfields - Tortage Volcano's 7 lava
		// volumes among them - while every other number in the import looks healthy.
		[Fact]
		public void BareDialect_Parses()
		{
			Assert.True(Parse(
				"<boundedareas><boundedarea><name>LavaDamage-1</name><scryid>0</scryid>" +
				"<heights>207.471756 343.771759</heights><vertice><vertex>1.5 2.5</vertex>" +
				"<vertex>3.5 4.5</vertex><vertex>5.5 6.5</vertex></vertice></boundedarea></boundedareas>", out var c));

			var area = Assert.Single(c!.Areas);
			Assert.Equal("LavaDamage-1", area.Name);
			Assert.Equal(3, area.Vertices.Count);
			Assert.Equal(1.5f, area.Vertices[0].X);
			Assert.Equal(2.5f, area.Vertices[0].Y);
		}

		// A record holds several sibling root blocks, not one. Scanning from the first `<boundedareas>` to the first
		// `</boundedareas>` returned 7 of Ymir's Pass's 121 areas - and none of its lava, which lives in a later block.
		[Fact]
		public void SiblingRootBlocks_AreAllScanned()
		{
			Assert.True(Parse(
				"<boundedareas><boundedarea><name>First</name><vertice><vertex>0 0</vertex></vertice></boundedarea></boundedareas>" +
				"<boundedareas_collision><boundedarea><name>Second</name><vertice><vertex>1 1</vertex></vertice></boundedarea></boundedareas_collision>" +
				"<boundedareas_fixed3d><boundedarea><name>Third</name><vertice><vertex>2 2</vertex></vertice></boundedarea></boundedareas_fixed3d>",
				out var c));

			Assert.Equal(["First", "Second", "Third"], c!.Areas.Select(a => a.Name));
		}

		// The root tags must never be mistaken for an element - they only differ by continuing the word.
		[Fact]
		public void EmptyRootBlocks_YieldNoAreas()
		{
			Assert.True(Parse("<boundedareas></boundedareas><boundedareas_collision></boundedareas_collision>", out var c));
			Assert.Empty(c!.Areas);
		}

		// A non-zero scryid marks a region the client scripts (the ambient/audio companions), which is what separates a
		// lava damage volume from its music twin.
		[Fact]
		public void ScryId_IsRead()
		{
			Assert.True(Parse(
				"<boundedareas><boundedarea><name>BA_Lava_music</name><scryid>1112698</scryid>" +
				"<heights>245.6 337.3</heights><vertice><vertex>0 0</vertex></vertice></boundedarea></boundedareas>", out var c));

			Assert.Equal(1112698u, Assert.Single(c!.Areas).ScryId);
		}

		// A region with no <heights> reports NaN rather than a made-up range, so the caller can refuse it instead of
		// silently treating it as an unbounded prism.
		[Fact]
		public void MissingHeights_ReportsNaN()
		{
			Assert.True(Parse(
				"<boundedareas><boundedarea><name>NoHeights</name><vertice><vertex>0 0</vertex></vertice></boundedarea></boundedareas>", out var c));

			var area = Assert.Single(c!.Areas);
			Assert.True(float.IsNaN(area.MinY));
			Assert.True(float.IsNaN(area.MaxY));
		}

		// Fewer than 3 vertices encloses nothing; the importer skips these rather than storing a hazard that can never
		// contain a player.
		[Fact]
		public void TwoVertices_IsNotAPolygon()
		{
			Assert.True(Parse(
				"<boundedareas><boundedarea><name>Sliver</name><vertice><vertex>0 0</vertex><vertex>1 1</vertex></vertice></boundedarea></boundedareas>",
				out var c));

			Assert.False(Assert.Single(c!.Areas).IsPolygon);
		}

		// Floats are invariant-culture: a machine with a comma decimal separator must not read "1.5 2.5" as 15 and 25.
		[Fact]
		public void Floats_AreInvariantCulture()
		{
			Assert.True(Parse(
				"<boundedareas><boundedarea><name>F</name><heights>1.25 2.75</heights>" +
				"<vertice><vertex>-3.5 4.25</vertex><vertex>1 2</vertex><vertex>3 4</vertex></vertice></boundedarea></boundedareas>", out var c));

			var area = Assert.Single(c!.Areas);
			Assert.Equal(1.25f, area.MinY);
			Assert.Equal(2.75f, area.MaxY);
			Assert.Equal(-3.5f, area.Vertices[0].X);
			Assert.Equal(4.25f, area.Vertices[0].Y);
		}

		// `<heights>` is written low-then-high in every record seen, but the parser normalises rather than trusting it.
		[Fact]
		public void Heights_AreNormalised()
		{
			Assert.True(Parse(
				"<boundedareas><boundedarea><name>Swapped</name><heights>300 100</heights>" +
				"<vertice><vertex>0 0</vertex></vertice></boundedarea></boundedareas>", out var c));

			var area = Assert.Single(c!.Areas);
			Assert.Equal(100f, area.MinY);
			Assert.Equal(300f, area.MaxY);
		}

		// A payload that is not a bounded-area record at all fails cleanly instead of returning an empty collection that
		// reads as "this playfield has no hazards".
		[Fact]
		public void NonBoundedAreaPayload_Fails()
		{
			Assert.False(Parse("<territories><territory/></territories>", out var c));
			Assert.Null(c);
			Assert.False(BoundedAreaCollection.TryParse([], out _));
		}

		// Even-odd containment over a concave shape - the real volumes are ribbons, so a convex-only test would be
		// wrong. This L needs the notch to read as outside.
		[Fact]
		public void Contains_HandlesAConcaveRing()
		{
			Assert.True(Parse(
				"<boundedareas><boundedarea><name>L</name><heights>0 10</heights><vertice>" +
				"<vertex>0 0</vertex><vertex>10 0</vertex><vertex>10 4</vertex>" +
				"<vertex>4 4</vertex><vertex>4 10</vertex><vertex>0 10</vertex>" +
				"</vertice></boundedarea></boundedareas>", out var c));

			var area = Assert.Single(c!.Areas);
			Assert.True(area.Contains(1f, 1f)); // in the corner
			Assert.True(area.Contains(8f, 2f)); // in the bottom arm
			Assert.True(area.Contains(2f, 8f)); // in the left arm
			Assert.False(area.Contains(8f, 8f)); // the notch - inside the bounding box, outside the shape
			Assert.False(area.Contains(-1f, 5f));
		}
	}
}
