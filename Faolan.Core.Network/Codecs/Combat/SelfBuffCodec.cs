namespace Faolan.Core.Network.Codecs.Combat
{
	// The MagicBuffIIR (ApplySpell 0x6B87E316) DATA tail for a player buff - the bytes AFTER the ApplySpell header
	// ([op][0xC350][applyTo][u16 0][spellId][0xC350][applyFrom]). Constructed field-by-field; it replaces the verbatim
	// captured blobs the buff send paths used to clone-and-patch. Decoded from those captures and proven byte-exact by
	// SelfBuffCodecTests.
	//
	// ONE wire form exists in traffic: the 54-byte LONG form (BuildBuffDataLong). Across all 111
	// Captures/*.tshark.json the only s2c MagicBuffIIR data tails are 54 bytes (x1938) and 0 (x982, the END twin).
	// A 50-byte tail occurs ZERO times.
	//
	// Which of the two a record carries is decided by the LOW BYTE of the wire spell id, which is the client's FORM
	// selector and not a rank (#438): MagicBuffIIR_c::ReadSubClass switches on it three ways - 0 = APPLY (58 more
	// bytes), 1 = END (nothing more), 2 = UPDATE (20 more, which retail never sends). The captures show the exact
	// functional dependency with zero exceptions, and the same base ids appear under both 0 and 1, which is a
	// start/stop pair rather than two ranks of one spell. SendBuffRemoval builds the END twin as `id | 0x01`.
	//
	// A 50-byte "SHORT form" used to live here for "ordinary self-buffs", but it was never retail: it originated as a
	// hand-built blob in our own ApplySpell path, and its golden test asserted the codec against that same invented
	// blob, so it could never fail. The client's MagicBuffIIR_c::ReadSubClass reads 4 bytes past the end of it and
	// rejects the whole record ("ReadSubClass for IIR 1804067606 (MagicBuffIIR_c) failed! datasize left in stream=4,
	// raw=76"), which is why cast buffs rendered no icon at all. Removed 2026-07-16 (#274); do not reintroduce a
	// second form without a capture that shows one.
	//
	// Field names below are the CLIENT's own, recovered in #438 from MagicBuffIIR_c::Activate (FUN_007a1580), whose
	// GameCode/Magic log format spells out "BuffTemplate / CasterID / CastingLevel / Counter / CounterType /
	// CounterTimeLeft", cross-referenced against the reader's write offsets (tools/ghidra/DumpMagicBuffConsumers.java).
	// Full grammar + the empirical-to-client name mapping: docs/subsystems/npc-spell-applyspell.md.
	//
	//   @0  Counter        @4  CounterType    - we send 0; Activate skips the update entirely when Counter is 0
	//   @8  Duration       centiseconds (Activate multiplies by 10 for ms), read back here for expiry (#274)
	//   @12 / @13          the two bools the buff-add takes
	//   @14..41            a 7-word DESCRIPTOR the client passes to the buff-add by pointer; the wire words land in
	//                      its slots [0],[2],[1],[3],[8],[4],[5], so @22 -> slot[1] = the spell BASE id (full wire
	//                      id >> 8) and @26 -> slot[3]
	//   @42 CastingLevel   the CASTER's level
	//   @46 u64            an absolute EXPIRY stamp - see the warning below
	//
	// **@42 is CastingLevel, and it is named by the client, not inferred.** CasterID is the ID32 at +0x3C/+0x40 (the
	// wire's [0xC350][applyFrom]), which the buff-add takes by pointer - so +0x38, this word, is the only offset left
	// for the other name. It was long sent as a fixed 80 because most captures are level-80 characters; across the
	// beach->city leveling capture the fatality buffs walk 1 -> 2 -> 3 -> 4 -> 5 monotonically, and scanning all 111
	// Captures/*.tshark.json, @42 is single-valued for 909 of 918 (capture, caster) pairs - the 9 that vary are
	// leveling sessions, and they vary monotonically. That is a character level.
	//
	// ⚠️ [V0] @26 is NOT the same field seen twice, and stays open. It goes into slot[3] of the descriptor above,
	// beside the spell id, while @42 goes onto the IIR itself - two different destinations, which is why they can
	// disagree (1563 equal / 283 below / 92 above, over 1938 captured applies). Sending the caster's level in both is
	// what the evidence supports; it is not a claim that @26 is decoded. What IS settled is that neither word is a
	// magnitude or a stack count - the question #438 was opened to answer.
	//
	// ⚠️ `effectNonce` is a MISNOMER, kept only because renaming it reaches into ActiveBuff persistence. @46..53 is
	// ONE u64, and the client treats it as an absolute expiry: Activate computes (stamp - <global clock>) / 10 and,
	// if that is positive and SHORTER than Duration, overwrites Duration with it. That is how retail resumes a buff
	// that is already partly elapsed. Our value is written into the low half with a zero high half, so it fails the
	// `0 < x <= Duration` test and is ignored - which is exactly why it has always looked like the client does not
	// validate this field. Sourcing it properly is Gitea #480.
	public static class SelfBuffCodec
	{
		// The 54-byte buff-data tail - the only shape retail sends (see the class comment). `casterLevel` fills @26/@42;
		// it has no default ON PURPOSE, so a new call site has to say whose level it is rather than inheriting an 80
		// that is only right for a level-80 character.
		public static byte[] BuildBuffDataLong(uint spellBaseId, uint durationCentiseconds, uint effectNonce, uint casterLevel)
		{
			return new FaolanStream()
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(durationCentiseconds)
				.WriteUInt32(0x00010000)
				.WriteUInt32(0x00010000)
				.WriteUInt16(0x0000)
				.WriteUInt32(spellBaseId) // high byte 0 (base id < 0x1000000)
				.WriteUInt32(casterLevel)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(casterLevel)
				.WriteUInt32(0)
				.WriteUInt32(effectNonce)
				.ToArray();
		}
	}
}
