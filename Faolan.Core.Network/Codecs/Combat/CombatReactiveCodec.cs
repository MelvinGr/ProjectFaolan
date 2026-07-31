using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// CombatReactiveIIR (0x54C02089) - the reactive element list: declares to the client which spellweave elements the
	// caster can ADD while in the trance. This is what lets the client send the 0x03-tagged adds, and a component add's
	// index indexes INTO this list.
	//
	//   [subop][class][caster][u32 fieldA=0][u8 tag=0x01][N x u32 elementBaseId]
	//
	// elementBaseId = full spell id >> 8 (necro: Arcane Renewal 0x396AE9, Arcane Surge 0x3969D8, Parasite Host
	// 0x37E6D9, Death God 0x396A22, Death 0x396A3D). A per-add re-send zeroes the just-used element (cooldown); pass 0
	// for a consumed slot to reproduce that. `tag` 0x01 is a constant "list active" flag.
	//
	// Decoded in tools/capture/decode_spellweave.py from NecroL80_..._consuming-potions-food-spellweaving
	// (caster 0x0802E5D4); docs/subsystems/combos.md.
	public static class CombatReactiveCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CombatReactiveIIR;

		// The "list active" flag every captured record carries.
		public const byte ActiveTag = 0x01;

		// CANONICAL - the only method here that writes bytes.
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

			// The element list runs to the end of the record - it carries no count of its own, which is why the reader
			// has to be told where the body stops rather than deriving it. A trailing partial word throws.
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

	// CombatReactiveIIR (0x54C02089).
	public sealed record CombatReactiveIIR(uint Caster, uint FieldA, byte Tag, uint[] ElementBaseIds);
}
