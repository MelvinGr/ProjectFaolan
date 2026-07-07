using System;
using System.Buffers.Binary;
using Faolan.Core;

namespace Faolan.Core.Network.Codecs
{
	// The 54-byte effect payload appended to an ApplySpell (0x6B87E316) NPC cast (see NpcSpellCastBuilder). Decoded by
	// diffing all 21 distinct captured NpcSpell payloads (#304): byte-packed, big-endian, every other byte a constant 0.
	//   @10 u16 = Duration (centiseconds; == Spell.DurationMs/10 for normal buffs)
	//   @13 / @17 / @33 = effect flags (0/1, 1/2, 0/1 - exact meaning undecoded)
	//   @23 u32 = the effect SPELL ID (the wire id (Spell.Id << 8) | rank)
	//   @29 = Magnitude, @45 = Rank (equal on most casts; differ on a few - the two effect-strength bytes)
	//   @50 u32 = a per-cast Counter/timestamp the client does NOT validate (0 when we synthesise a cast)
	// Codec is byte-exact: ToBytes(Parse(captured)) == captured for all 21 goldens (NpcSpellEffectCodecTests).
	public readonly record struct NpcSpellEffect(uint SpellId, ushort Duration, byte Magnitude, byte Rank, byte FlagA, byte FlagB, byte FlagC, uint Counter = 0) : ICodec<NpcSpellEffect>
	{
		public const int Length = 54;

		// ICodec seam over the fixed-offset 54-byte payload (ToBytes/Parse remain the byte-packed core).
		public void Write(FaolanStream s) => s.WriteArray(ToBytes());
		public byte[] ToArray() => ToBytes();
		public static NpcSpellEffect Read(FaolanStream s) => Parse(s.ReadArray(Length));

		public byte[] ToBytes()
		{
			var b = new byte[Length];
			BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), Duration);
			b[13] = FlagA;
			b[17] = FlagB;
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(23), SpellId);
			b[29] = Magnitude;
			b[33] = FlagC;
			b[45] = Rank;
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(50), Counter);
			return b;
		}

		public static NpcSpellEffect Parse(ReadOnlySpan<byte> b)
		{
			if (b.Length != Length)
				throw new ArgumentException($"NpcSpellEffect payload must be {Length} bytes, got {b.Length}.", nameof(b));

			return new NpcSpellEffect(BinaryPrimitives.ReadUInt32BigEndian(b[23..]), BinaryPrimitives.ReadUInt16BigEndian(b[10..]), b[29], b[45], b[13], b[17], b[33], BinaryPrimitives.ReadUInt32BigEndian(b[50..]));
		}
	}
}
