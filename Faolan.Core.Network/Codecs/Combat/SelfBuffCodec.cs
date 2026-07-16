namespace Faolan.Core.Network.Codecs.Combat
{
	// The MagicBuffIIR (ApplySpell 0x6B87E316) DATA tail for a player buff - the bytes AFTER the ApplySpell header
	// ([op][0xC350][applyTo][u16 0][spellId][0xC350][applyFrom]). Constructed field-by-field; it replaces the verbatim
	// captured blobs the buff send paths used to clone-and-patch. Decoded from those captures and proven byte-exact by
	// SelfBuffCodecTests.
	//
	// ONE wire form exists: the 54-byte LONG form (BuildBuffDataLong). Retail sends nothing else - across all 103
	// Captures/*.tshark.json the only s2c MagicBuffIIR data tails are 54 bytes (x1412) and 0 (x578, the rank-1 end
	// twin). A 50-byte tail occurs ZERO times.
	//
	// A 50-byte "SHORT form" used to live here for "ordinary self-buffs", but it was never retail: it originated as a
	// hand-built blob in our own ApplySpell path, and its golden test asserted the codec against that same invented
	// blob, so it could never fail. The client's MagicBuffIIR_c::ReadSubClass reads 4 bytes past the end of it and
	// rejects the whole record ("ReadSubClass for IIR 1804067606 (MagicBuffIIR_c) failed! datasize left in stream=4,
	// raw=76"), which is why cast buffs rendered no icon at all. Removed 2026-07-16 (#274); do not reintroduce a
	// second form without a capture that shows one.
	//
	// Fields: duration@8 (centiseconds; the client counts it down and ApplySpell reads it back for expiry, #274),
	// spell BASE id@22 (= full wire id >> 8, the (base<<8)|rank buff-id convention), a paired magnitude word (80 in
	// the captures) and a per-application effect NONCE (so distinct buffs don't collapse onto one effect instance
	// client-side). The two 0x00010000 words @12/@16 are capture-derived flags.
	public static class SelfBuffCodec
	{
		public const uint DefaultMagnitude = 80;

		// The 54-byte buff-data tail - the only shape retail sends (see the class comment).
		public static byte[] BuildBuffDataLong(uint spellBaseId, uint durationCentiseconds, uint effectNonce, uint magnitude = DefaultMagnitude)
		{
			return new FaolanStream()
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(durationCentiseconds)
				.WriteUInt32(0x00010000)
				.WriteUInt32(0x00010000)
				.WriteUInt16(0x0000)
				.WriteUInt32(spellBaseId) // high byte 0 (base id < 0x1000000)
				.WriteUInt32(magnitude)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(magnitude)
				.WriteUInt32(0)
				.WriteUInt32(effectNonce)
				.ToArray();
		}
	}
}
