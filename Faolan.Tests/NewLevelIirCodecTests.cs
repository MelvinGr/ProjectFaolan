using System;
using Faolan.Core;
using Xunit;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.Tests
{
	// Byte-exact construct of the NewLevelIIR_t (0x6D314743) - the level-up EVENT the client processes to draw the
	// "Congratulations, you have reached level N!" banner + effect (the text is client-side, gated on this packet).
	// Oracle: the Blablabla soldier-guardian "...combat-levelup-questcomplete" capture, frame 9937 - the L1->L2 ding,
	// addressed to GameInterface instance 0x01316d8a, newLevel 2, deltas [168,100,340,1,8,0].
	public class NewLevelIirCodecTests
	{
		// frame 9937, the 45-byte NewLevelIIR_t body.
		private const string L1ToL2Ding = "000000296d3147430000c35001316d8a0000000002000000a80000006400000154000000010000000800000000";

		[Fact]
		public void Build_Convenience_CarriesLevelWithZeroDeltas()
		{
			var body = NewLevelIirCodec.Build(0x02000003, 20);

			// [u32 len][sub][marker][instance][env 00][newLevel 20][6x u32 0]
			Assert.Equal("0000002" + "9", Convert.ToHexStringLower(body)[..8]); // len = 41 (0x29)
			Assert.Equal(45, body.Length);
			// newLevel field (offset 4 len + 4 sub + 4 marker + 4 instance + 1 env = 17)
			var newLevel = (uint)((body[17] << 24) | (body[18] << 16) | (body[19] << 8) | body[20]);
			Assert.Equal(20u, newLevel);
		}

		[Fact]
		public void Build_MatchesCapturedLevelUpEvent()
		{
			ReadOnlySpan<uint> deltas = [168, 100, 340, 1, 8, 0];
			var body = NewLevelIirCodec.Build(0x01316d8a, 2, deltas);

			Assert.Equal(L1ToL2Ding, Convert.ToHexStringLower(body));

			// ICodec round-trip: Read recovers level + the 6 deltas and rebuilds byte-for-byte.
			var rt = NewLevelIirCodec.Message.Read(new FaolanStream(body));
			Assert.Equal(2u, rt.NewLevel);
			Assert.Equal(new uint[] { 168, 100, 340, 1, 8, 0 }, rt.Deltas);
			Assert.Equal(body, rt.ToArray());
		}
	}
}
