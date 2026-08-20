using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// DispatchRequestGetEffect(nEffect) - GameCharInterface 0xB3. The server ASKING the client about an effect, which
	// is the unusual thing about it: almost every Dispatch on this interface tells the client something (#874).
	//
	//   [u32 nEffect]
	//
	// ⚠️ **All eight captured frames are byte-identical zero.** So the field's WIDTH is the signature's and its
	// meaning is not observable: the corpus shows retail asking for effect 0 and nothing else, which is exactly the
	// case a value census cannot decide. What it is asking FOR needs the client's own reader.
	// `[V0: what nEffect selects, and what the client answers with]`
	public static class RequestGetEffectCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchRequestGetEffect;

		public static byte[] Build(RequestGetEffect value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint effect)
		{
			return Build(new RequestGetEffect(effect));
		}

		public static RequestGetEffect Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RequestGetEffectCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static RequestGetEffect Read(ref SpanReader r)
		{
			return new RequestGetEffect(r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, RequestGetEffect v)
		{
			s.WriteUInt32(v.Effect);
		}
	}

	public sealed record RequestGetEffect(uint Effect);
}
