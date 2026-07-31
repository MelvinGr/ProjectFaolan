using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// PlayStockphraseAsSubtitle (c2s, GameCharAgent 0x205E) - the client asking for one cutscene subtitle line:
	//   [ID32 speaker: u32 class, u32 instance][u16 len][stock key ascii, "{stock_N}"][u32 threadContext]
	// It answers with DispatchReceivedStockPhraseText on the same `threadContext`.
	//
	// Moved in from `StockConversationService.TryParsePlayStockphraseRequest` in #530 wave 7. That reader indexed the
	// body at 8/10/10+len and returned false on a short one; reading it sequentially is what makes a body that merely
	// has the right byte at 8 fail (rule 3), and the caller now reports a CodecParseException rather than "could not
	// parse" with no detail.
	public static class PlayStockphraseAsSubtitleCodec
	{
		public const ushort Opcode = (ushort)GameCharAgentOpcodes.PlayStockphraseAsSubtitle;

		// CANONICAL - the only method here that writes bytes.
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

	// PlayStockphraseAsSubtitle (0x205E). `StockKey` is the `{stock_N}` token keying the StockPhrases row.
	public sealed record PlayStockphraseAsSubtitle(uint SpeakerClass, uint SpeakerInstance, string StockKey, uint ThreadContext);
}
