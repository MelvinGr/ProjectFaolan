using System;
using System.Text;
using Faolan.Core.Network.Packets;
using Xunit;
using Faolan.Core.Network.Codecs;

namespace Faolan.Tests
{
	// Byte-exact construct of the AttributeChanged / Increased on-screen feedback (the visible half of a level-up).
	// Oracle: the strickenbla "create-...-combat-levelup-questcomplete" capture, frame 9939, the Strength(804) +=3
	// message of the level-up burst. Proves the XML template + FormatFeedbackCodec wrapper reconstruct the captured
	// bytes from (instance, attributeId, amount, keys). See Captures/scripts/decode_levelup_feedback.py.
	public class AttributeChangedFeedbackTests
	{
		// frame 9939, the 532-byte AttributeChanged packet (attr 804 += 3) addressed to instance 0x01316d8a.
		private const string Attr804Plus3 = "000002108d5a8b6b0000c35001316d8a0100000000000000004000000601f13c72656d6f7465666f726d617420746f6b656e3d" +
		                                    "224174747269627574654368616e676564222063617465676f72793d2231303022206b65793d2257384b66762f406c32397026" +
		                                    "2333363b6561302e262333363b723f2e22206b6e75626f743d22302220203e3c706172616d657465722076616c75653d22373b" +
		                                    "266c743b72656d6f7465666f726d61742069643d2671756f743b3830342671756f743b2063617465676f72793d2671756f743b" +
		                                    "3631352671756f743b206b65793d2671756f743b3f6d33714a5a4e725453426b79387270326c2c772671756f743b206b6e7562" +
		                                    "6f743d2671756f743b302671756f743b20202667743b266c743b2f72656d6f7465666f726d61742667743b22202f3e3c706172" +
		                                    "616d657465722076616c75653d22373b266c743b72656d6f7465666f726d617420746f6b656e3d2671756f743b496e63726561" +
		                                    "7365642671756f743b2063617465676f72793d2671756f743b3130302671756f743b206b65793d2671756f743b616b664c644e" +
		                                    "26616d703b2333393b6030685e2f6561783047493f4b2671756f743b206b6e75626f743d2671756f743b302671756f743b2020" +
		                                    "2667743b266c743b2f72656d6f7465666f726d61742667743b22202f3e3c706172616d657465722076616c75653d22313b3322" + "202f3e3c2f72656d6f7465666f726d61743e00000000";

		[Fact]
		public void Build_Convenience_IsParsableFeedback()
		{
			var body = AttributeChangedFeedback.Build(0x12345678, 806, 2);
			var p = FormatFeedbackCodec.Parse(body);

			Assert.Equal(0x12345678u, p.Instance);
			Assert.Equal(AttributeChangedFeedback.TypeFlags, p.TypeFlags);
			var xml = Encoding.ASCII.GetString(p.Xml);
			Assert.Contains("AttributeChanged", xml);
			Assert.Contains("id=&quot;806&quot;", xml);
			Assert.Contains("1;2", xml);
		}

		[Fact]
		public void Build_FromFields_MatchesCapturedLevelUpMessage()
		{
			// The captured per-burst key nonces of frame 9939's Strength message (verbatim, wire-escaped).
			var xml = AttributeChangedFeedback.BuildXml(804, 3, "W8Kfv/@l29p&#36;ea0.&#36;r?.", "?m3qJZNrTSBky8rp2l,w", "akfLdN&amp;#39;`0h^/eax0GI?K");

			var body = FormatFeedbackCodec.Build(0x01316d8a, AttributeChangedFeedback.TypeFlags, AttributeChangedFeedback.Footer, Encoding.ASCII.GetBytes(xml));

			Assert.Equal(Attr804Plus3, Convert.ToHexStringLower(body));
		}
	}
}
