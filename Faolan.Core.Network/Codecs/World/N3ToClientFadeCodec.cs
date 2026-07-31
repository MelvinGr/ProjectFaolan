using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// n3ToClientFadeIIR (0x66180B20) - the graceful fade-out despawn (the loot-consumed / loot-expired variant of
	// n3ToClientQuit). 17 B: [subop][typeTag][handle][u8 0 marker][f32 fadeSeconds, big-endian]. Marker 0x00 + a FLOAT
	// distinguishes it from n3ToClientQuit's 0x01 marker + u32 playfield token.
	public static class N3ToClientFadeCodec
	{
		public const float DefaultFadeSeconds = 3.0f;

		public const uint Opcode = (uint)IirTypes.N3ToClientFadeIIR;

		private const byte RecordMarker = 0x00;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(N3ToClientFadeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint typeTag, uint handle, float fadeSeconds = DefaultFadeSeconds)
		{
			return Build(new N3ToClientFadeIIR(typeTag, handle, fadeSeconds));
		}

		public static N3ToClientFadeIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(N3ToClientFadeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static N3ToClientFadeIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "n3ToClientFadeIIR subop");
			var typeTag = r.ReadU32Be();
			var handle = r.ReadU32Be();
			r.ExpectU8(RecordMarker, "record marker");
			return new N3ToClientFadeIIR(typeTag, handle, r.ReadF32Be());
		}

		internal static void Write(FaolanStream s, N3ToClientFadeIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(v.TypeTag).WriteUInt32(v.Handle).WriteByte(RecordMarker).WriteFloat(v.FadeSeconds);
		}
	}

	// n3ToClientFadeIIR (0x66180B20).
	public sealed record N3ToClientFadeIIR(uint TypeTag, uint Handle, float FadeSeconds);
}
