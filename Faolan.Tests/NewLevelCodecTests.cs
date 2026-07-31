using System;
using Faolan.Core;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the NewLevelIIR_t (0x6D314743) - the level-up EVENT the client processes to draw the
	// "Congratulations, you have reached level N!" banner + effect (the text is client-side, gated on this packet).
	// Oracle: the Blablabla soldier-guardian "...combat-levelup-questcomplete" capture, frame 9937 - the L1->L2 ding,
	// addressed to GameInterface instance 0x01316d8a, newLevel 2, deltas [168,100,340,1,8,0].
	public class NewLevelCodecTests
	{
		// frame 9937, the 45-byte NewLevelIIR body. Its leading 00000029 is the carrier's length, which #530 rule 2
		// moved off the codec and onto the sender - hence the [8..] slices below.
		private const string L1ToL2Ding = "000000296d3147430000c35001316d8a0000000002000000a80000006400000154000000010000000800000000";

		[Fact]
		public void Build_Convenience_CarriesLevelWithZeroDeltas()
		{
			var body = NewLevelCodec.Build(0x02000003, 20);

			// [sub][marker][instance][env 00][newLevel 20][6x u32 0] - the carrier's u32 length is the sender's (#530 rule 2).
			Assert.Equal(41, body.Length);
			Assert.Equal(20u, NewLevelCodec.Parse(body).NewLevel);
			Assert.All(NewLevelCodec.Parse(body).Deltas, d => Assert.Equal(0u, d));
		}

		[Fact]
		public void Build_MatchesCapturedLevelUpEvent()
		{
			ReadOnlySpan<uint> deltas = [168, 100, 340, 1, 8, 0];
			var body = NewLevelCodec.Build(0x01316d8a, 2, deltas);

			// The captured payload's leading u32 is the carrier's length, which #530 rule 2 moved to the sender.
			Assert.Equal(L1ToL2Ding[8..], Convert.ToHexStringLower(body));

			// Parse recovers level + the 6 deltas and rebuilds byte-for-byte.
			var rt = NewLevelCodec.Parse(body);
			Assert.Equal(2u, rt.NewLevel);
			Assert.Equal(new uint[] { 168, 100, 340, 1, 8, 0 }, rt.Deltas);
			Assert.Equal(body, NewLevelCodec.Build(rt));
		}
	}
}
