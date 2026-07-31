using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// CharInPlayIIR (0x864CFEF8) - the "character is now in play" signal. 9 bytes past the subop:
	//
	//   [u32 subop][u32 class][u32 handle][u8 env]
	//
	// An EMPTY record - the client reader (FUN_0075a920) only resolves the SimpleChar for `handle` and logs, so the
	// carrier handle is the whole message (docs/re/iir-readers-combat-world.md). `env` is the base IIR's flag byte,
	// 0 in every capture.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class CharInPlayCodec
	{
		public const uint Opcode = (uint)IirTypes.CharInPlayIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CharInPlayIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static CharInPlayIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharInPlayCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CharInPlayIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CharInPlayIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			return new CharInPlayIIR(handle, r.ReadU8());
		}

		internal static void Write(FaolanStream s, CharInPlayIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env);
		}
	}

	// CharInPlayIIR (0x864CFEF8).
	public sealed record CharInPlayIIR(uint Handle, byte Env = 0);
}
