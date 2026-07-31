using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// KnubotFinishTradeIIR (0x7446F3FA) - the vendor/NPC trade-close. ReadSubClass (FUN_009a7c20) runs the Knubot
	// base prefix (FUN_009a7e00, a u16 streaming version) then a target entity ref + trailer. Body observed as
	// [u16 version][u32 targetClass = 0xC350][u32 targetInstance][u64][u32]. Grammar from the client reader
	// (docs/re/v1-iir-readers.md), byte-exact.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class KnubotFinishTradeCodec
	{
		public const uint Opcode = (uint)IirTypes.KnubotFinishTradeIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(KnubotFinishTradeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static KnubotFinishTradeIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(KnubotFinishTradeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static KnubotFinishTradeIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "KnubotFinishTradeIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var version = r.ReadU16Be();
			var targetClass = r.ReadU32Be();
			var targetInstance = r.ReadU32Be();
			var trailer64 = r.ReadU64Be();
			return new KnubotFinishTradeIIR(handle, env, version, targetClass, targetInstance, trailer64, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, KnubotFinishTradeIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteByte(v.Env)
				.WriteUInt16(v.Version)
				.WriteUInt32(v.TargetClass)
				.WriteUInt32(v.TargetInstance)
				.WriteUInt64(v.Trailer64)
				.WriteUInt32(v.Trailer32);
		}
	}

	// KnubotFinishTradeIIR (0x7446F3FA).
	public sealed record KnubotFinishTradeIIR(uint Handle, byte Env, ushort Version, uint TargetClass, uint TargetInstance, ulong Trailer64, uint Trailer32);
}
