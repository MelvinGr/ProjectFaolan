using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     c2s <c>GameCharAgent.GuildTransferMoney(uint64 i_nGuildInstance, Explicit64_c i_cAmount)</c> - a member paying
	///     money into the guild bank.
	/// </summary>
	/// <remarks>
	///     It rides the game tier rather than the CS one, and that asymmetry is the client's own: the wallet lives on the
	///     game server, so the deposit is sent where the money is and the guild is credited across the tier seam. The
	///     withdrawal is the mirror image, <c>CSPlayerAgent.GuildWithdrawMoney</c> on the CS tier, where <c>Guild.Cash</c>
	///     lives.
	///     <para>
	///         Captured in <c>demol80-demonologist_login_gameplay_annotated_3</c>, with the player narrating it in /say
	///         ("just deposited 1g to guild bank"): <c>0000000000086c47 00000000000f4240</c>, i.e. guild 0x86C47 and
	///         1,000,000 tin = 1 gold. The guild instance is the same 0x86C47 the login's <c>GuildHeader</c> carries for
	///         "Faolan", and the amount is the gold the annotation names, so both parameters are pinned rather than just
	///         the length. <c>Explicit64_c</c> is the client's explicit-width 64-bit wrapper and occupies the same eight
	///         bytes as the <c>uint64</c> beside it.
	///         [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json fr2385]
	///     </para>
	/// </remarks>
	public static class GuildTransferMoneyCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.GuildTransferMoney;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(GuildTransferMoney value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt64(value.GuildInstance);
			s.WriteUInt64(value.AmountTin);

			return s.ToArray();
		}

		public static byte[] Build(ulong guildInstance, ulong amountTin)
		{
			return Build(new GuildTransferMoney(guildInstance, amountTin));
		}

		public static GuildTransferMoney Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildTransferMoneyCodec));
			var value = new GuildTransferMoney(r.ReadU64Be(), r.ReadU64Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	/// <remarks>
	///     <c>GuildInstance</c> is the guild the client believes it is paying into. The server does not trust it - the
	///     payer's own <c>Character.GuildId</c> decides where the money goes - but it is parsed rather than skipped,
	///     because a mismatch is the one signal that the client and the server disagree about the player's guild.
	/// </remarks>
	public sealed record GuildTransferMoney(ulong GuildInstance, ulong AmountTin);
}
