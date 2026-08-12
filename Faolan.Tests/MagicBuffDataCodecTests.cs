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
	// occurs ZERO times across Captures/*.tshark.json, so form + test were removed (#274, 2026-07-16). Re-measured
	// over the current 111 captures (#438): the only s2c MagicBuffIIR tails are 54 bytes (x1938) and 0 (x982).
	public class MagicBuffDataCodecTests
	{
		[Theory, InlineData(0x37F298u, 640000u, 0x195DA1FEuL, "00000000000000000009c40000010000000100000000 0037f298 00000050 00000000 00000000 00000000 00000050 00000000 195da1fe"),
		 InlineData(0x37E5A8u, 1200u, 0x18FC28DEuL, "0000000000000000000004b0000100000001000000000037e5a800000050000000000000000000000000000000500000000018fc28de"),
		 InlineData(0x3969D0u, 1000u, 0x18FC210EuL, "0000000000000000000003e800010000000100000000003969d000000050000000000000000000000000000000500000000018fc210e"),
		 InlineData(0x396AE9u, 650u, 0x18FC1CF6uL, "00000000000000000000028a0001000000010000000000396ae900000050000000000000000000000000000000500000000018fc1cf6")]
		// The 54-byte LONG form vs the captured spellweave trance + component buffs (frames 1644/1646/1648/1659).
		// Passing that session's own @46 expiry stamp is what makes each byte-exact - it is an absolute instant on the
		// capturing server's uptime clock, so at runtime it comes from ours (`ServerUptimeClock`), never from here.
		public void BuildBuffDataLong_MatchesSpellweaveCapture(uint spellBase, uint durationCs, ulong expiryStamp, string expected)
		{
			var bytes = MagicBuffDataCodec.Build(spellBase, durationCs, expiryStamp, 80, casterIsPlayer: true); // the spellweave capture is a level-80 necro
			Assert.Equal(expected.Replace(" ", ""), Convert.ToHexStringLower(bytes));
		}

		// The buff-tracking side (#274) reads the duration back out of the constructed payload, so the world tick
		// expires the buff at the same instant the client counts down to. The inverse of the builder above.
		[Fact]
		public void PayloadExtractors_RecoverDuration_FromBuffPayload()
		{
			var payload = MagicBuffDatas.Apply(0x37F298, 640000, 0x195DA1FE, 80, casterIsPlayer: true);
			Assert.Equal(54, MagicBuffDataCodec.Build(payload).Length);
			Assert.Equal(6400000u, ActiveBuff.DurationMsFromPayload(payload)); // 640000 cs -> ms

			// The removal twin carries NO data region at all - the extractor takes the absence, not an empty array.
			Assert.Equal(0u, ActiveBuff.DurationMsFromPayload(null));
		}
	}
}
