using System;
using Faolan.Core.Network.Packets;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the ComboBookIIR (0xA8BBEB7F). Oracle: the combat-levelup capture, frame 9939 - the
	// first of the four L2 combos (0x0035F6BE) addressed to GameInterface instance 0x01316d8a.
	public class ComboBookIirCodecTests
	{
		[Fact]
		public void Build_MatchesCapturedLevelUpCombo()
		{
			var body = ComboBookIirCodec.Build(0x01316d8a, 0x0035F6BE);

			Assert.Equal("00000012a8bbeb7f0000c35001316d8a00010035f6be", Convert.ToHexStringLower(body));
		}
	}
}
