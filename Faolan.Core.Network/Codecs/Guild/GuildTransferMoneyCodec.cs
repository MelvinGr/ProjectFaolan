using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// c2s `GameCharAgent.GuildTransferMoney(uint64 i_nGuildInstance, Explicit64_c i_cAmount)` - a member paying money
	// INTO the guild bank (#654). The withdrawal's missing counterpart: `Guild.Cash` could only ever go down.
	//
	// ⚠️ It rides the GAME tier, not the CS one, and that asymmetry is the client's own: the WALLET lives on the game
	// server, so the deposit is sent where the money is and the guild is credited across the tier seam. The
	// withdrawal is the mirror image - `CSPlayerAgent.GuildWithdrawMoney` on the CS tier, where `Guild.Cash` lives.
	//
	// ⭐ CAPTURED, and the body is exactly what the client's method table predicted (#795). It was written from the
	// method table alone, under the belief - stated here and in `gaps.md` - that no captured session ever opened the
	// bank window. One does: `demol80-demonologist_login_gameplay_annotated_3`, with the player narrating it in /say
	// ("just deposited 1g to guild bank"). The sweep that reported zero guild-bank frames predates that capture.
	//
	//   09:45:32.917 c2s  0000000000086c47 00000000000f4240
	//                     guild 0x86C47     1,000,000 tin = 1 gold
	//
	// The guild instance is the same 0x86C47 the login's `GuildHeader` carries for "Faolan", and the amount is the
	// gold the annotation names - so both parameters are pinned, not just the length. `Explicit64_c` is the client's
	// explicit-width 64-bit wrapper and does occupy the same eight bytes as the `uint64` beside it.
	// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json fr2385]
	public static class GuildTransferMoneyCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.GuildTransferMoney;

		// CANONICAL - the only method here that writes bytes.
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

	// `GuildInstance` is the guild the client believes it is paying into. The server does NOT trust it - the payer's
	// own `Character.GuildId` decides where the money goes - but it is parsed rather than skipped, because a
	// mismatch is the one signal that the client and the server disagree about the player's guild.
	public sealed record GuildTransferMoney(ulong GuildInstance, ulong AmountTin);
}
