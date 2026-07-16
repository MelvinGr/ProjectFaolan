using System;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the MagicBuffIIR data tail: the 54-byte form (BuildBuffDataLong) must reproduce the captured
	// spellweave trance/component buffs. It is the ONLY form retail sends - see the SelfBuffCodec class comment.
	//
	// A second test used to guard a 50-byte "SHORT form" against a blob captioned "captured", but that blob was our own
	// ApplySpell path's hand-built default, so the test asserted the codec against its own output and could never fail.
	// The client rejects that shape outright (MagicBuffIIR_c::ReadSubClass, "datasize left in stream=4, raw=76") and it
	// occurs ZERO times across all 103 Captures/*.tshark.json, so form + test were removed (#274, 2026-07-16).
	public class SelfBuffCodecTests
	{
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

		// The buff-persistence save side (#274) recovers the nonce back out of the constructed payload
		// (ActiveBuff.NonceFromPayload), so RestoreBuffs can rebuild a byte-compatible packet on relog. This is the
		// inverse of the builder above.
		[Fact]
		public void PayloadExtractors_RecoverNonce_FromBuffPayload()
		{
			var payload = SelfBuffCodec.BuildBuffDataLong(0x37F298, 640000, 0x195DA1FE);
			Assert.Equal(54, payload.Length);
			Assert.Equal(0x195DA1FEu, ActiveBuff.NonceFromPayload(payload));
			Assert.Equal(6400000u, ActiveBuff.DurationMsFromPayload(payload)); // 640000 cs -> ms

			// The empty removal twin carries no duration/nonce - the extractors must not read off the end.
			Assert.Equal(0u, ActiveBuff.NonceFromPayload([]));
			Assert.Equal(0u, ActiveBuff.DurationMsFromPayload([]));
		}
	}
}
