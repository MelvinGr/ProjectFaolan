using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// "You have mail" and the recipe query - two small c2s/s2c bodies whose widths are pinned by the
	// client's own signature rather than by variance, since the corpus carries four frames of one and one of the
	// other. `--report method-bodies` is the oracle for both.
	public class MailNotificationCodecTests
	{
		// A captured notification, verbatim. The high half is retail's item-id epoch on every frame, which is what
		// says a mail id is a 64-bit id in the item id space rather than a pair of unrelated words.
		private static readonly byte[] CapturedNotification = [0x20, 0xD8, 0x2B, 0x63, 0x3A, 0x16, 0x1B, 0x0B];

		[Fact]
		public void MailNotification_IsOneU64InTheItemIdSpace()
		{
			var parsed = MailNotificationCodec.Parse(CapturedNotification);

			Assert.Equal(0x20D82B633A161B0BUL, parsed.MailId);
			Assert.Equal(0x20D82B63UL, parsed.MailId >> 32); // the epoch half, constant across all four frames
			Assert.Equal(CapturedNotification, MailNotificationCodec.Build(parsed));
			Assert.Equal(CapturedNotification, MailNotificationCodec.Build(0x20D82B633A161B0BUL));
		}

		// The single captured RequestRecipeInfo. 4+4+4+16+16 = 44 is the whole body, which is what pins the widths:
		// the signature's five fields add up to the captured length exactly.
		private static readonly byte[] CapturedRecipeRequest =
		[
			0x00, 0x00, 0x00, 0x3C, // levelId = 60
			0x00, 0x3B, 0x35, 0x58, // templateId1
			0x00, 0x00, 0x00, 0x00, // templateId2 = 0 - a one-ingredient query
			0x26, 0x0B, 0x55, 0x4E, 0xD9, 0xBE, 0xA5, 0x7F, 0x0A, 0xC9, 0x6F, 0x2D, 0x02, 0x2D, 0x09, 0x60,
			0x26, 0x0B, 0x55, 0x4E, 0xD9, 0xBE, 0xA5, 0x7F, 0x0A, 0xC9, 0x6F, 0x2D, 0x02, 0x2D, 0x09, 0x60
		];

		[Fact]
		public void RecipeInfoRequest_CarriesTwoSixteenByteDecryptionKeys()
		{
			var parsed = RecipeInfoRequestCodec.Parse(CapturedRecipeRequest);

			Assert.Equal(60u, parsed.LevelId);
			Assert.Equal(0x003B3558u, parsed.TemplateId1);
			Assert.Equal(0u, parsed.TemplateId2);
			Assert.Equal(RecipeInfoRequestCodec.KeyLength, parsed.Key1.Length);
			Assert.Equal(RecipeInfoRequestCodec.KeyLength, parsed.Key2.Length);

			// In the one captured frame both keys are the same - consistent with templateId2 being absent.
			Assert.Equal(parsed.Key1, parsed.Key2);
			Assert.Equal(CapturedRecipeRequest, RecipeInfoRequestCodec.Build(parsed));
		}
	}
}
