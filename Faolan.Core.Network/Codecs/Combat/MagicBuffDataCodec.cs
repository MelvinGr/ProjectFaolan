using System;
using System.Linq;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     The 54-byte data region of a MagicBuffIIR apply - the bytes after
	///     <c>[subop][class][applyTo][env][spellBaseId][form][class][applyFrom]</c>. A sub-record: it has no opcode of its
	///     own and is only ever composed into <see cref="MagicBuffCodec" />'s data field.
	/// </summary>
	/// <remarks>
	///     One wire form exists in traffic. Across all the captures the only s2c MagicBuffIIR data regions are 54 bytes
	///     (x1938) and 0 (x982, the end twin). A 50-byte body is not retail and must not be reintroduced without a capture
	///     showing one: the client's MagicBuffIIR_c::ReadSubClass reads four bytes past the end of it and rejects the whole
	///     record ("ReadSubClass for IIR 1804067606 (MagicBuffIIR_c) failed! datasize left in stream=4, raw=76"), so the
	///     buff renders no icon at all.
	///     <para>
	///         Field names are the client's own, recovered from MagicBuffIIR_c::Activate (FUN_007a1580), whose GameCode/Magic
	///         log format spells out BuffTemplate / CasterID / CastingLevel / Counter / CounterType / CounterTimeLeft,
	///         cross-referenced against the reader's write offsets (tools/ghidra/DumpMagicBuffConsumers.java). Full grammar
	///         and the empirical-to-client name mapping are in docs/subsystems/npc-spell-applyspell.md.
	///     </para>
	///     <code>
	///     @0  Counter        @4 CounterType - we send 0; Activate skips the update entirely when Counter is 0
	///     @8  Duration       centiseconds (Activate multiplies by 10 for ms), read back here for expiry
	///     @12 / @13          the two bools the buff-add takes
	///     @14..41            a 7-word descriptor the client passes to the buff-add by pointer, in wire order; the
	///                        client lands those words in its slots [0],[2],[1],[3],[8],[4],[5], so Descriptor[2] (@22)
	///                        is slot[1], the spell base id, and Descriptor[3] (@26) is slot[3]
	///     @42 CastingLevel   a character level
	///     @46 u64            an absolute expiry stamp
	///     </code>
	///     <para>
	///         CastingLevel is named by the client rather than inferred: CasterID is the ID32 at +0x3C/+0x40 (the wire's
	///         [class][applyFrom]), which the buff-add takes by pointer, so +0x38 - this word - is the only offset left for
	///         the other name. It is a character level and not a constant: across the beach-to-city levelling capture the
	///         fatality buffs walk 1 to 5 monotonically, and over all captures it is single-valued for 909 of 918
	///         (capture, caster) pairs, the nine that vary being levelling sessions that vary monotonically.
	///     </para>
	///     <para>
	///         Descriptor[3] (@26) is not the same field seen twice: it goes into slot[3] of the descriptor beside the spell
	///         id while CastingLevel goes onto the IIR itself, two different destinations, which is why they disagree on 375
	///         of 1938 captured applies (283 below, 92 above). What is settled is that neither word is a magnitude or a
	///         stack count. [V0: what Descriptor[3] means]
	///     </para>
	///     <para>
	///         ExpiryStamp is one u64 and the client treats it as an absolute expiry: Activate computes
	///         (stamp - the client's ServerTime) / 10 and, if that is positive and shorter than Duration, overwrites Duration
	///         with it. That is how retail resumes a buff that is already partly elapsed - a relog, or an entity streaming
	///         into view mid-buff. Writing a nonce here always fails the <c>0 &lt; x &lt;= Duration</c> test, which makes the
	///         client look as though it ignores the field.
	///     </para>
	///     <para>
	///         The clock is the server's uptime in milliseconds. The client's ServerTime_c has exactly one input: the
	///         DispatchPong reply to its LatencyCheck poll, whose trailing 64-bit microsecond uptime counter its signal slot
	///         divides by 1000. So stamp = uptimeMs + durationMs, and both halves come out of
	///         <see cref="ServerUptimeClock" />. Verified over the corpus as well as in the client: across the 94 captures
	///         carrying both a pong and an apply, stamp - (uptimeMs + duration_cs * 10) has a median of +6 ms and lands
	///         within 250 ms for 2551 of 2929 applies (98.6% within 5 s, the residue being captures that span a reconnect,
	///         where the nearest pong belongs to a different server instance).
	///         [V1: tools/ghidra/DumpServerTimeClock.java + Faolan.Tests/MagicBuffExpiryStampTests.cs]
	///     </para>
	/// </remarks>
	public static class MagicBuffDataCodec
	{
		public const int Length = 54;
		public const int DescriptorWords = 7;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(MagicBuffData value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     The form every live buff send uses: <paramref name="casterLevel" /> fills both the descriptor slot and
		///     CastingLevel. It deliberately has no default, so a new call site has to say whose level it is rather than
		///     inheriting one that is only right for a level-80 character.
		/// </summary>
		public static byte[] Build(uint spellBaseId, uint durationCentiseconds, ulong expiryStamp, uint casterLevel, bool casterIsPlayer, uint? targetLevel = null)
		{
			return Build(MagicBuffDatas.Apply(spellBaseId, durationCentiseconds, expiryStamp, casterLevel, casterIsPlayer, targetLevel));
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

	/// <summary>
	///     The MagicBuffIIR apply data region. <c>Descriptor</c> is the seven words at @14..41 in wire order; see
	///     <see cref="MagicBuffDataCodec" />.
	/// </summary>
	public sealed record MagicBuffData(uint Counter, uint CounterType, uint Duration, byte Bool1, byte Bool2, uint[] Descriptor, uint CastingLevel, ulong ExpiryStamp);

	/// <summary>The semantic forms of a <see cref="MagicBuffData" />, as value factories.</summary>
	public static class MagicBuffDatas
	{
		/// <summary>
		///     Descriptor[0] for a plain cast. The word is a server-side provenance tag, not a constant: over 3,424 captured
		///     applies it is 1 (x3055), 2 (x367) or 3 (x2). A 3 says Descriptor[1] names the combo that applied the buff
		///     (MagicBuffCorpusTests.DescriptorZeroIsThree_ExactlyWhenDescriptorOneNamesACombo); 2 is undecoded, and the
		///     obvious readings are all measured dead - see MagicBuffCorpusTests.
		/// </summary>
		/// <remarks>
		///     Nothing in the client reads the slot. The word lands at BuffInfo_c+0xA4 (ActivateBuff to AddBuffInfo arg 8),
		///     and the magic module, every FindBuffInfo caller, the com.GameInterface.Game.BuffData GUI exporter and the
		///     spawn-scaffold serialization are all attributed without touching it, while the BuffInfo_c constructor defaults
		///     the slot to 1. So 1 is the client's own neutral value and writing it for the direct casts we perform is
		///     correct by construction. See docs/subsystems/npc-spell-applyspell.md.
		/// </remarks>
		private const uint PlainCastDescriptorLead = 1;

		/// <summary>
		///     The apply form every buff we send uses. <paramref name="expiryStamp" /> is an absolute instant on the server's
		///     uptime-millisecond clock (ServerUptimeClock.Milliseconds), not a duration and not an opaque nonce; see
		///     <see cref="MagicBuffDataCodec" />.
		/// </summary>
		/// <remarks>
		///     The payload holds two level slots and they are two different entities' levels: Descriptor[3] is the caster's
		///     and CastingLevel the target's. Measured by scope over all 2,929 captured applies - CastingLevel varies within
		///     a target on 6 of 1,183 entities against 36 of 1,075 within a caster, and Descriptor[3] the other way round (20
		///     against 48). Corroborated by the split that follows: 2,241 of the applies are self-applies, where the two are
		///     one number by construction, and they agree on 87% of those against 35% of the rest. So
		///     <paramref name="targetLevel" /> defaults to <paramref name="casterLevel" />, which is right for a self-apply;
		///     a call site buffing someone else should pass the target's level.
		///     [V3: MagicBuffCorpusTests.TheTwoLevelSlots_AreTheCastersAndTheTargets]
		///     <para>
		///         <paramref name="casterIsPlayer" /> drives bool2, which is exactly "the caster is a player character" and
		///         not a constant: it splits the corpus perfectly, 1 on all 2,090 applies whose caster is a player and 0 on
		///         all 1,334 whose caster is an NPC or absent. It is a property of the caster rather than of the spell - of
		///         the 159 casters that cast more than one distinct spell none vary it, while 4 of the 128 spells cast by more
		///         than one caster do. [V3: MagicBuffCorpusTests.Bool2_IsWhetherTheCasterIsAPlayer]
		///     </para>
		/// </remarks>
		public static MagicBuffData Apply(uint spellBaseId, uint durationCentiseconds, ulong expiryStamp, uint casterLevel, bool casterIsPlayer, uint? targetLevel = null)
		{
			return new MagicBuffData(0, 0, durationCentiseconds, 0x00, casterIsPlayer ? (byte)0x01 : (byte)0x00,
				[PlainCastDescriptorLead, 0, spellBaseId, casterLevel, 0, 0, 0], targetLevel ?? casterLevel, expiryStamp);
		}

		/// <summary>
		///     The apply form for a buff starting now and running for <paramref name="durationCentiseconds" /> - the shape
		///     every live cast wants, with the stamp sourced rather than invented. It sits beside <see cref="Apply" /> so a
		///     call site cannot half-source it.
		/// </summary>
		public static MagicBuffData ApplyNow(uint spellBaseId, uint durationCentiseconds, uint casterLevel, bool casterIsPlayer, uint? targetLevel = null)
		{
			return Apply(spellBaseId, durationCentiseconds, ServerUptimeClock.ExpiryFromNow(durationCentiseconds * 10UL), casterLevel, casterIsPlayer, targetLevel);
		}

		/// <summary>
		///     The apply form of a buff with no caster. The wire writes a zero class word where the entity reference would be
		///     (see <see cref="MagicBuffCodec" />), and the data region differs from a player's <see cref="Apply" /> in
		///     exactly two slots: Bool2 is 0 - no caster is not a player caster, so that is the same rule rather than an
		///     exception - and Descriptor[3] is 0 rather than the level, which still rides in CastingLevel. Both hold on all
		///     11 casterless applies in the corpus, which is what makes this a distinct form rather than
		///     <see cref="Apply" /> with a null caster.
		/// </summary>
		/// <remarks>
		///     Retail's local-travel "Racer" is one of these: no caster, 0.5 s, the travelling player's own level.
		///     [V2: NecroL80_..._chat-annotated-4 fr9793/fr11539, NecroL80_..._combat fr6160, Hakotep_..._gameplay0 fr812]
		/// </remarks>
		public static MagicBuffData ApplyNoCaster(uint spellBaseId, uint durationCentiseconds, ulong expiryStamp, uint level)
		{
			return new MagicBuffData(0, 0, durationCentiseconds, 0x00, 0x00, [PlainCastDescriptorLead, 0, spellBaseId, 0, 0, 0, 0], level, expiryStamp);
		}

		/// <summary>The same, starting now, with the stamp sourced rather than invented.</summary>
		public static MagicBuffData ApplyNowNoCaster(uint spellBaseId, uint durationCentiseconds, uint level)
		{
			return ApplyNoCaster(spellBaseId, durationCentiseconds, ServerUptimeClock.ExpiryFromNow(durationCentiseconds * 10UL), level);
		}
	}

	/// <summary>
	///     The <see cref="MagicBuffData" /> to NpcSpell column mapping. A mapper, not a codec: the columns it feeds are
	///     sub-bytes of the words above, harvested by the importer from a captured NPC cast, and they are what the runtime
	///     re-sends.
	/// </summary>
	/// <remarks>
	///     Some column names are misnomers kept because they are database columns: Magnitude (@29) is not a magnitude, Rank
	///     (@45) is not a rank and Counter (@50) is not a counter - each is the low byte, or low half, of a wider field the
	///     client reads. Byte-exactness does not depend on them.
	/// </remarks>
	public static class NpcSpellEffectColumns
	{
		/// <summary>
		///     The stored columns back to the 54 bytes retail sent. Every word not covered by a column is 0, which is what the
		///     21 captured NPC payloads carry: the two forms differ in which sub-bytes are populated, not in the layout.
		///     <paramref name="spellId" /> is the wire id, so it straddles two descriptor words on the way out exactly as it
		///     does on the way in.
		/// </summary>
		public static MagicBuffData ToBuffData(uint spellId, ushort duration, byte descriptorSlot3, byte castingLevel, byte flagA, byte descriptorSlot0, byte descriptorSlot8)
		{
			return new MagicBuffData(0, 0, duration, 0x00, flagA, [descriptorSlot0, 0, spellId >> 8, ((spellId & 0xFF) << 24) | descriptorSlot3, descriptorSlot8, 0, 0], castingLevel, 0);
		}

		/// <summary>
		///     The full wire spell id, (base &lt;&lt; 8) | rank, which straddles Descriptor[2] and Descriptor[3]: the four
		///     bytes at @23..26, i.e. the low three of the base-id word and the high one of the word after it.
		/// </summary>
		public static uint SpellId(MagicBuffData v)
		{
			return ((Word(v, 2) & 0x00FFFFFF) << 8) | (Word(v, 3) >> 24);
		}

		/// <summary>@10 - the low half of the u32 Duration at @8.</summary>
		public static ushort Duration(MagicBuffData v)
		{
			return (ushort)v.Duration;
		}

		/// <summary>@13.</summary>
		public static byte FlagA(MagicBuffData v)
		{
			return v.Bool2;
		}

		/// <summary>@17 / @29 / @33 - the low bytes of Descriptor[0] / Descriptor[3] / Descriptor[4].</summary>
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

		/// <summary>@45 - the low byte of the u32 CastingLevel at @42.</summary>
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
