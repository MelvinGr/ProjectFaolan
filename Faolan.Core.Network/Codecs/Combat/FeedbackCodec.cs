using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// FeedbackIIR (0xBA1FD222) - NOT the FormatFeedback FIFO banner (0x8D5A8B6B); a plain feedback record.
	// ReadSubClass (FUN_0079f8e0) reads three u32 then keys off the env byte. Body observed as
	// [u32 a=0][u32 value][u32 source-handle][u8 flag]. Grammar from the client reader (docs/re/v1-iir-readers.md),
	// byte-exact.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class FeedbackCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FeedbackIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(FeedbackIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static FeedbackIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FeedbackCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static FeedbackIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "FeedbackIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var fieldA = r.ReadU32Be();
			var value = r.ReadU32Be();
			var source = r.ReadU32Be();
			return new FeedbackIIR(handle, env, fieldA, value, source, r.ReadU8());
		}

		internal static void Write(FaolanStream s, FeedbackIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteByte(v.Env)
				.WriteUInt32(v.FieldA)
				.WriteUInt32(v.Value)
				.WriteUInt32(v.Source)
				.WriteByte(v.Flag);
		}
	}

	// FeedbackIIR (0xBA1FD222).
	public sealed record FeedbackIIR(uint Handle, byte Env, uint FieldA, uint Value, uint Source, byte Flag);
}
