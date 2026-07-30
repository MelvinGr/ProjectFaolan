using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The 54-byte effect payload appended to an ApplySpell (0x6B87E316) NPC cast (see NpcSpellCastBuilder). Decoded by
	// diffing all 21 distinct captured NpcSpell payloads (#304): byte-packed, big-endian, every other byte a constant 0.
	//   @8  u32 = Duration (centiseconds; the client does *10 for ms). We store its low half at @10.
	//   @13 = FlagA, a bool the client reads as one
	//   @17 / @29 / @33 = the low bytes of descriptor slots [0] / [3] / [8]
	//   @23 u32 = the effect SPELL ID (the wire id (Spell.Id << 8) | rank)
	//   @45 = the low byte of the u32 CastingLevel at @42 - the CASTER's level
	//   @46 u64 = an absolute EXPIRY stamp
	// Codec is byte-exact: ToBytes(Parse(captured)) == captured for all 21 goldens (NpcSpellEffectCodecTests).
	//
	// ⚠️ The property names were MISNOMERS until #480 and the old ones still turn up in older commits and comments:
	// `Magnitude` (@29) was never a magnitude, `Rank` (@45) was never a rank, `Counter` (@50) was never a counter -
	// each was the low byte (or low half) of a wider field the client reads. They date from the #304 empirical diff,
	// before the client's own reader and Activate log format were walked (#438). Byte-exactness never depended on
	// them: every offset written is still the offset retail writes.
	// Full table and the old-to-new mapping: docs/subsystems/npc-spell-applyspell.md.
	//
	// ⚠️ ExpiryStamp is the WHOLE u64 at @46, not the u32 at @50 the old `Counter` wrote. `MagicBuffIIR_c::Activate`
	// treats it as an absolute expiry: it computes (stamp - <the client's global clock>) / 10 and, when that is
	// positive and SHORTER than Duration, overwrites Duration with it - which is how retail resumes a buff that is
	// already partly elapsed. Ours has always been ignored because a value in the low half alone fails the
	// `0 < x <= Duration` test. Sourcing it for real is the open half of #480.
	public readonly record struct NpcSpellEffect(uint SpellId, ushort Duration, byte DescriptorSlot3, byte CastingLevel, byte FlagA, byte DescriptorSlot0, byte DescriptorSlot8, ulong ExpiryStamp = 0) : ICodec<NpcSpellEffect>
	{
		public const int Length = 54;

		public static NpcSpellEffect Read(FaolanStream s)
		{
			return Parse(s.ReadArray(Length));
		}

		// ICodec seam over the fixed-offset 54-byte payload (ToArray/Parse remain the byte-packed core - the payload
		// is a sparse fixed-offset layout with every other byte a constant 0, which does not map to sequential writes).
		public void Write(FaolanStream s)
		{
			s.WriteArray(ToArray());
		}

		public byte[] ToArray()
		{
			var b = new byte[Length];
			b.WriteU16Be(10, Duration);
			b[13] = FlagA;
			b[17] = DescriptorSlot0;
			b.WriteU32Be(23, SpellId);
			b[29] = DescriptorSlot3;
			b[33] = DescriptorSlot8;
			b[45] = CastingLevel;
			b.WriteU64Be(46, ExpiryStamp);
			return b;
		}

		public static NpcSpellEffect Parse(ReadOnlySpan<byte> b)
		{
			if (b.Length != Length)
				throw new ArgumentException($"NpcSpellEffect payload must be {Length} bytes, got {b.Length}.", nameof(b));

			return new NpcSpellEffect(b.ReadU32Be(23), b.ReadU16Be(10), b[29], b[45], b[13], b[17], b[33],
				b.ReadU64Be(46));
		}
	}
}
