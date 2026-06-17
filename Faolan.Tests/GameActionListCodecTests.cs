using System;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for GameActionListIIR (0xF508F4C1) vs captured instances (#190). Proves the
	// decoded fixed header is built for a live frame/dynel handle and the (still-opaque) action tail rides verbatim -
	// the construct-don't-replay step for this movement/AI record. Grammar: Captures/scripts/decode_gameactionlist.py.
	public class GameActionListCodecTests
	{
		// strickenbla / necrol80 capture bodies (subop onward, i.e. BodyHex minus the 4-byte length).
		private const string EmoteBody = // frame 765: dynel 0xD014, action tail = "idle2_emote"
			"f508f4c10000c35000017e0300000007e20000d01400000000000000040000000000000001000000000000000100000009000b69646c65325f656d6f74650000000000";
		private const string NumericBody = // frame 777: dynel 0xCFEC, numeric position/timer action record
			"f508f4c10000c35000017e2c00000007e20000cfec000000000000000400000000000000010000000000000001000000090000000100042652000000000000000000002580ffffffce0000004b00000014000002bc0000000000";

		[Theory]
		[InlineData(EmoteBody)]
		[InlineData(NumericBody)]
		public void Parse_ThenBuild_RoundTripsByteExact(string hex)
		{
			var body = Convert.FromHexString(hex);

			var p = GameActionListCodec.Parse(body);
			var rebuilt = GameActionListCodec.BuildBody(p.FrameHandle, p.DynelHandle, p.Field3, p.ActionTail);

			Assert.Equal(hex, Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void Parse_DecodesTheHeaderFields()
		{
			var p = GameActionListCodec.Parse(Convert.FromHexString(EmoteBody));

			Assert.Equal(0x00017E03u, p.FrameHandle);
			Assert.Equal(0x0000D014u, p.DynelHandle);
			Assert.Equal(0u, p.Field3);
			// the action tail carries the emote string verbatim (still-opaque grammar)
			Assert.Contains("idle2_emote", System.Text.Encoding.ASCII.GetString(p.ActionTail));
		}
	}
}
