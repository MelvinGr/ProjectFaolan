using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// ChainKillIIR (0x0EED7068) - a kill-chain/streak count (30 captured). ReadSubClass (FUN_0079c0b0) reads one u32
	// (observed a small count). Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class ChainKillCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.ChainKillIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ChainKillIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, uint count, byte env = 0x00)
		{
			return Build(new ChainKillIIR(handle, env, count));
		}

		public static ChainKillIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ChainKillCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static ChainKillIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "ChainKillIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new ChainKillIIR(handle, env, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, ChainKillIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(v.Count);
		}
	}

	// ChainKillIIR (0x0EED7068).
	public sealed record ChainKillIIR(uint Handle, byte Env, uint Count);
}
