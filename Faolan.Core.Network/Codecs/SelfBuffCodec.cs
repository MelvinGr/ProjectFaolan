namespace Faolan.Core.Network.Codecs
{
	// The generic 50-byte MagicBuffIIR (0x6B87E316) DATA tail for a player SELF-buff - the bytes AFTER the ApplySpell
	// header ([op][0xC350][applyTo][u16 0][spellId][0xC350][applyFrom]). Constructed field-by-field; it replaces the
	// verbatim captured "Scythe Shield Rank 3" blob the shared ApplySpell path used to clone-and-patch. Decoded from
	// that capture and proven byte-exact by SelfBuffCodecTests.
	//
	// This is the 50-byte sibling of SpellweaveCodec.BuildBuffData (54 bytes): the spellweave buffs carry ONE extra
	// zero word between the two magnitude words - the client's MagicBuffIIR_c::ReadSubClass wants that 54-byte shape
	// for them, so they use SpellweaveCodec; ordinary self-buffs use this 50-byte form.
	//
	// Fields (big-endian): duration@8 (centiseconds; the client counts it down and ApplySpell reads it back for
	// expiry, #274), spell BASE id@22 (= full wire id >> 8, the (base<<8)|rank buff-id convention), magnitude@26/@38
	// (a paired strength word, 80 in the capture), and a per-application effect NONCE@46 (so distinct buffs don't
	// collapse onto one effect instance client-side). The two 0x00010000 words @12/@16 are capture-derived flags.
	public static class SelfBuffCodec
	{
		public const uint DefaultMagnitude = 80;

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
	}
}
