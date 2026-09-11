using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     c2s <c>AddTradeItem(const ID32&amp; i_cIventoryID, int i_nItemPos, int i_nDestPos)</c> - which of the sender's
	///     items goes on the table, and where in the trade window it lands:
	///     <c>[u32 container][u32 owner][u32 itemPos][u32 destPos]</c>.
	/// </summary>
	/// <remarks>
	///     The leading ID32 is the source container (104, the backpack, in both captured adds, paired with the sender's
	///     own character id), <c>ItemPos</c> is the bag slot and the last word is <c>DestPos</c>, the slot in the trade
	///     window.
	///     <para>
	///         <c>DestPos</c> is not a count: there is no count in the message at all, since the whole stack moves and the
	///         quantity comes from the inventory row.
	///     </para>
	///     <para>
	///         c2s 0x2068 is AddTradeItem while s2c 0x2068 is DispatchRequestTrade - the same method index on the two
	///         interfaces, which is how per-interface, per-direction method indices work. Do not unify them.
	///     </para>
	///     Byte-gated by <c>TeamTradeFriendCodecTests</c>.
	/// </remarks>
	public static class AddTradeItemCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.AddTradeItem;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The AddTradeItem body.</summary>
	public sealed record AddTradeItem(uint Container, uint Owner, uint ItemPos, uint DestPos);
}
