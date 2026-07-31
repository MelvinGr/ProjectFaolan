using System;
using Faolan.Core;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the ComboBookIIR (0xA8BBEB7F). Oracle: the combat-levelup capture, frame 9939 - the
	// first of the four L2 combos (0x0035F6BE) addressed to GameInterface instance 0x01316d8a.
	public class ComboBookCodecTests
	{
		[Fact]
		public void Build_MatchesCapturedLevelUpCombo()
		{
			var body = ComboBookCodec.Build(0x01316d8a, 0x0035F6BE);

			// The captured payload's leading 00000012 is the carrier's u32 length, which #530 rule 2 moved to the sender.
			Assert.Equal("a8bbeb7f0000c35001316d8a00010035f6be", Convert.ToHexStringLower(body));

			// Parse recovers the fields and rebuilds byte-for-byte.
			var rt = ComboBookCodec.Parse(body);
			Assert.Equal(0x01316d8au, rt.Instance);
			Assert.Equal(0x0035F6BEu, rt.ComboId);
			Assert.Equal(body, ComboBookCodec.Build(rt));
		}
	}
}
