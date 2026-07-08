namespace Faolan.Core.Network.Codecs.Combat
{
	// The MagicBuffIIR (ApplySpell 0x6B87E316) DATA tail for a player buff - the bytes AFTER the ApplySpell header
	// ([op][0xC350][applyTo][u16 0][spellId][0xC350][applyFrom]). Constructed field-by-field; it replaces the verbatim
	// captured blobs the buff send paths used to clone-and-patch. Decoded from those captures and proven byte-exact by
	// SelfBuffCodecTests.
	//
	// Two wire forms exist (both big-endian, identical up to the magnitude words):
	//   - the 50-byte SHORT form (BuildBuffData) - ordinary self-buffs / offensive casts;
	//   - the 54-byte LONG form (BuildBuffDataLong) - one extra zero word between the two magnitudes. The client's
	//     MagicBuffIIR_c::ReadSubClass wants this shape for the spellweave trance/component buffs (it runs off the end
	//     of the short form and rejects them) and it is also the captured shape of the mount ride buff.
	//
	// Fields: duration@8 (centiseconds; the client counts it down and ApplySpell reads it back for expiry, #274),
	// spell BASE id@22 (= full wire id >> 8, the (base<<8)|rank buff-id convention), a paired magnitude word (80 in
	// the captures) and a per-application effect NONCE (so distinct buffs don't collapse onto one effect instance
	// client-side). The two 0x00010000 words @12/@16 are capture-derived flags.
	public static class SelfBuffCodec
	{
		public const uint DefaultMagnitude = 80;

		// 50-byte SHORT form: ordinary self-buff / offensive MagicBuff cast.
		public static byte[] BuildBuffData(uint spellBaseId, uint durationCentiseconds, uint effectNonce, uint magnitude = DefaultMagnitude)
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
				.WriteUInt32(magnitude)
				.WriteUInt32(0)
				.WriteUInt32(effectNonce)
				.ToArray();
		}

		// 54-byte LONG form: the spellweave trance/component buffs and the mount ride buff (one extra zero word before
		// the second magnitude vs the short form).
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
