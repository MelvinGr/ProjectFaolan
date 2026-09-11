using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     c2s <c>GuildWithdrawMoney(uint64 amount)</c> - the guild-bank withdrawal the client sends from the guild
	///     window: a single bare big-endian <c>uint64</c>.
	/// </summary>
	/// <remarks>
	///     Captured in <c>demol80-demonologist_login_gameplay_annotated_3</c>, annotated by the player in /say ("withdrew
	///     50s"): <c>000000000007a120</c> = 500,000 tin = 50 silver.
	///     <para>
	///         There is a second method of this name - <c>GameCharAgent.GuildWithdrawMoney(const ID32&amp; i_cOfficer)</c>
	///         - which is a different message taking an officer id rather than an amount. This is the CS-tier one, which
	///         retail sends from the guild window; the game-tier twin appears in no capture.
	///         [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json fr2458]
	///     </para>
	/// </remarks>
	public static class GuildWithdrawMoneyCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.GuildWithdrawMoney;

		// Canonical: the only method here that writes bytes.
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
