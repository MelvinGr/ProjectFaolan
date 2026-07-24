using System.Linq;
using System.Text;
using System.Xml.Linq;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// RdbXml.Carve is the one place every RDB XML reader clips the element out of a body, and it exists because two
	// separate silent-drop bugs came from doing it ad hoc:
	//
	//  - requiring a `</root>` dropped SELF-CLOSING elements - 151 of the 744 combo defs (#313), which is what made
	//    that issue conclude the ids "live in a TEA-encrypted RDB type";
	//  - anchoring on the first '<' latched onto a 0x3C byte inside the BINARY 12-byte RDB-Id header - 1,693 of the
	//    29,854 mesh manifests, a data-dependent failure that only shows up when parsed count is compared to
	//    available count.
	//
	// Both failures returned an empty record through a `catch`, so nothing ever surfaced.
	public class RdbXmlCarveTests
	{
		[Fact]
		public void BinaryHeaderContainingAnAngleBracket_DoesNotAnchorTheCarve()
		{
			// The real shape: RDB-Id header bytes, one of which is '<' (id 12604's second word), then the XML.
			var body = "ni\u0001\u0000<1\u0000\u0000CB\u000f\u0000<MeshIndexFile><Source file=\"W:\\a.max\"/><RenderMesh file=\"224886\"/></MeshIndexFile>\n\0";

			var xml = RdbXml.Carve(body, "MeshIndexFile");
			Assert.NotNull(xml);
			Assert.StartsWith("<MeshIndexFile>", xml);
			Assert.Equal("224886", (string?)XElement.Parse(xml!).Element("RenderMesh")?.Attribute("file"));
		}

		[Fact]
		public void MeshIndex_TextForm_IsReadNotDropped()
		{
			// 1,501 of the manifests are the older line-oriented export, carrying the same ids.
			var body = Encoding.Latin1.GetBytes("ni\u0001$\u0000\u0000CB\u0000\u0000\u0000\u0000Source: W:\\achardb\\hut.max\nRenderMesh: 6532\nRenderMesh: 27817\nCollisionMesh: 6536\nOccluderMesh: 27819\n");

			var mesh = MeshIndexFile.Parse(9234, body);
			Assert.Equal("W:\\achardb\\hut.max", mesh.SourceFile);
			Assert.Equal(6532u, mesh.RenderMeshId); // the FIRST RenderMesh line is the model
			Assert.Equal(6536u, mesh.CollisionMeshId);
			Assert.Empty(mesh.Materials); // the text form carries no material graph - absent, not wrong
		}

		[Fact]
		public void MissingRoot_IsNull_NotAFallbackToTheFirstAngleBracket()
		{
			// A 1010201 entry that is an <animsys> document, not <monsterdata>: null beats handing the parser garbage.
			Assert.Null(RdbXml.Carve("ni\u0001\u0000\u0000\u0000CB<?xml version=\"1.0\" ?><animsys skeleton=\"12692\"></animsys>", "monsterdata"));
		}

		[Fact]
		public void SelfClosingRoot_IsCarvedThroughTheSlashGt()
		{
			var xml = RdbXml.Carve("<?xml version=\"1.0\"?>\n<combo animation=\"fighting_bow_fire\" gameactionid=\"149725\" name=\"Freefireattack\"/>\n\0\0", "combo");

			Assert.NotNull(xml);
			Assert.EndsWith("/>", xml);

			var combo = XElement.Parse(xml!);
			Assert.Equal("Freefireattack", (string?)combo.Attribute("name"));
			Assert.Empty(combo.Elements("action"));
		}

		[Fact]
		public void TrailingPadding_IsExcluded()
		{
			var xml = RdbXml.Carve("<combo name=\"x\"><action name=\"UR\"/></combo>\n\0\0\u00ff>", "combo");

			Assert.Equal("<combo name=\"x\"><action name=\"UR\"/></combo>", xml);
			Assert.Equal(["UR"], XElement.Parse(xml!).Elements("action").Select(a => (string?)a.Attribute("name")));
		}

		[Fact]
		public void WithChildren_IsCarvedThroughTheClosingTag_NotTheFirstChildSelfClose()
		{
			var xml = RdbXml.Carve(
				"<?xml version=\"1.0\"?>\n<combo name=\"Cimmerian_Dance_combo1\" featid=\"13600\">\n\t<action name=\"UR\"/>\n\t<action name=\"UL\"/>\n</combo>\n\0",
				"combo");

			Assert.NotNull(xml);
			Assert.EndsWith("</combo>", xml);
			Assert.Equal(["UR", "UL"], XElement.Parse(xml!).Elements("action").Select(a => (string?)a.Attribute("name")));
		}
	}
}
