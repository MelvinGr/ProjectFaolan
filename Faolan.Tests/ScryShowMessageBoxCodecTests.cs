using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Dialogue;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gates for the scry confirmation box - "Do you want to travel to Ymir's Pass?". Goldens are the
	// captured boxes in NecroL80_mage-necromancer_login_gameplay_chat-annotated-4 and their answers.
	//
	// ⚠️ NOT local travel (#514), despite what these gates used to say. The box belongs to the ZONE-LINE DOOR the
	// player selects: at t=809.8 they click door 0x863 on Field of the Dead, the box follows, and answering it drives
	// the cross-map redirect. The wagoneer who dropped them at that border half a minute earlier teleported them
	// straight off the destination pick, with no box at all (docs/subsystems/local-travel.md). Nothing sends these
	// yet - the door path is `world-teleports`, which has no confirmation step.
	//
	// The point worth gating is that `id` is the DESTINATION MAP ID: all three boxes name the map the player is about
	// to be moved to, and each is followed by a redirect and a MapChange to exactly that map.
	public class ScryShowMessageBoxCodecTests
	{
		// t=810.4 - the box for Ymir's Pass (map 1090), which the redirect at t=812.9 and the MapChange at t=814.5
		// then deliver, after the player selected the border door at t=809.8.
		[Fact]
		public void YmirsPassConfirm_IsByteIdentical()
		{
			var captured = Convert.FromHexString(
				"004d3c6c6f63616c697a65642069643d2231303930222063617465676f72793d223532302220666c6167733d2222206b65793d22257a262333363b6e5f7a7735436f6d4e694863716f57346a22203e" +
				"0000006f00000070000000710250b94c0a31c0d3000000000025e738000000000000487589b0");

			var built = ScryShowMessageBoxCodec.Build(1090, "%z&#36;n_zw5ComNiHcqoW4j", 0x0025E738, 0x00000000487589B0);

			Assert.Equal(captured, built);
		}

		// t=536 - Field of the Dead (map 1020). Same shape, different map and key, which is what shows `id` tracks
		// the destination rather than being a message identity.
		[Fact]
		public void FieldOfTheDeadConfirm_IsByteIdentical()
		{
			var captured = Convert.FromHexString(
				"004d3c6c6f63616c697a65642069643d2231303230222063617465676f72793d223532302220666c6167733d2222206b65793d227274455e5148233930446839262339323b2859522b622c4b22203e" +
				"0000006f00000070000000710250b94c0a31c0d30000000000373cd3000000000000354394e0");

			var built = ScryShowMessageBoxCodec.Build(1020, "rtE^QH#90Dh9&#92;(YR+b,K", 0x00373CD3, 0x00000000354394E0);

			Assert.Equal(captured, built);
		}

		// The answer is a pure echo plus the button index, and that is what makes the thread-context id usable as a
		// correlation handle for a pending trip.
		[Theory]
		[InlineData("000000010025e73800000000487589b0", 1u, 0x0025E738u, 0x00000000487589B0ul)]
		[InlineData("0000000100373cd300000000354394e0", 1u, 0x00373CD3u, 0x00000000354394E0ul)]
		[InlineData("00000001002e4097000000004b29eda0", 1u, 0x002E4097u, 0x000000004B29EDA0ul)]
		public void Answer_Parses(string hex, uint expectedAnswer, uint expectedCtx, ulong expectedCookie)
		{
			var parsed = ScryMessageBoxAnswerCodec.Parse(Convert.FromHexString(hex));

			Assert.Equal(expectedAnswer, parsed.Answer);
			Assert.Equal(expectedCtx, parsed.ThreadCtxId);
			Assert.Equal(expectedCookie, parsed.Cookie);
		}

		// The show and the answer must agree on the correlation pair - a round trip through both halves of the codec.
		[Fact]
		public void ShowAndAnswer_ShareTheCorrelationPair()
		{
			const uint ctx = 0x00300001;
			const ulong cookie = 0x0000002A00300001;
			var show = ScryShowMessageBoxCodec.Build(4020, "rtE^QH#90Dh9&#92;(YR+b,K", ctx, cookie);

			// The trailing 12 bytes of the show are exactly what the answer echoes after its button index.
			var answer = new byte[16];
			answer[3] = 1;
			// ctxId, then skip the u16 gap, then the cookie.
			Array.Copy(show, show.Length - 14, answer, 4, 4);
			Array.Copy(show, show.Length - 8, answer, 8, 8);

			var parsed = ScryMessageBoxAnswerCodec.Parse(answer);

			Assert.Equal(ScryMessageBoxAnswerCodec.Confirm, parsed.Answer);
			Assert.Equal(ctx, parsed.ThreadCtxId);
			Assert.Equal(cookie, parsed.Cookie);
		}

		// Strict parse (#530 rule 4): a short body is a decode FAILURE, not a `false` the caller can drop.
		[Fact]
		public void ShortAnswer_IsRejected()
		{
			Assert.Throws<CodecParseException>(() => ScryMessageBoxAnswerCodec.Parse(new byte[15]));
		}

		[Fact]
		public void ContextMustBeSixWords()
		{
			Assert.Throws<ArgumentException>(() => ScryShowMessageBoxCodec.Build(new ScryShowMessageBox("<localized />", [1, 2, 3], 0, 0)));
		}
	}
}
