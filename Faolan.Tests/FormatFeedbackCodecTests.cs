using System;
using System.Text;
using Faolan.Core.Network.Packets;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the FormatFeedbackIIR (0x8D5A8B6B) body - AoC's on-screen feedback /
	// FIFO-message overlay (NOT chat). Oracles: the GenericDisplayAreaName zone-name banner (necrol80 Has-inventory
	// #1753) and the LostGraceEffect feedback line (necrol80 changemap #1262). Proves the codec reconstructs the
	// captured bytes from the parsed fields (construct-don't-replay) and that Parse is its faithful inverse.
	public class FormatFeedbackCodecTests
	{
		// showfifomessagefromserver(8)|main(1)|GenericDisplayAreaName - the top-of-screen zone-name banner.
		private const string AreaNameBody =
			"000001508d5a8b6b0000c3500802e5d40100000000000000000000000001313c72656d6f7465666f726d617420746f6b656e3d" +
			"2273686f776669666f6d65737361676566726f6d7365727665722838297c6d61696e2831297c47656e65726963446973706c61" +
			"79417265614e616d65222063617465676f72793d22353030313822206b65793d22706341644957596853586654464465354931" +
			"673722206b6e75626f743d22302220203e3c706172616d657465722076616c75653d22373b266c743b6c6f63616c697a656420" +
			"63617465676f72793d262333393b3530303235262333393b20746f6b656e3d262333393b446973706c61796564546578742833" +
			"297c47656e65726963446973706c6179417265614e616d657c33343734353836262333393b2667743b22206e616d653d224469" +
			"73706c617965645465787422202f3e3c2f72656d6f7465666f726d61743e00000001";

		// A plain-token feedback line (no parameters): the buff "LostGraceEffect" notice.
		private const string LostGraceBody =
			"0000008f8d5a8b6b0000c35008006f310100000000000000004200002000703c72656d6f7465666f726d617420746f6b656e3d" +
			"224c6f73744772616365456666656374222063617465676f72793d2231303022206b65793d22524f3553516c5a6e2e682f4476" +
			"766148262333363b57477522206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e00000001";

		[Theory]
		[InlineData(AreaNameBody)]
		[InlineData(LostGraceBody)]
		public void Build_FromParsedFields_MatchesCapture(string capturedHex)
		{
			var captured = Convert.FromHexString(capturedHex);
			var p = FormatFeedbackCodec.Parse(captured);

			var rebuilt = FormatFeedbackCodec.Build(p.Instance, p.TypeFlags, p.Footer, p.Xml);

			Assert.Equal(capturedHex, Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void Parse_DecodesTheFifoBanner()
		{
			var p = FormatFeedbackCodec.Parse(Convert.FromHexString(AreaNameBody));

			Assert.Equal(0x0802e5d4u, p.Instance);          // the player's GameInterface object
			Assert.Equal(FormatFeedbackCodec.FifoBannerTypeFlags, p.TypeFlags);
			var xml = Encoding.ASCII.GetString(p.Xml);
			Assert.Contains("showfifomessagefromserver", xml);
			Assert.Contains("GenericDisplayAreaName", xml);
		}

		[Fact]
		public void Build_FromScratch_ProducesParsableBanner()
		{
			var xml = Encoding.ASCII.GetBytes("<remoteformat token=\"showfifomessagefromserver(8)|main(1)|GenericDisplayAreaName\" category=\"50018\" key=\"x\" knubot=\"0\"  ></remoteformat>");

			var body = FormatFeedbackCodec.Build(instance: 0x12345678, typeFlags: 0, footer: 1, xml);
			var p = FormatFeedbackCodec.Parse(body);

			Assert.Equal(0x12345678u, p.Instance);
			Assert.Equal(xml, p.Xml);
		}
	}
}
