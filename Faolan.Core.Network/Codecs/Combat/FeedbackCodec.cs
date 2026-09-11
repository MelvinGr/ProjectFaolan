using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     FeedbackIIR - a plain feedback record, distinct from the FormatFeedback FIFO banner. The client reader
	///     (FUN_0079f8e0) reads three u32 then keys off the env byte; the body is observed as
	///     <c>[u32 a=0][u32 value][u32 source-handle][u8 flag]</c>. Grammar from that reader; see
	///     docs/re/v1-iir-readers.md.
	/// </summary>
	/// <remarks>The u32 carrier length belongs to the framing sender, not to this body.</remarks>
	public static class FeedbackCodec
	{
		public const uint Opcode = (uint)IirTypes.FeedbackIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The FeedbackIIR body.</summary>
	public sealed record FeedbackIIR(uint Handle, byte Env, uint FieldA, uint Value, uint Source, byte Flag);
}
