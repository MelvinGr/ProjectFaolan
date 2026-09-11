using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     An item taken back off the trade table: <c>[u32 i_nItemPos]</c> - the trade-window slot, the same number the
	///     c2s <c>RemoveTradeItem</c> addresses.
	/// </summary>
	/// <remarks>
	///     One body, two methods, exactly like its <see cref="TradeAddedItemCodec" /> twin:
	///     <c>DispatchTradeRemovedItem</c> goes to the remover and <c>DispatchTradePartnerRemovedItem</c> to the other
	///     party, so only the method says whose side of the table the item left. Hence <c>HandledMethods</c> rather than
	///     naming one of the two.
	///     <para>
	///         Not capture-derived. A <c>*Methods</c> member is the wire field, recovered from the per-class dispatcher
	///         switch, and the reconciled table puts <c>DispatchRequestTrade</c> at 0x68, exactly where the capture shows
	///         it. The same table gives the body: the client's method entry is
	///         <c>DispatchTradeRemovedItem(i_nItemPos)</c>, a single scalar, and every sibling scalar-parameter method on
	///         this interface writes its argument as a bare big-endian word with no header.
	///         [V1: docs/re/aoc-client-rpc-methods.json]
	///     </para>
	/// </remarks>
	public static class TradeRemovedItemCodec
	{
		public static readonly GameCharInterfaceMethods[] HandledMethods =
			[GameCharInterfaceMethods.DispatchTradeRemovedItem, GameCharInterfaceMethods.DispatchTradePartnerRemovedItem];

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(TradeRemovedItem value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint slot)
		{
			return Build(new TradeRemovedItem(slot));
		}

		public static TradeRemovedItem Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradeRemovedItemCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TradeRemovedItem Read(ref SpanReader r)
		{
			return new TradeRemovedItem(r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, TradeRemovedItem v)
		{
			s.WriteUInt32(v.Slot);
		}
	}

	/// <summary>The trade-window slot an item was taken off (<c>i_nItemPos</c>).</summary>
	public sealed record TradeRemovedItem(uint Slot);
}
