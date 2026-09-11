using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	/// <summary>
	///     <c>DispatchReceivedStockPhraseText</c> - the create-world cutscene's subtitle reply: the remoteformat line the
	///     client renders for one <c>{stock_N}</c> token, on the script thread that asked for it, as
	///     <c>[u32 threadContext][u16 textLen][remoteformat text]</c>.
	/// </summary>
	/// <remarks>
	///     The client blocks up to 60 s per line waiting for this, which is why the cutscene stalls without it. The text
	///     is the same <c>remoteformat</c> payload <c>RemoteFormatScreenCodec</c> builds, carried here as a string
	///     because that is what the layer above holds; the wire encoding is Latin1.
	/// </remarks>
	public static class ReceivedStockPhraseTextCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchReceivedStockPhraseText;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(ReceivedStockPhraseText value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static ReceivedStockPhraseText Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ReceivedStockPhraseTextCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ReceivedStockPhraseText Read(ref SpanReader r)
		{
			return new ReceivedStockPhraseText(r.ReadU32Be(), r.ReadStringU16Length());
		}

		internal static void Write(FaolanStream s, ReceivedStockPhraseText v)
		{
			s.WriteUInt32(v.ThreadContext).WriteString(v.Text);
		}
	}

	/// <summary>The DispatchReceivedStockPhraseText body.</summary>
	public sealed record ReceivedStockPhraseText(uint ThreadContext, string Text);
}
