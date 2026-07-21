using System;
using System.Collections.Generic;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the CombatShieldIIR (0xCFFD0519) guard broadcast, anchored on real captured records.
	//
	// Replaces CombatShieldBuilderTests, whose golden ("cffd05190000c3500000aa7200000000") matches NO capture: it
	// omitted the record's length prefix AND its env byte, and carried value 0 when not one of the 86 captured
	// records does. The builder it guarded emitted that same malformed 16-byte shape. Both are deleted - the
	// lesson being that a golden is only a golden if the bytes came from a capture.
	public class CombatShieldCodecTests
	{
		// A real captured record: [u32 len=17][subop][C350][handle][u8 env=0][u32 value=111].
		private const string Captured = "00000011cffd05190000c35000002c56000000006f";

		// The decimal-packing semantics came from client RE. The captures corroborate it: every distinct value seen
		// across the 86 captured records decodes to three arc counts summing to exactly 3 - the client's own guard
		// pool. A wrong packing model would not produce that.
		[Fact]
		public void EveryCapturedValue_DecodesToThreeSlices()
		{
			IReadOnlyList<uint> captured = [111, 102, 210, 21, 12, 30, 120, 3];

			Assert.All(captured, v =>
			{
				var (left, mid, right) = (v / 100, v / 10 % 10, v % 10);
				Assert.Equal(v, CombatShieldCodec.Pack(left, mid, right));
				Assert.Equal(3u, left + mid + right);
			});
		}

		[Fact]
		public void Pack_MatchesTheDecimalDigitModel()
		{
			Assert.Equal(210u, CombatShieldCodec.Pack(2, 1, 0));
			Assert.Equal(CombatShieldCodec.DefaultDistribution, CombatShieldCodec.Pack(1, 1, 1));
		}

		[Fact]
		public void RoundTrips_ACapturedRecord_ByteExact()
		{
			var parsed = CombatShieldCodec.Parse(Convert.FromHexString(Captured));

			Assert.Equal(0x2C56u, parsed.Handle);
			Assert.Equal(0x00, parsed.Env);
			Assert.Equal(CombatShieldCodec.DefaultDistribution, parsed.Value);
			Assert.Equal(Captured, Convert.ToHexStringLower(CombatShieldCodec.Build(parsed.Handle, parsed.Value, parsed.Env)));
		}
	}
}
