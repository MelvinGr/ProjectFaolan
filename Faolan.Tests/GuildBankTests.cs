using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Protocol;
using Xunit;

namespace Faolan.Tests
{
	// #654: the guild bank's DEPOSIT half. `Guild.Cash` could only ever go down - `GuildWithdrawMoney` paid out and
	// nothing paid in - and the code said so ("there is no DEPOSIT counterpart; guild money is fed by the guild
	// TAX"). The client's own method table says otherwise: `GameCharAgent.GuildTransferMoney(uint64
	// i_nGuildInstance, Explicit64_c i_cAmount)`.
	//
	// ⚠️ NONE of it is captured. `--report method-census --report-arg Bank` finds ZERO guild-bank frames across all
	// 120 captures, because no captured session ever opened the bank window. So the body is recovered from the
	// client, and what makes that more than a guess is the LAYOUT RULE below, which is measurable.
	public class GuildBankTests
	{
		// The rule the deposit body rests on: a GameCharAgent request body is its declared parameters concatenated,
		// big-endian, with no header - ID32 = 8 bytes, int = 4, uint64 = 8.
		//
		// These are the CAPTURED average body lengths from `--report method-census`, against the client's declared
		// signatures. Nine methods, four distinct parameter counts, three type widths, and every one lands exactly -
		// which is what licenses reading a two-64-bit-parameter method as a 16-byte body.
		// (RequestBindLocation, KickFromTeam and TryLootCash are all `(ID32)` at 8 bytes; listed once, since xUnit
		// rejects duplicate InlineData - the three independent confirmations are the point, not three rows.)
		[Theory]
		[InlineData("RequestBindLocation/KickFromTeam/TryLootCash(ID32)", 8, 1, 0, 0)]
		[InlineData("SetTradeCash(uint64)", 8, 0, 0, 1)]
		[InlineData("BuyItem(ID32,int)", 12, 1, 1, 0)]
		[InlineData("AddTradeItem(ID32,int,int)", 16, 1, 2, 0)]
		[InlineData("SellItem(ID32,ID32,int)", 20, 2, 1, 0)]
		[InlineData("SplitInventoryItem(ID32,int,int,int)", 20, 1, 3, 0)]
		[InlineData("TryLootItem(ID32,int,ID32,int)", 24, 2, 2, 0)]
		public void ACapturedGameCharAgentBody_IsItsParametersConcatenated(string method, int capturedLength, int id32s, int ints, int uint64s)
		{
			Assert.Equal(capturedLength, id32s * 8 + ints * 4 + uint64s * 8);
			Assert.NotEmpty(method); // named so a failure says WHICH captured method disagrees
		}

		// So `GuildTransferMoney(uint64, Explicit64_c)` is sixteen bytes: two 64-bit values, guild first.
		[Fact]
		public void TheDepositBody_IsTwoBigEndian64BitValues()
		{
			var body = GuildTransferMoneyCodec.Build(0x0102030405060708UL, 12345UL);

			Assert.Equal(16, body.Length);
			Assert.Equal("01020304050607080000000000003039", System.Convert.ToHexStringLower(body));

			var parsed = GuildTransferMoneyCodec.Parse(body);
			Assert.Equal(0x0102030405060708UL, parsed.GuildInstance);
			Assert.Equal(12345UL, parsed.AmountTin);
			Assert.Equal(body, GuildTransferMoneyCodec.Build(parsed));
		}

		// The refusal is retail's own named message, not a chat line and not silence - the one request in this
		// subsystem the client can legitimately send while being wrong, because it does not know the wallet moved.
		[Fact]
		public void TheRefusal_CarriesTheAmountThatWasShort()
		{
			var body = GuildNotEnoughCashCodec.Build(9_000UL);

			Assert.Equal(8, body.Length);
			Assert.Equal(9_000UL, GuildNotEnoughCashCodec.Parse(body).AmountTin);
			Assert.Equal(body, GuildNotEnoughCashCodec.Build(GuildNotEnoughCashCodec.Parse(body)));
		}

		// The two halves of the bank are on DIFFERENT tiers, and that asymmetry is the client's own: the deposit goes
		// where the WALLET is (the game server) and the withdrawal where `Guild.Cash` is (the CS tier). Pinning it
		// here because "both guild money methods are on the guild tier" is the obvious wrong assumption, and it would
		// silently address the deposit to a listener that never receives it.
		[Fact]
		public void TheTwoHalvesOfTheBank_AreOnDifferentTiers()
		{
			Assert.Equal((uint)GameCharAgentMethods.GuildTransferMoney, GuildTransferMoneyCodec.Method);
			Assert.Equal((uint)CSPlayerAgentMethods.GuildWithdrawMoney, GuildWithdrawMoneyCodec.Method);

			// And the wire renderings collide across the two interfaces (0x2011 vs 0x2035 here, but the principle is
			// what matters): a method number is only meaningful WITH its interface.
			Assert.Equal(0x2011, WireOpcode.Of(GameCharAgentMethods.GuildTransferMoney));
			Assert.Equal(0x2035, WireOpcode.Of(CSPlayerAgentMethods.GuildWithdrawMoney));
		}
	}
}
