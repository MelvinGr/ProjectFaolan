using System;
using Faolan.Core;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Combat;
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

			// ICodec round-trip: Read recovers the fields and rebuilds byte-for-byte.
			var rt = ComboBookIirCodec.Message.Read(new FaolanStream(body));
			Assert.Equal(0x01316d8au, rt.Instance);
			Assert.Equal(0x0035F6BEu, rt.ComboId);
			Assert.Equal(body, rt.ToArray());
		}
	}
}
