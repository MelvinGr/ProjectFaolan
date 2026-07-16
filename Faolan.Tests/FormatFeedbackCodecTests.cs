using System;
using System.Text;
using Faolan.Core.Network.Codecs.Text;
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
		// Hoxxling_mage-heraldofxotli_login_gameplay f=7862 - discovery of point 784237, in the same frame as the
		// 2-spot DispatchValidResurrectionSpots re-send (DispatchValidResurrectionSpotsTests pins that side).
		private const string DiscoveredPoint784237Body = "0000012c8d5a8b6b0000c35001316e7101000000000000000000000000010d3c72656d6f7465666f726d61742069643d22323138333237323336222063617465676f72793d2231303022206b65793d224b734154474f4b604825396c40407a5b316d6f2c22206b6e75626f743d22302220203e3c706172616d657465722076616c75653d22373b266c743b6c6f63616c697a65642069643d2671756f743b3738343233372671756f743b2063617465676f72793d2671756f743b3237302671756f743b20666c6167733d2671756f743b2671756f743b206b65793d2671756f743b5674215d3026616d703b616d703b5323336354337626616d703b71756f743b2926616d703b2333393b484a65552671756f743b202667743b22202f3e3c2f72656d6f7465666f726d61743e00000001";

		// ..._guild_invite_accepted-random-gameplay-other_playersvisible f=1109 - the same message for point 784238.
		private const string DiscoveredPoint784238Body = "0000012c8d5a8b6b0000c35001316e7101000000000000000000000000010d3c72656d6f7465666f726d61742069643d22323138333237323336222063617465676f72793d2231303022206b65793d224b734154474f4b604825396c40407a5b316d6f2c22206b6e75626f743d22302220203e3c706172616d657465722076616c75653d22373b266c743b6c6f63616c697a65642069643d2671756f743b3738343233382671756f743b2063617465676f72793d2671756f743b3237302671756f743b20666c6167733d2671756f743b2671756f743b206b65793d2671756f743b533526616d703b71756f743b2926616d703b2333393b563f68566c4626616d703b2333393b5f6f52345d4c752e2671756f743b202667743b22202f3e3c2f72656d6f7465666f726d61743e00000001";

		private const string AreaNameBody = "000001508d5a8b6b0000c3500802e5d40100000000000000000000000001313c72656d6f7465666f726d617420746f6b656e3d" +
		                                    "2273686f776669666f6d65737361676566726f6d7365727665722838297c6d61696e2831297c47656e65726963446973706c61" +
		                                    "79417265614e616d65222063617465676f72793d22353030313822206b65793d22706341644957596853586654464465354931" +
		                                    "673722206b6e75626f743d22302220203e3c706172616d657465722076616c75653d22373b266c743b6c6f63616c697a656420" +
		                                    "63617465676f72793d262333393b3530303235262333393b20746f6b656e3d262333393b446973706c61796564546578742833" +
		                                    "297c47656e65726963446973706c6179417265614e616d657c33343734353836262333393b2667743b22206e616d653d224469" +
		                                    "73706c617965645465787422202f3e3c2f72656d6f7465666f726d61743e00000001";

		// A plain-token feedback line (no parameters): the buff "LostGraceEffect" notice.
		private const string LostGraceBody = "0000008f8d5a8b6b0000c35008006f310100000000000000004200002000703c72656d6f7465666f726d617420746f6b656e3d" +
		                                     "224c6f73744772616365456666656374222063617465676f72793d2231303022206b65793d22524f3553516c5a6e2e682f4476" +
		                                     "766148262333363b57477522206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e00000001";

		[Theory, InlineData(AreaNameBody), InlineData(LostGraceBody)]
		public void Build_FromParsedFields_MatchesCapture(string capturedHex)
		{
			var captured = Convert.FromHexString(capturedHex);
			var p = FormatFeedbackCodec.Parse(captured);

			var rebuilt = FormatFeedbackCodec.Build(p.Instance, p.TypeFlags, p.Footer, p.Xml);

			Assert.Equal(capturedHex, Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void Build_FromScratch_ProducesParsableBanner()
		{
			var xml = Encoding.ASCII.GetBytes("<remoteformat token=\"showfifomessagefromserver(8)|main(1)|GenericDisplayAreaName\" category=\"50018\" key=\"x\" knubot=\"0\"  ></remoteformat>");

			var body = FormatFeedbackCodec.Build(0x12345678, 0, 1, xml);
			var p = FormatFeedbackCodec.Parse(body);

			Assert.Equal(0x12345678u, p.Instance);
			Assert.Equal(xml, p.Xml);
		}

		[Fact]
		public void BuildAreaNameXml_MatchesCapturedBanner()
		{
			// The area-name banner is fully reconstructable from just its localized-text id (#348): rebuild the whole
			// captured AreaNameBody from BuildAreaNameXml(3474586) + the captured instance/typeFlags/footer.
			var captured = Convert.FromHexString(AreaNameBody);
			var p = FormatFeedbackCodec.Parse(captured);

			var xml = FormatFeedbackCodec.BuildAreaNameXml(3474586);
			var rebuilt = FormatFeedbackCodec.Build(p.Instance, p.TypeFlags, p.Footer, xml);

			Assert.Equal(AreaNameBody, Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void BuildLostGraceEffectXml_MatchesCapturedLine()
		{
			// The "lost the grace" line (shown when the Shield of the Risen is lost to an aggressive action) is a fixed
			// token+key string - rebuild the whole captured LostGraceBody from BuildLostGraceEffectXml() + the captured
			// instance/typeFlags/footer. Proves the key is a constant, not a per-send nonce (same bytes in two captures).
			var captured = Convert.FromHexString(LostGraceBody);
			var p = FormatFeedbackCodec.Parse(captured);

			var xml = FormatFeedbackCodec.BuildLostGraceEffectXml();
			var rebuilt = FormatFeedbackCodec.Build(p.Instance, p.TypeFlags, p.Footer, xml);

			Assert.Equal(LostGraceBody, Convert.ToHexStringLower(rebuilt));
			Assert.Equal(FormatFeedbackCodec.EffectLineTypeFlags, p.TypeFlags);
		}

		// #372: the "resurrection point discovered" banner - the toast that actually fires when a player walks near an
		// undiscovered point, sent in the SAME frame as the spots re-send that adds it to their list. Two captured
		// occurrences, different points: the OUTER key is identical in both (fixed per message id) while the inner
		// per-point name key differs, which is what identifies the latter as a property of the point's category-270
		// string rather than of the message. Rebuilt whole from the captured instance/typeFlags/footer.
		[Theory]
		[InlineData(784237u, "Vt!]0&amp;amp;S#3cT3v&amp;quot;)&amp;#39;HJeU", DiscoveredPoint784237Body)]
		[InlineData(784238u, "S5&amp;quot;)&amp;#39;V?hVlF&amp;#39;_oR4]Lu.", DiscoveredPoint784238Body)]
		public void BuildRespawnPointDiscoveredXml_MatchesCapture(uint pointId, string nameKey, string capturedBody)
		{
			var p = FormatFeedbackCodec.Parse(Convert.FromHexString(capturedBody));

			var xml = FormatFeedbackCodec.BuildRespawnPointDiscoveredXml(pointId, nameKey);
			var rebuilt = FormatFeedbackCodec.Build(p.Instance, p.TypeFlags, p.Footer, xml);

			Assert.Equal(capturedBody, Convert.ToHexStringLower(rebuilt));
			Assert.Equal(FormatFeedbackCodec.FifoBannerTypeFlags, p.TypeFlags); // a FIFO banner, like the area name
		}

		[Fact]
		public void Parse_DecodesTheFifoBanner()
		{
			var p = FormatFeedbackCodec.Parse(Convert.FromHexString(AreaNameBody));

			Assert.Equal(0x0802e5d4u, p.Instance); // the player's GameInterface object
			Assert.Equal(FormatFeedbackCodec.FifoBannerTypeFlags, p.TypeFlags);
			var xml = Encoding.ASCII.GetString(p.Xml);
			Assert.Contains("showfifomessagefromserver", xml);
			Assert.Contains("GenericDisplayAreaName", xml);
		}
	}
}
