using System;
using System.Collections.Generic;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the CombatShieldIIR (0xCFFD0519) guard broadcast, anchored on real captured records.
	//
	// A golden is only a golden if its bytes came from a capture. A 16-byte shape such as
	// "cffd05190000c3500000aa7200000000" matches none: it omits the record's length prefix and its env byte, and
	// carries value 0, which not one of the 86 captured records does.
	public class CombatShieldCodecTests
	{
		// A real captured record: [u32 len=17][subop][C350][handle][u8 env=0][u32 value=111]. The leading length is the
		// carrier's and belongs to the sender, so the codec sees the record from the subop on.
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
				Assert.Equal(v, CombatShieldDistribution.Pack(left, mid, right));
				Assert.Equal(3u, left + mid + right);
			});
		}

		[Fact]
		public void Pack_MatchesTheDecimalDigitModel()
		{
			Assert.Equal(210u, CombatShieldDistribution.Pack(2, 1, 0));
			Assert.Equal(CombatShieldCodec.DefaultDistribution, CombatShieldDistribution.Pack(1, 1, 1));
		}

		[Fact]
		public void RoundTrips_ACapturedRecord_ByteExact()
		{
			var parsed = CombatShieldCodec.Parse(Convert.FromHexString(Captured)[4..]);

			Assert.Equal(0x2C56u, parsed.Handle);
			Assert.Equal(0x00, parsed.Env);
			Assert.Equal(CombatShieldCodec.DefaultDistribution, parsed.Value);
			Assert.Equal(Captured[8..], Convert.ToHexStringLower(CombatShieldCodec.Build(parsed.Handle, parsed.Value, parsed.Env)));
		}
	}
}
