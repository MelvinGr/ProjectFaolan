using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// An item put on the trade table: `[u32 entryLen][ProtoInventoryGameEntry][u32 count]` - the same item shape the
	// snapshot / shop / AH grid / live add carry, then the stack count.
	//
	// ONE body, TWO methods: `DispatchTradeAddedItem` (0x2070) goes to the adder and
	// `DispatchTradePartnerAddedItem` (0x206F) to the other party, so only the opcode says whose side of the table the
	// item is on - which is why this declares `HandledMethods` rather than picking one of the two.
	//
	// Split out of `PlayerTradeCodec` in #530 wave 7; byte-gated by TeamTradeFriendCodecTests.
	public static class TradeAddedItemCodec
	{
		public static readonly GameCharInterfaceMethods[] HandledMethods =
			[GameCharInterfaceMethods.DispatchTradeAddedItem, GameCharInterfaceMethods.DispatchTradePartnerAddedItem];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(TradeAddedItem value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static TradeAddedItem Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradeAddedItemCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TradeAddedItem Read(ref SpanReader r)
		{
			return new TradeAddedItem(r.ReadProtobufU32Length<ProtoInventoryGameEntry>(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, TradeAddedItem v)
		{
			s.WriteArrayUInt32Length(v.Entry.ToProtobuf()).WriteUInt32(v.Count);
		}
	}

	// DispatchTradeAddedItem / DispatchTradePartnerAddedItem.
	public sealed record TradeAddedItem(ProtoInventoryGameEntry Entry, uint Count);
}
