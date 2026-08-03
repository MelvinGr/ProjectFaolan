using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// AddTradeItem (c2s, GameCharAgent 0x2068) - which of the sender's items goes on the table, and where in the trade
	// WINDOW it lands:
	//   [u32 container][u32 owner][u32 itemPos][u32 destPos]
	// The client's own signature is `AddTradeItem(const ID32& i_cIventoryID, int i_nItemPos, int i_nDestPos)`, so the
	// leading ID32 is the source container (104 = the backpack in both captured adds, paired with the sender's own
	// character id), `ItemPos` the bag slot, and the last word `DestPos`, the slot in the trade window.
	//
	// ⚠️ `DestPos` was once read as a COUNT, which happened to work only because the capture's single add carried
	// destPos 1 for a stack of 1. There is no count in the message at all: the whole stack moves, so the quantity
	// comes from the inventory row. A destPos of 0 read as a count would have moved zero of the item.
	//
	// ⚠️ c2s 0x2068 is AddTradeItem while s2c 0x2068 is DispatchRequestTrade - the same method index on the two
	// interfaces, which is exactly how 16-bit method indices work (per interface, per direction). Do not "unify" them.
	//
	// Split out of `PlayerTradeCodec` in #530 wave 7; byte-gated by TeamTradeFriendCodecTests.
	public static class AddTradeItemCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.AddTradeItem;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(AddTradeItem value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static AddTradeItem Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(AddTradeItemCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static AddTradeItem Read(ref SpanReader r)
		{
			return new AddTradeItem(r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, AddTradeItem v)
		{
			s.WriteUInt32(v.Container).WriteUInt32(v.Owner).WriteUInt32(v.ItemPos).WriteUInt32(v.DestPos);
		}
	}

	// AddTradeItem (0x2068).
	public sealed record AddTradeItem(uint Container, uint Owner, uint ItemPos, uint DestPos);
}
