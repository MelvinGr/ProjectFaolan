using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	/// <summary>
	///     c2s <c>PlayStockphraseAsSubtitle</c> - the client asking for one cutscene subtitle line:
	///     <c>[ID32 speaker][u16 len][stock key ascii, "{stock_N}"][u32 threadContext]</c>. The server answers with
	///     <c>DispatchReceivedStockPhraseText</c> on the same <c>threadContext</c>.
	/// </summary>
	public static class PlayStockphraseAsSubtitleCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.PlayStockphraseAsSubtitle;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(PlayStockphraseAsSubtitle value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static PlayStockphraseAsSubtitle Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PlayStockphraseAsSubtitleCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static PlayStockphraseAsSubtitle Read(ref SpanReader r)
		{
			return new PlayStockphraseAsSubtitle(r.ReadU32Be(), r.ReadU32Be(), r.ReadStringU16Length(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, PlayStockphraseAsSubtitle v)
		{
			s.WriteUInt32(v.SpeakerClass).WriteUInt32(v.SpeakerInstance).WriteString(v.StockKey).WriteUInt32(v.ThreadContext);
		}
	}

	/// <summary>
	///     The PlayStockphraseAsSubtitle body. <c>StockKey</c> is the <c>{stock_N}</c> token keying the StockPhrases row.
	/// </summary>
	public sealed record PlayStockphraseAsSubtitle(uint SpeakerClass, uint SpeakerInstance, string StockKey, uint ThreadContext);
}
