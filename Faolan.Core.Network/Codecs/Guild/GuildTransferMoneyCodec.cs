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
	// NOT capture-derived, and it cannot be: `--report method-census --report-arg Bank` finds ZERO guild-bank frames
	// across all 120 captures, because no captured session ever opened the bank window. The body is recovered from
	// the client's method table, and the LAYOUT is proven against captured siblings rather than assumed - a
	// GameCharAgent body is its parameters concatenated big-endian at ID32=8, int=4, uint64=8, which reproduces the
	// captured length of every scalar-parameter method on the interface (`GameCharAgentBodyLayoutTests`):
	//
	//     RequestBindLocation(ID32) 8 · SetTradeCash(uint64) 8 · BuyItem(ID32,int) 12 · AddTradeItem(ID32,int,int) 16
	//     SellItem(ID32,ID32,int) 20 · SplitInventoryItem(ID32,int,int,int) 20 · TryLootItem(ID32,int,ID32,int) 24
	//
	// So two 64-bit parameters are a 16-byte body. `Explicit64_c` is the client's explicit-width 64-bit wrapper and
	// occupies the same eight bytes as the `uint64` beside it.
	// [V0: the guild deposit body - recovered from the client, layout corroborated by captured siblings]
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
