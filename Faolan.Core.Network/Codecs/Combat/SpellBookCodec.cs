using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// SpellBookIIR (0x6BA10936). ReadSubClass (FUN_00795ba0) triggers a spellbook refresh on the carrier handle; the
	// body carries a COUNT-FRAMED list whose elements the reader does not consume - so the wire word is
	// (count + 1) * 0x3F1 and an empty book is 0x3F1, not a constant. Grammar from the client reader
	// (docs/re/v1-iir-readers.md), byte-exact.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class SpellBookCodec
	{
		public const uint Opcode = (uint)IirTypes.SpellBookIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SpellBookIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, int count = 0, byte env = 0x00)
		{
			return Build(new SpellBookIIR(handle, env, count));
		}

		public static SpellBookIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SpellBookCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SpellBookIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SpellBookIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new SpellBookIIR(handle, env, r.ReadListCount());
		}

		internal static void Write(FaolanStream s, SpellBookIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(ListFrame.ForCount(v.Count));
		}
	}

	// SpellBookIIR (0x6BA10936).
	public sealed record SpellBookIIR(uint Handle, byte Env, int Count);
}
