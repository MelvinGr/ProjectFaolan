using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the MagicBuffIIR data tail. The 50-byte SHORT form (BuildBuffData) must reproduce the
	// captured "Scythe Shield Rank 3" self-buff blob the shared ApplySpell path used to clone verbatim; the 54-byte
	// LONG form (BuildBuffDataLong) must reproduce the captured spellweave trance/component buffs.
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

		[Theory, InlineData(0x37F298u, 640000u, 0x195DA1FEu, "00000000000000000009c40000010000000100000000 0037f298 00000050 00000000 00000000 00000000 00000050 00000000 195da1fe"),
		 InlineData(0x37E5A8u, 1200u, 0x18FC28DEu, "0000000000000000000004b0000100000001000000000037e5a800000050000000000000000000000000000000500000000018fc28de"),
		 InlineData(0x3969D0u, 1000u, 0x18FC210Eu, "0000000000000000000003e800010000000100000000003969d000000050000000000000000000000000000000500000000018fc210e"),
		 InlineData(0x396AE9u, 650u, 0x18FC1CF6u, "00000000000000000000028a0001000000010000000000396ae900000050000000000000000000000000000000500000000018fc1cf6")]
		// The 54-byte LONG form vs the captured spellweave trance + component buffs (frames 1644/1646/1648/1659).
		// Passing the captured effect nonce makes each byte-exact; at runtime the server assigns a unique nonce per buff.
		public void BuildBuffDataLong_MatchesSpellweaveCapture(uint spellBase, uint durationCs, uint nonce, string expected)
		{
			var bytes = SelfBuffCodec.BuildBuffDataLong(spellBase, durationCs, nonce);
			Assert.Equal(expected.Replace(" ", ""), Convert.ToHexStringLower(bytes));
		}
	}
}
