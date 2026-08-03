using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// c2s 0x2035 GuildWithdrawMoney(uint64 amount) - the guild-bank withdrawal the client sends from the guild window.
	//
	// NOT capture-derived, and it cannot be: a sweep of all 215 captures finds ZERO guild-bank traffic, because no
	// captured session ever opened the bank UI (#132). The body is recovered from the client instead - its method
	// table declares the single `uint64 amount` parameter, and every sibling scalar-parameter method on this
	// interface writes its argument as a bare big-endian value with no header, which is what makes a one-parameter
	// method's body its parameter. The width matches `Guild.Cash`, which is also a u64.
	// [V0: the guild withdrawal body]
	public static class GuildWithdrawMoneyCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.GuildWithdrawMoney;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GuildWithdrawMoney value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt64(value.AmountTin);

			return s.ToArray();
		}

		public static byte[] Build(ulong amountTin)
		{
			return Build(new GuildWithdrawMoney(amountTin));
		}

		public static GuildWithdrawMoney Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildWithdrawMoneyCodec));
			var value = new GuildWithdrawMoney(r.ReadU64Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildWithdrawMoney(ulong AmountTin);
}
