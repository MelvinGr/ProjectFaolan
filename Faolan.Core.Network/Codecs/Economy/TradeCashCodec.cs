using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// The tin on the trade table: `[u64 tin]`, nothing else.
	//
	// ONE body, two directions: the client's c2s `SetTradeCash` (GameCharAgent 0x2067) and the s2c
	// `DispatchPartnerUpdatedCash` (0x206E) echo to the other party. `Opcode` is the s2c one - the message this codec
	// sends; the c2s twin is another interface's method index and cannot be spelled in the same enum.
	//
	// Split out of `PlayerTradeCodec` (which carried four unrelated messages) in #530 wave 7. PLAYER-to-player trade,
	// #101 - decoded from login_2-clients_group-trade-friend, where both clients are on tape; byte-gated by
	// TeamTradeFriendCodecTests.
	public static class TradeCashCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchPartnerUpdatedCash;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(TradeCash value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static TradeCash Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradeCashCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TradeCash Read(ref SpanReader r)
		{
			return new TradeCash(r.ReadU64Be());
		}

		internal static void Write(FaolanStream s, TradeCash v)
		{
			s.WriteUInt64(v.Tin);
		}
	}

	// SetTradeCash / DispatchPartnerUpdatedCash.
	public sealed record TradeCash(ulong Tin);
}
