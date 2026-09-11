using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     DispatchRequestGetEffect(nEffect) - the server asking the client about an effect, which is unusual on this
	///     interface: almost every Dispatch on it tells the client something. The body is <c>[u32 nEffect]</c>.
	/// </summary>
	/// <remarks>
	///     All eight captured frames are byte-identical zero, so the field's width comes from the signature and its meaning
	///     is not observable from traffic: retail asks for effect 0 and nothing else. What it asks for needs the client's own
	///     reader. [V0: what nEffect selects, and what the client answers with]
	/// </remarks>
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
