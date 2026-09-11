using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     An item put on the trade table: <c>[u32 entryLen][ProtoInventoryGameEntry][u32 count]</c> - the same item shape
	///     the snapshot, shop, AH grid and live add carry, then the stack count.
	/// </summary>
	/// <remarks>
	///     One body, two methods: <c>DispatchTradeAddedItem</c> goes to the adder and <c>DispatchTradePartnerAddedItem</c>
	///     to the other party, so only the method says whose side of the table the item is on, which is why this declares
	///     <c>HandledMethods</c> rather than naming one of the two. Byte-gated by <c>TeamTradeFriendCodecTests</c>.
	/// </remarks>
	public static class TradeAddedItemCodec
	{
		public static readonly GameCharInterfaceMethods[] HandledMethods =
			[GameCharInterfaceMethods.DispatchTradeAddedItem, GameCharInterfaceMethods.DispatchTradePartnerAddedItem];

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The DispatchTradeAddedItem / DispatchTradePartnerAddedItem body.</summary>
	public sealed record TradeAddedItem(ProtoInventoryGameEntry Entry, uint Count);
}
