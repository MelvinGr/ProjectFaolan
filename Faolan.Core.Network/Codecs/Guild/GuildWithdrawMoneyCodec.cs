using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// c2s 0x2035 GuildWithdrawMoney(uint64 amount) - the guild-bank withdrawal the client sends from the guild window.
	//
	// ⭐ CAPTURED (#795), and it is the single bare big-endian `uint64` the client's method table predicted. This
	// used to say a sweep of all 215 captures found ZERO guild-bank traffic; `demol80-demonologist_login_gameplay_
	// annotated_3` has the whole transaction, annotated by the player in /say ("withdrew 50s"):
	//
	//   09:45:46.051 c2s  000000000007a120   = 500,000 tin = 50 silver
	//
	// ⚠️ There is a SECOND method of this name - `GameCharAgent.GuildWithdrawMoney(const ID32& i_cOfficer)` (0x12) -
	// and it is a different message taking an officer id, not an amount. This is the CS-tier one, which is the one
	// retail sends from the guild window; the game-tier twin appears in no capture.
	// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json fr2458]
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
