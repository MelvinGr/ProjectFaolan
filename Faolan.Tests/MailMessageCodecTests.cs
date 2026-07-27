using System;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the DispatchMailMessage (S->C 0x2002) body against the NecroL80
	// buy_from_autionhouse_get_mail capture: the base mail (#1604, 629 B, no attachment) and the same mail with the
	// bought item appended (#1607, 754 B). Proves the mail builder reconstructs the captured bytes from a Mail row
	// (construct-don't-replay) and that Parse is its faithful inverse. docs/subsystems/tradepost-protocol.md "Buy -> mail".
	public class MailMessageCodecTests
	{
		// The same mail, with the bought item attached (attachCount=1 + [instanceId][entry][trailer]).
		private const string AttachmentBody = "20d82b631c0c578f08006f3100074d656c6c69656a08006f31000002403c72656d6f7465666f726d617420746f6b656e3d22" +
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
		                                      "726d61743e0000000b00000000000000006a25d00f6a4d5d0f0000000120d82b62d02b198b00000065088bb3ac81adec8aec" +
		                                      "201204081a10051a530a140d274a93cb1564da8ff71d166eedda25bf045b3b12140d274a93cb1564da8ff71d166eedda25bf" +
		                                      "045b3b1a140d274a93cb1564da8ff71d166eedda25bf045b3b252e7d02002d2e7d0200380a45000000000000007208006f31" + "00000000";

		// DispatchMailMessage body (bytes after the 0x2002 opcode), system mail to the captured recipient, attachCount=0. The
		// recipient is the captured character - it is IN these bytes (and in the body XML), so the assertions spell it
		// out; a pseudonym belongs anywhere the name is free input, not where it is what the dump says (#417).
		private const string BaseBody = "20d82b631c0c578f08006f3100074d656c6c69656a08006f31000002403c72656d6f7465666f726d617420746f6b656e3d22" +
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
		public void Build_MatchesCapturedAttachmentMail()
		{
			var mail = MailMessageCodec.Parse(Convert.FromHexString(AttachmentBody));

			var body = MailMessageCodec.Build(mail, true);

			Assert.Equal(AttachmentBody, Convert.ToHexStringLower(body));
		}

		[Fact]
		public void Build_MatchesCapturedBaseMail()
		{
			var mail = MailMessageCodec.Parse(Convert.FromHexString(BaseBody));

			var body = MailMessageCodec.Build(mail, true);

			Assert.Equal(BaseBody, Convert.ToHexStringLower(body));
		}

		[Fact]
		public void Build_WithoutAttachments_DropsTheItem()
		{
			// The base mail (poll / GetAllMails) is the attachment mail with the items withheld - i.e. the 754 B
			// mail rebuilt with includeAttachments=false reproduces the 629 B body (same mailId/recipient/footer).
			var mail = MailMessageCodec.Parse(Convert.FromHexString(AttachmentBody));

			var body = MailMessageCodec.Build(mail, false);

			Assert.Equal(BaseBody, Convert.ToHexStringLower(body));
		}

		[Fact]
		public void Parse_DecodesTheSystemMail()
		{
			var mail = MailMessageCodec.Parse(Convert.FromHexString(AttachmentBody));

			Assert.Equal(0x20d82b631c0c578ful, mail.MailId);
			Assert.Equal("AssassinL23", mail.RecipientName); // the capture's recipient - must match the blob
			Assert.Equal(0x08006f31u, mail.RecipientClass);
			Assert.Equal("", mail.SenderName); // system mail - empty sender name
			Assert.Equal(0ul, mail.Money);
			Assert.StartsWith("<remoteformat token=\"TradePostGUI_MailItemBought\"", mail.BodyXml);

			var attachment = Assert.Single(mail.Attachments);
			Assert.Equal(0x20d82b62d02b198bul, attachment.ItemInstanceId);
			Assert.Equal(5u, attachment.Quantity);
		}
	}
}
