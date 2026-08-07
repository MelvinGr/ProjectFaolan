using System;
using System.Linq;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The 54-byte DATA region of a MagicBuffIIR APPLY - the bytes after `[subop][class][applyTo][env][spellBaseId]
	// [form][class][applyFrom]`. A sub-record: it has no opcode of its own and is only ever composed into
	// MagicBuffCodec's `Data`.
	//
	// ONE wire form exists in traffic. Across all 111 Captures/*.tshark.json the only s2c MagicBuffIIR data regions are
	// 54 bytes (x1938) and 0 (x982, the END twin). A 50-byte "SHORT form" used to live here for "ordinary self-buffs",
	// but it was never retail: it originated as a hand-built blob in our own ApplySpell path, and its golden test
	// asserted the codec against that same invented blob, so it could never fail. The client's
	// MagicBuffIIR_c::ReadSubClass reads 4 bytes past the end of it and rejects the whole record ("ReadSubClass for IIR
	// 1804067606 (MagicBuffIIR_c) failed! datasize left in stream=4, raw=76"), which is why cast buffs rendered no icon
	// at all. Removed 2026-07-16 (#274); do not reintroduce a second form without a capture that shows one.
	//
	// Field names are the CLIENT's own, recovered in #438 from MagicBuffIIR_c::Activate (FUN_007a1580), whose
	// GameCode/Magic log format spells out "BuffTemplate / CasterID / CastingLevel / Counter / CounterType /
	// CounterTimeLeft", cross-referenced against the reader's write offsets (tools/ghidra/DumpMagicBuffConsumers.java).
	// Full grammar + the empirical-to-client name mapping: docs/subsystems/npc-spell-applyspell.md.
	//
	//   @0  Counter        @4  CounterType    - we send 0; Activate skips the update entirely when Counter is 0
	//   @8  Duration       centiseconds (Activate multiplies by 10 for ms), read back here for expiry (#274)
	//   @12 / @13          the two bools the buff-add takes
	//   @14..41            a 7-word DESCRIPTOR the client passes to the buff-add by pointer. `Descriptor` here is in
	//                      WIRE order; the client lands those words in its slots [0],[2],[1],[3],[8],[4],[5], so
	//                      Descriptor[2] (@22) is slot[1] = the spell BASE id and Descriptor[3] (@26) is slot[3]
	//   @42 CastingLevel   the CASTER's level
	//   @46 u64            an absolute EXPIRY stamp - see the warning below
	//
	// **CastingLevel is named by the client, not inferred.** CasterID is the ID32 at +0x3C/+0x40 (the wire's
	// [class][applyFrom]), which the buff-add takes by pointer - so +0x38, this word, is the only offset left for the
	// other name. It was long sent as a fixed 80 because most captures are level-80 characters; across the beach->city
	// leveling capture the fatality buffs walk 1 -> 2 -> 3 -> 4 -> 5 monotonically, and scanning all 111
	// Captures/*.tshark.json it is single-valued for 909 of 918 (capture, caster) pairs - the 9 that vary are leveling
	// sessions, and they vary monotonically. That is a character level.
	//
	// ⚠️ [V0] Descriptor[3] (@26) is NOT the same field seen twice, and stays open. It goes into slot[3] of the
	// descriptor, beside the spell id, while CastingLevel goes onto the IIR itself - two different destinations, which
	// is why they can disagree (1563 equal / 283 below / 92 above, over 1938 captured applies). Sending the caster's
	// level in both is what the evidence supports; it is not a claim that it is decoded. What IS settled is that
	// neither word is a magnitude or a stack count - the question #438 was opened to answer.
	//
	// ExpiryStamp is ONE u64 and the client treats it as an ABSOLUTE expiry: Activate computes
	// (stamp - <the client's ServerTime>) / 10 and, if that is positive and SHORTER than Duration, overwrites Duration
	// with it. That is how retail resumes a buff that is already partly elapsed - a relog, or an entity streaming into
	// view mid-buff.
	//
	// **The clock is the server's UPTIME IN MILLISECONDS, and we already send it** (#480). The client's ServerTime_c
	// has exactly one input: the `DispatchPong` (0x2082) reply to its LatencyCheck poll, whose trailing 64-bit
	// MICROSECOND uptime counter its signal slot divides by 1000. So `stamp = uptimeMs + durationMs`, and both halves
	// come out of `ServerUptimeClock`. Verified over the corpus, not just read out of the client: across the 94 captures
	// carrying both a pong and an apply, `stamp - (uptimeMs + duration_cs*10)` has a MEDIAN of +6 ms and lands within
	// 250 ms for 2551 of 2929 applies (98.6% within 5 s; the residue is captures spanning a reconnect, where the
	// nearest pong belongs to a different server instance).
	// [V1: tools/ghidra/DumpServerTimeClock.java + Faolan.Tests/MagicBuffExpiryStampTests.cs]
	//
	// It was long written as a per-application "effect nonce" in the LOW half with a zero high half, which always
	// failed the `0 < x <= Duration` test - which is exactly why it looked like the client does not validate the field.
	public static class MagicBuffDataCodec
	{
		public const int Length = 54;
		public const int DescriptorWords = 7;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(MagicBuffData value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The form every live buff send uses: `casterLevel` fills BOTH the descriptor slot and CastingLevel (see the
		// [V0] note above). It has no default ON PURPOSE, so a new call site has to say whose level it is rather than
		// inheriting an 80 that is only right for a level-80 character.
		public static byte[] Build(uint spellBaseId, uint durationCentiseconds, ulong expiryStamp, uint casterLevel, uint? targetLevel = null)
		{
			return Build(MagicBuffDatas.Apply(spellBaseId, durationCentiseconds, expiryStamp, casterLevel, targetLevel));
		}

		public static MagicBuffData Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MagicBuffDataCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static MagicBuffData Read(ref SpanReader r)
		{
			var counter = r.ReadU32Be();
			var counterType = r.ReadU32Be();
			var duration = r.ReadU32Be();
			var bool1 = r.ReadU8();
			var bool2 = r.ReadU8();

			var descriptor = new uint[DescriptorWords];
			for (var i = 0; i < DescriptorWords; i++)
				descriptor[i] = r.ReadU32Be();

			return new MagicBuffData(counter, counterType, duration, bool1, bool2, descriptor, r.ReadU32Be(), r.ReadU64Be());
		}

		internal static void Write(FaolanStream s, MagicBuffData v)
		{
			s.WriteUInt32(v.Counter).WriteUInt32(v.CounterType).WriteUInt32(v.Duration).WriteByte(v.Bool1).WriteByte(v.Bool2);
			for (var i = 0; i < DescriptorWords; i++)
			{
				s.WriteUInt32(i < v.Descriptor.Length
					? v.Descriptor[i]
					: 0u);
			}

			s.WriteUInt32(v.CastingLevel).WriteUInt64(v.ExpiryStamp);
		}
	}

	// The MagicBuffIIR APPLY data region. `Descriptor` is the seven words at @14..41 in WIRE order (see the codec).
	public sealed record MagicBuffData(uint Counter, uint CounterType, uint Duration, byte Bool1, byte Bool2, uint[] Descriptor, uint CastingLevel, ulong ExpiryStamp);

	// The semantic forms of a MagicBuffData, as VALUE factories.
	public static class MagicBuffDatas
	{
		// Descriptor[0] - the only descriptor word that is a non-zero constant across every captured apply.
		private const uint DescriptorLead = 1;

		// The APPLY form every buff we send uses. `expiryStamp` is an ABSOLUTE instant on the server's uptime-milli
		// clock (`ServerUptimeClock.Milliseconds`), not a duration and not an opaque nonce - see the codec's header.
		//
		// ⚠️ The payload holds TWO level slots and they are two different entities' levels (#658): `descriptor[3]`
		// is the CASTER's and `CastingLevel` is the TARGET's. Measured by SCOPE over all 2,929 captured applies -
		// `CastingLevel` varies within a target on 6 of 1,183 entities against 36 of 1,075 within a caster, and
		// `descriptor[3]` is the other way round (20 vs 48). Corroborated by the split that follows from it: 2,241
		// of the applies are SELF-applies, where the two are one number by construction, and they agree on 87% of
		// those against 35% of the rest.
		//
		// `targetLevel` therefore defaults to `casterLevel`, which is exactly right for a self-apply (most of our
		// call sites) and is what the single-argument form always assumed. A call site applying a buff to someone
		// ELSE should pass the target's level.
		// [V3: MagicBuffCorpusTests.TheTwoLevelSlots_AreTheCastersAndTheTargets]
		public static MagicBuffData Apply(uint spellBaseId, uint durationCentiseconds, ulong expiryStamp, uint casterLevel, uint? targetLevel = null)
		{
			return new MagicBuffData(0, 0, durationCentiseconds, 0x00, 0x01, [DescriptorLead, 0, spellBaseId, casterLevel, 0, 0, 0],
				targetLevel ?? casterLevel, expiryStamp);
		}

		// The APPLY form for a buff starting NOW and running for `durationCentiseconds` - the shape every live cast
		// wants, with the stamp sourced rather than invented. Kept beside Apply so a call site cannot half-source it.
		public static MagicBuffData ApplyNow(uint spellBaseId, uint durationCentiseconds, uint casterLevel, uint? targetLevel = null)
		{
			return Apply(spellBaseId, durationCentiseconds, ServerUptimeClock.ExpiryFromNow(durationCentiseconds * 10UL), casterLevel, targetLevel);
		}

		// The APPLY form of a buff with NO CASTER - the wire writes a zero class word where the entity reference would
		// be (MagicBuffCodec.Read), and the data region differs from `Apply` in exactly two slots: `Bool2` is 0 rather
		// than 1, and Descriptor[3] is 0 rather than the level. Both hold on all 11 casterless applies in the corpus,
		// 11 of 11, and the level still rides in `CastingLevel` - which is what makes this a distinct FORM rather than
		// `Apply` with a null caster.
		//
		// Retail's local-travel "Racer" (#636) is one of these: no caster, 0.5 s, the travelling player's own level.
		// [V2: NecroL80_..._chat-annotated-4 fr9793/fr11539, NecroL80_..._combat fr6160, Hakotep_..._gameplay0 fr812]
		public static MagicBuffData ApplyNoCaster(uint spellBaseId, uint durationCentiseconds, ulong expiryStamp, uint level)
		{
			return new MagicBuffData(0, 0, durationCentiseconds, 0x00, 0x00, [DescriptorLead, 0, spellBaseId, 0, 0, 0, 0], level, expiryStamp);
		}

		// The same, starting NOW - the shape a live send wants, with the stamp sourced rather than invented.
		public static MagicBuffData ApplyNowNoCaster(uint spellBaseId, uint durationCentiseconds, uint level)
		{
			return ApplyNoCaster(spellBaseId, durationCentiseconds, ServerUptimeClock.ExpiryFromNow(durationCentiseconds * 10UL), level);
		}
	}

	// The MagicBuffData -> `NpcSpell` column mapping (#304). A MAPPER, not a codec: the six columns it feeds are
	// SUB-BYTES of the words above, harvested by the importer from a captured NPC cast, and they are what the runtime
	// re-sends. Naming them here is what let the second 54-byte model go away in #530 wave 4 - the importer's
	// "NpcSpellEffect" and the buff sender's "BuildBuffDataLong" were building and reading the same 54 bytes under two
	// unrelated field sets, one sequential and one at sparse fixed offsets.
	//
	// ⚠️ The column names are MISNOMERS from the #304 empirical diff, kept because they are DB columns: `Magnitude`
	// (@29) was never a magnitude, `Rank` (@45) was never a rank, `Counter` (@50) was never a counter - each was the
	// low byte (or low half) of a wider field the client reads. Byte-exactness never depended on them.
	public static class NpcSpellEffectColumns
	{
		// The stored columns back to the 54 bytes retail sent. Every word not covered by a column is 0, which is what
		// the 21 captured NPC payloads carry - the two forms differ in which sub-bytes are populated, not in the
		// layout. `SpellId` is the WIRE id, so it straddles two descriptor words on the way out exactly as it does on
		// the way in.
		public static MagicBuffData ToBuffData(uint spellId, ushort duration, byte descriptorSlot3, byte castingLevel, byte flagA, byte descriptorSlot0, byte descriptorSlot8)
		{
			return new MagicBuffData(0, 0, duration, 0x00, flagA, [descriptorSlot0, 0, spellId >> 8, ((spellId & 0xFF) << 24) | descriptorSlot3, descriptorSlot8, 0, 0], castingLevel, 0);
		}

		// The full wire spell id - (base << 8) | rank - which straddles Descriptor[2] and Descriptor[3]: it is the four
		// bytes at @23..26, i.e. the low three of the base-id word and the high one of the word after it.
		public static uint SpellId(MagicBuffData v)
		{
			return ((Word(v, 2) & 0x00FFFFFF) << 8) | (Word(v, 3) >> 24);
		}

		// @10 - the low half of the u32 Duration at @8.
		public static ushort Duration(MagicBuffData v)
		{
			return (ushort)v.Duration;
		}

		// @13.
		public static byte FlagA(MagicBuffData v)
		{
			return v.Bool2;
		}

		// @17 / @29 / @33 - the low bytes of Descriptor[0] / Descriptor[3] / Descriptor[4].
		public static byte DescriptorSlot0(MagicBuffData v)
		{
			return (byte)Word(v, 0);
		}

		public static byte DescriptorSlot3(MagicBuffData v)
		{
			return (byte)Word(v, 3);
		}

		public static byte DescriptorSlot8(MagicBuffData v)
		{
			return (byte)Word(v, 4);
		}

		// @45 - the low byte of the u32 CastingLevel at @42.
		public static byte CastingLevel(MagicBuffData v)
		{
			return (byte)v.CastingLevel;
		}

		private static uint Word(MagicBuffData v, int index)
		{
			return v.Descriptor.ElementAtOrDefault(index);
		}
	}
}
