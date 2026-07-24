using System.Linq;
using System.Xml.Linq;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// Guards RdbComboImporter.CarveComboXml against the #313 regression: an RDB 1010206 combo with no <action>
	// children is written SELF-CLOSING (`<combo ... />`), and the carve used to require a `</combo>` - so 151 of the
	// 744 entries were skipped SILENTLY (the loop just `continue`d). Those 151 include the bow attacks, firebreath,
	// PowerAttack, Bladestorm and the racial dance emotes; their absence from `Combos` is what made #313 conclude the
	// 0x00416541..49 block was "cataloged nowhere" and go looking for a TEA-encrypted RDB type.
	public class RdbComboXmlTests
	{
		// Both bodies are the real RDB text, header and trailing padding included (entries 4285761 and 149732).
		private const string WithActions =
			"<?xml version=\"1.0\"?>\n<combo animation=\"movement_dance_cimmerian_combo_1\" featid=\"13600\" " +
			"gameactionid=\"3773973\" level=\"0\" name=\"Cimmerian_Dance_combo1\" quality=\"1\" state=\"94\">\n" +
			"\t<action eventid=\"202\" name=\"UR\"/>\n\t<action eventid=\"201\" name=\"UL\"/>\n</combo>\n\0\0";

		private const string SelfClosing =
			"<?xml version=\"1.0\"?>\n<combo animation=\"fighting_bow_fire\" gameactionid=\"149725\" level=\"1\" " +
			"name=\"Freefireattack\" quality=\"1\" stamina=\"75.0\"/>\n\0\0";

		[Fact]
		public void NoComboElement_IsRejected()
		{
			Assert.Null(RdbComboImporter.CarveComboXml("<gameaction id=\"1\"/>"));
		}

		[Fact]
		public void SelfClosingCombo_IsCarvedAndParses()
		{
			var xml = RdbComboImporter.CarveComboXml(SelfClosing);
			Assert.NotNull(xml);

			var combo = XElement.Parse(xml!);
			Assert.Equal("Freefireattack", (string?)combo.Attribute("name"));
			Assert.Equal("149725", (string?)combo.Attribute("gameactionid"));
			Assert.Empty(combo.Elements("action"));
		}

		[Fact]
		public void WithActions_IsCarvedThroughTheClosingTag()
		{
			var xml = RdbComboImporter.CarveComboXml(WithActions);
			Assert.NotNull(xml);
			Assert.EndsWith("</combo>", xml);

			var combo = XElement.Parse(xml!);
			Assert.Equal("Cimmerian_Dance_combo1", (string?)combo.Attribute("name"));
			Assert.Equal(["UR", "UL"], combo.Elements("action").Select(a => (string?)a.Attribute("name")));
		}
	}
}
