using System;
using System.Text;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for GameActionListIIR (0xF508F4C1) vs captured instances (#190). Proves the
	// decoded fixed header is built for a live frame/dynel handle and the (still-opaque) action tail rides verbatim -
	// the construct-don't-replay step for this movement/AI record. Grammar: tools/capture/decode_gameactionlist.py.
	public class GameActionListCodecTests
	{
		// strickenbla / necrol80 capture bodies (subop onward, i.e. BodyHex minus the 4-byte length).
		private const string EmoteBody = // frame 765: dynel 0xD014, action tail = "idle2_emote"
			"f508f4c10000c35000017e0300000007e20000d01400000000000000040000000000000001000000000000000100000009000b69646c65325f656d6f74650000000000";

		private const string NumericBody = // frame 777: dynel 0xCFEC, numeric position/timer action record
			"f508f4c10000c35000017e2c00000007e20000cfec000000000000000400000000000000010000000000000001000000090000000100042652000000000000000000002580ffffffce0000004b00000014000002bc0000000000";

		// The two create-editor racial CHARACTER-TEMPLATE choice records (dynel 0xD00C) constructed in
		// RetailCreateEditorPostReadyBurst, plus a world-entry action record (dynel 0xCF0D). Gates that BuildBody
		// reproduces each captured body byte-exact, so a burst feeding BuildBody the same dynel/tail is wire-identical.
		private const string TemplateChoiceBodyA =
			"f508f4c10000c35000017e0300000007e20000d00c000000000000000400000000000000010000000000000001000000090039bf9300504d616c6554656d706c6174653d35303030303a3635353235323b46656d616c6554656d706c6174653d35303030303a3635353235333b5374796769616e536c617665723d35303030303a3635353235340000000000";

		private const string TemplateChoiceBodyB =
			"f508f4c10000c35000017e0300000007e20000d00c0000000000000004000000000000000100000000000000010000000900019dd100504d616c6554656d706c6174653d35303030303a3635353235323b46656d616c6554656d706c6174653d35303030303a3635353235333b5374796769616e536c617665723d35303030303a3635353235340000000000";

		private const string WorldEntryBody = // frame 0x01316E71, dynel 0xCF0D, empty numeric action tail
			"f508f4c10000c35001316e7100000007e20000cf0d000000000000000400000000000000010000000000000001000000090000000000";

		[Fact]
		public void Parse_DecodesTheCreateEditorTemplateChoiceRecord()
		{
			var p = GameActionListCodec.Parse(Convert.FromHexString(TemplateChoiceBodyA));

			Assert.Equal(0x0000D00Cu, p.DynelHandle);
			Assert.Equal(0u, p.Field3);
			// carries the racial template-id choice list the creation editor reads
			Assert.Contains("MaleTemplate=50000:655252", Encoding.ASCII.GetString(p.ActionTail));
			Assert.Contains("StygianSlaver=50000:655254", Encoding.ASCII.GetString(p.ActionTail));
		}

		[Fact]
		public void Parse_DecodesTheHeaderFields()
		{
			var p = GameActionListCodec.Parse(Convert.FromHexString(EmoteBody));

			Assert.Equal(0x00017E03u, p.FrameHandle);
			Assert.Equal(0x0000D014u, p.DynelHandle);
			Assert.Equal(0u, p.Field3);
			// the action tail carries the emote string verbatim (still-opaque grammar)
			Assert.Contains("idle2_emote", Encoding.ASCII.GetString(p.ActionTail));
		}

		[Theory, InlineData(EmoteBody), InlineData(NumericBody), InlineData(TemplateChoiceBodyA), InlineData(TemplateChoiceBodyB), InlineData(WorldEntryBody)]
		public void Parse_ThenBuild_RoundTripsByteExact(string hex)
		{
			var body = Convert.FromHexString(hex);

			var p = GameActionListCodec.Parse(body);
			var rebuilt = GameActionListCodec.BuildBody(p.FrameHandle, p.DynelHandle, p.Field3, p.ActionTail);

			Assert.Equal(hex, Convert.ToHexStringLower(rebuilt));
		}
	}
}
