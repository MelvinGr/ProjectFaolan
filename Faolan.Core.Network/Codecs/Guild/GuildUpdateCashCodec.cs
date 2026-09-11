using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     s2c <c>DispatchGuildUpdateCash(i_nCash)</c> - the guild bank's new balance, pushed to the members who can see
	///     the guild window after a deposit or a withdrawal.
	/// </summary>
	/// <remarks>
	///     The field is the whole balance, not a delta, and the capture settles it:
	///     <c>demol80-demonologist_login_gameplay_annotated_3</c> carries both halves of one session's guild banking - the
	///     login's GuildHeader for "Faolan" (0x86C47) carries cash 0, depositing 1 gold gives 1,000,000, and withdrawing
	///     50 silver then gives 500,000. The first value fits either reading because the guild started empty; the second
	///     does not, since a delta would have had to be negative.
	///     <para>
	///         It is sent on both directions of the transaction, not just the withdrawal, and it arrives on the CS tier
	///         while the payer's own <c>DispatchUpdateCash</c> arrives on the game tier, so their relative order is not
	///         fixed. [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json fr2393/2460]
	///     </para>
	/// </remarks>
	public static class GuildUpdateCashCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildUpdateCash;

		// Canonical: the only method here that writes bytes.
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
