using System;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte test for the 0x2005 wallet-update body against retail (hoxxling-login-gameplay capture):
	// the very first coin loot pushed [u64 balance=2][u32 reason=3] - body hex 000000000000000200000003.
	public class UpdateCashTests
	{
		[Fact]
		public void UpdateCash_Matches_CapturedFirstCoinLoot()
		{
			var body = UpdateCashCodec.Build(new UpdateCash(2, UpdateCashReason.Gain));

			Assert.Equal("000000000000000200000003", Convert.ToHexStringLower(body));
			Assert.Equal(new UpdateCash(2, UpdateCashReason.Gain), UpdateCashCodec.Parse(body));
		}

		[Fact]
		public void UpdateCash_Spend_UsesReasonZero()
		{
			// The DemoL80 capture shows a decreased balance with reason 0 (a purchase): 0x2638 after 0x26E8.
			Assert.Equal("000000000000263800000000", Convert.ToHexStringLower(UpdateCashCodec.Build(new UpdateCash(0x2638, UpdateCashReason.Spend))));
		}
	}
}
