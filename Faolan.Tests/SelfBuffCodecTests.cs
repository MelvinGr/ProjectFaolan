using Faolan.Core.Network.Codecs;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the generic self-buff MagicBuffIIR data tail: BuildBuffData with the captured
	// "Scythe Shield Rank 3" fields must reproduce the 50-byte blob the shared ApplySpell path used to clone verbatim.
	public class SelfBuffCodecTests
	{
		[Fact]
		public void BuildBuffData_ReproducesCapturedScytheShieldR3Blob()
		{
			// The verbatim default from ApplySpell (Scythe Shield Rank 3): spell 145534, duration 1277478 cs,
			// magnitude 80, effect nonce 0x14C5C912.
			byte[] captured =
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x13, 0x7E, 0x26, // duration@8 = 1277478
				0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x38, 0x7E, // spell@22 = 145534
				0x00, 0x00, 0x00, 0x50, // magnitude@26 = 80
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, // magnitude@38 = 80
				0x00, 0x00, 0x00, 0x00, 0x14, 0xC5, 0xC9, 0x12 // nonce@46 = 0x14C5C912
			];

			var built = SelfBuffCodec.BuildBuffData(145534, 1277478, 0x14C5C912);

			Assert.Equal(50, built.Length);
			Assert.Equal(captured, built);
		}
	}
}
