using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// TradepostItemSearch (c2s) - the auction-house search query:
	//   [u16 len][lang ascii][u32 category][u32 page][u32 category2][u32 itemId][filter/price tail]
	//
	// ⚠️ `Category` is NOT echoed back as the search-results `head`, as this once claimed: that head is the protocol's
	// 0x3F1 list count, (rowCount + 1) * 0x3F1 (TradepostListingCodec). The two merely both read 0x7E2 in the
	// captures - a 1-row result is 2 * 0x3F1 = 0x7E2 exactly - which is what made it look echoed. What `Category`
	// MEANS in the request is untested: every captured query carries the same value. [V0]
	//
	// The trailing filter/price region is carried OPAQUELY rather than guessed at: every captured query has the same
	// bytes there, so there is nothing to tell one field from another (#530 §7's treatment, not an exemption - the
	// region round-trips exactly and its gap is visible).
	//
	// Was `TradepostResponses.ParseSearch(FaolanStream)`, which read the five fields and stopped (#530 wave 7).
	public static class TradepostItemSearchCodec
	{
		public const ushort Opcode = (ushort)TradepostAgentOpcodes.ItemSearch;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(TradepostItemSearch value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static TradepostItemSearch Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradepostItemSearchCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TradepostItemSearch Read(ref SpanReader r)
		{
			var lang = r.ReadStringU16Length();
			var category = r.ReadU32Be();
			var page = r.ReadU32Be();
			var category2 = r.ReadU32Be();

			return new TradepostItemSearch(lang, category, page, category2, r.ReadU32Be(), r.ReadRemaining().ToArray());
		}

		internal static void Write(FaolanStream s, TradepostItemSearch v)
		{
			s.WriteString(v.Lang).WriteUInt32(v.Category).WriteUInt32(v.Page).WriteUInt32(v.Category2).WriteUInt32(v.ItemId).WriteArray(v.Filter);
		}
	}

	// TradepostItemSearch. `Category2` mirrors `Category` in every captured query; `Filter` is the undecoded
	// filter/price tail, carried verbatim.
	public sealed record TradepostItemSearch(string Lang, uint Category, uint Page, uint Category2, uint ItemId, byte[] Filter);
}
