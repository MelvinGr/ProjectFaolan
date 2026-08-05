using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// An item taken back off the trade table: `[u32 i_nItemPos]` - the trade-WINDOW slot, the same number the c2s
	// `RemoveTradeItem` (0x2069) addresses.
	//
	// ONE body, TWO methods, exactly like its `TradeAddedItemCodec` twin: `DispatchTradeRemovedItem` (0x2074) goes to
	// the remover and `DispatchTradePartnerRemovedItem` (0x2073) to the other party, so only the opcode says whose side
	// of the table the item left. Hence `HandledMethods` rather than picking one of the two.
	//
	// ⚠️ NOT capture-derived, and the reason it can be built anyway is worth stating (#427). The ticket recorded this
	// pair as un-buildable because "the client's s2c numbering is offset against the capture and the offset is not
	// uniform". That was true of the old string-catalog reading and is not true of the enum any more: since #615 a
	// `*Methods` member IS the wire field, recovered from the per-class dispatcher SWITCH, and the reconciled table
	// puts `DispatchRequestTrade` at 0x68 - which is exactly where the capture demonstrably shows it. The same table
	// gives the body: the client's method entry is `DispatchTradeRemovedItem(i_nItemPos)`, a single scalar, and every
	// sibling scalar-parameter method on this interface writes its argument as a bare big-endian word with no header
	// (the rule that recovered the guild-bank bodies in #132).
	// [V1: docs/re/aoc-client-rpc-methods.json]
	public static class TradeRemovedItemCodec
	{
		public static readonly GameCharInterfaceMethods[] HandledMethods =
			[GameCharInterfaceMethods.DispatchTradeRemovedItem, GameCharInterfaceMethods.DispatchTradePartnerRemovedItem];

		// CANONICAL - the only method here that writes bytes.
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

	// The trade-window slot an item was taken off (`i_nItemPos`).
	public sealed record TradeRemovedItem(uint Slot);
}
