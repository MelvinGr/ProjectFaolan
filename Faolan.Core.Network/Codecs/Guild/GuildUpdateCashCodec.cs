using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// s2c 0x2052 DispatchGuildUpdateCash(i_nCash) - the guild bank's new balance, pushed to the members who can see
	// the guild window after a deposit OR a withdrawal (#132).
	//
	// ⭐ CAPTURED, and the balance-vs-delta question is SETTLED by the pair of frames rather than by the field's name
	// (#795). `demol80-demonologist_login_gameplay_annotated_3` carries both halves of one session's guild banking:
	//
	//   login        GuildHeader for "Faolan" (0x86C47) carries cash 0
	//   09:45:33.111 after depositing 1 gold      -> 00000000000f4240   1,000,000
	//   09:45:46.220 after withdrawing 50 silver  -> 000000000007a120     500,000
	//
	// The first value is equally consistent with either reading, because the guild started empty. The SECOND is not:
	// 500,000 is the balance (1,000,000 - 500,000), and a delta would have had to be negative there. Retail sends the
	// positive number the balance reading predicts, so this is the whole balance - which is also what we send.
	//
	// It is sent on BOTH directions of the transaction, not just the withdrawal, and it arrives on the CS tier while
	// the payer's own `DispatchUpdateCash` arrives on the game tier - so their relative order is not fixed (the
	// capture has the deposit's player update first and the withdrawal's second).
	// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json fr2393/2460]
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
