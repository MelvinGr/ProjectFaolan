using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     CombatReactiveIIR - the reactive element list, declaring which spellweave elements the caster may add while in
	///     the trance. It is what lets the client send the 0x03-tagged adds, and a component add's index indexes into this
	///     list. Layout: <c>[subop][class][caster][u32 fieldA=0][u8 tag=0x01][N x u32 elementBaseId]</c>.
	/// </summary>
	/// <remarks>
	///     elementBaseId is the full spell id shifted right by 8 (necromancer: Arcane Renewal 0x396AE9, Arcane Surge
	///     0x3969D8, Parasite Host 0x37E6D9, Death God 0x396A22, Death 0x396A3D). A per-add re-send zeroes the just-used
	///     element for its cooldown; pass 0 for a consumed slot to reproduce that. Decoded in
	///     tools/capture/decode_spellweave.py from NecroL80_..._consuming-potions-food-spellweaving (caster 0x0802E5D4);
	///     see docs/subsystems/combos.md.
	/// </remarks>
	public static class CombatReactiveCodec
	{
		public const uint Opcode = (uint)IirTypes.CombatReactiveIIR;

		/// <summary>The "list active" flag every captured s2c record carries.</summary>
		public const byte ActiveTag = 0x01;

		/// <summary>
		///     The c2s tag on a component add - the packet carrying the picked element's index in the list slot. Its
		///     0x02-tagged twin is the ack and carries an empty list, which is the only difference between the two.
		/// </summary>
		public const byte AddComponentTag = 0x03;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(CombatReactiveIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint caster, uint[] elementBaseIds)
		{
			return Build(new CombatReactiveIIR(caster, 0, ActiveTag, elementBaseIds));
		}

		public static CombatReactiveIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CombatReactiveCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CombatReactiveIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CombatReactiveIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var caster = r.ReadU32Be();
			var fieldA = r.ReadU32Be();
			var tag = r.ReadU8();

			// The element list runs to the end of the record: it carries no count of its own, so the reader has to be
			// told where the body stops rather than deriving it. A trailing partial word throws.
			var elements = new uint[r.Remaining / sizeof(uint)];
			for (var i = 0; i < elements.Length; i++)
				elements[i] = r.ReadU32Be();

			return new CombatReactiveIIR(caster, fieldA, tag, elements);
		}

		internal static void Write(FaolanStream s, CombatReactiveIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Caster).WriteUInt32(v.FieldA).WriteByte(v.Tag);
			foreach (var element in v.ElementBaseIds)
				s.WriteUInt32(element);
		}
	}

	/// <summary>The CombatReactiveIIR body.</summary>
	public sealed record CombatReactiveIIR(uint Caster, uint FieldA, byte Tag, uint[] ElementBaseIds);
}
