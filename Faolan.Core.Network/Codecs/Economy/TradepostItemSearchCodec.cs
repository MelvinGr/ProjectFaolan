using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     c2s <c>TradepostItemSearch</c> - the auction-house search query:
	///     <c>[u16 len][lang ascii][u32 category][u32 page][u32 category2][u32 itemId][filter/price tail]</c>.
	/// </summary>
	/// <remarks>
	///     <c>Category</c> is not echoed back as the search results' <c>head</c>: that head is the protocol's list count
	///     (see <see cref="TradepostListingCodec" />). The two merely both read the same value in the captures, a one-row
	///     result framing to exactly twice the frame unit. What <c>Category</c> means in the request is untested - every
	///     captured query carries the same value. [V0]
	///     <para>
	///         The trailing filter/price region is carried opaquely rather than guessed at: every captured query has the
	///         same bytes there, so there is nothing to tell one field from another. The region round-trips exactly and
	///         its gap is visible.
	///     </para>
	/// </remarks>
	public static class TradepostItemSearchCodec
	{
		public const uint Method = (uint)TradepostAgentMethods.ItemSearch;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>
	///     The TradepostItemSearch body. <c>Category2</c> mirrors <c>Category</c> in every captured query, and
	///     <c>Filter</c> is the undecoded filter/price tail, carried verbatim.
	/// </summary>
	public sealed record TradepostItemSearch(string Lang, uint Category, uint Page, uint Category2, uint ItemId, byte[] Filter);
}
