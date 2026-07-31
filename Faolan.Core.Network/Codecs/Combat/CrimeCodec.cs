using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// CrimeIIR (0xC7954504). ReadSubClass (FUN_0148c9f0) reads a gate byte and, when set, a u32. Body observed as
	// [u8 flag=1][u32 value]. Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class CrimeCodec
	{
		public const uint Opcode = (uint)IirTypes.CrimeIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CrimeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, byte flag, uint value, byte env = 0x00)
		{
			return Build(new CrimeIIR(handle, env, flag, value));
		}

		public static CrimeIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CrimeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CrimeIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CrimeIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var flag = r.ReadU8();
			return new CrimeIIR(handle, env, flag, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, CrimeIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteByte(v.Flag).WriteUInt32(v.Value);
		}
	}

	// CrimeIIR (0xC7954504).
	public sealed record CrimeIIR(uint Handle, byte Env, byte Flag, uint Value);
}
