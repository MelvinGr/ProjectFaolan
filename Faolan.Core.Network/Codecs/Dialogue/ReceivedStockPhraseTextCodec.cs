using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// DispatchReceivedStockPhraseText (GameCharInterface 0x205D) - the create-world cutscene's SUBTITLE reply: the
	// remoteformat line the client renders for one `{stock_N}` token, on the script thread that asked for it.
	//   [u32 threadContext][u16 textLen][remoteformat text]
	// The client blocks up to 60s per line waiting for this, which is why the cutscene stalls without it (#234).
	//
	// The text is the same `<remoteformat>` payload RemoteFormatScreenCodec builds, carried here as a STRING because
	// that is what the layer above holds; the wire encoding is Latin1, as everywhere else (`WriteString`).
	//
	// Moved in from `StockConversationService.BuildReceivedStockPhraseBody` in #530 wave 7 - the service keeps the
	// StockPhrases lookup and the send.
	public static class ReceivedStockPhraseTextCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchReceivedStockPhraseText;

		// CANONICAL - the only method here that writes bytes.
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

	// DispatchReceivedStockPhraseText (0x205D).
	public sealed record ReceivedStockPhraseText(uint ThreadContext, string Text);
}
