using System;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact reconstruction of the TradePostGUI_MailItemBought remoteformat XML (the body of the AH buy->mail
	// notification) from its substitutable fields, against the NecroL80 buy_from_autionhouse_get_mail capture. Proves
	// the buy->mail body is CONSTRUCTED, not replayed, for the captured inputs; arbitrary-item buys reuse the same
	// builder with a best-effort item-name fragment (the per-item RDB name key is unsynthesizable - see the builder).
	public class TradepostMailBodyTests
	{
		// The captured mail body (#1604) - the XML is embedded between the mailId/endpoints header and the footer;
		// reuse MailMessageCodec.Parse to lift it out (the same oracle MailMessageCodecTests uses).
		private const string CapturedMail = "20d82b631c0c578f08006f3100074d656c6c69656a08006f31000002403c72656d6f7465666f726d617420746f6b656e3d22" +
		                                    "5472616465506f73744755495f4d61696c4974656d426f75676874222063617465676f72793d2236323922206b65793d2223" +
		                                    "785b63604e745d3665512136495d546a5d4c7722206b6e75626f743d22302220203e3c706172616d657465722076616c7565" +
		                                    "3d22373b4d656c6c69656a22202f3e3c706172616d657465722076616c75653d22313b373331303836383733333937333439" +
		                                    "3538313322202f3e3c706172616d657465722076616c75653d22373b266c743b72656d6f7465666f726d61742069643d2671" +
		                                    "756f743b3136333131382671756f743b2063617465676f72793d2671756f743b35303230302671756f743b206b65793d2671" +
		                                    "756f743b29744363416479625d4c26616d703b616d703b4d6e5d36645b3945562671756f743b206b6e75626f743d2671756f" +
		                                    "743b302671756f743b20202667743b266c743b2f72656d6f7465666f726d61742667743b22202f3e3c706172616d65746572" +
		                                    "2076616c75653d22373b506f6e616b696e22202f3e3c706172616d657465722076616c75653d22373b323520266c743b6c6f" +
		                                    "63616c697a656420746f6b656e3d2671756f743b43757272656e637954696e536d616c6c2671756f743b2063617465676f72" +
		                                    "793d2671756f743b3530302671756f743b20666c6167733d2671756f743b2671756f743b206b65793d2671756f743b5b4525" +
		                                    "772a487049684e4c214526616d703b2339323b672d6b7229632671756f743b202667743b22202f3e3c2f72656d6f7465666f" +
		                                    "726d61743e0000000b00000000000000006a25d00f6a4d5d0f00000000";

		[Fact]
		public void BuildItemBought_MatchesCapturedXml()
		{
			var capturedXml = MailMessageCodec.Parse(Convert.FromHexString(CapturedMail)).BodyXml;

			// Recipient = the captured character, because the captured XML this is compared against contains it (#417).
			var built = TradepostMailBodyCodec.Build(new TradepostMailBody("AssassinL23" /* the capture's buyer - must match the golden XML */,
				7310868733973495813ul, // the mail XML's param-1 item id (decimal, distinct from the saleId)
				TradepostMailBodyCodec.CapturedItemNameEscaped, "Ponakin", "25"));

			Assert.Equal(capturedXml, built);
		}

		[Fact]
		public void BuildItemNameEscaped_IsXmlEscaped()
		{
			var escaped = TradepostMailBodies.ItemName(163118);

			Assert.DoesNotContain("<", escaped);
			Assert.DoesNotContain(">", escaped);
			Assert.Contains("&lt;remoteformat id=&quot;163118&quot;", escaped);
		}
	}
}
