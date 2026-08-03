using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// s2c 0x2052 DispatchGuildUpdateCash(i_nCash) - the guild bank's new balance, pushed to the members who can see
	// the guild window after a withdrawal (#132).
	//
	// Same evidence footing as its request twin, GuildWithdrawMoneyCodec: no capture exercises the guild bank, so the
	// single field comes from the client's method table and the width from `Guild.Cash`. This is the whole BALANCE,
	// not a delta - `i_nCash` names a total, and the request carries the amount, so a delta here would say the same
	// number twice and leave the client to add it up. [V0: the guild cash update body]
	public static class GuildUpdateCashCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildUpdateCash;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GuildUpdateCash value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt64(value.CashTin);

			return s.ToArray();
		}

		public static byte[] Build(ulong cashTin)
		{
			return Build(new GuildUpdateCash(cashTin));
		}

		public static GuildUpdateCash Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildUpdateCashCodec));
			var value = new GuildUpdateCash(r.ReadU64Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildUpdateCash(ulong CashTin);
}
