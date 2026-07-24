using System;
using Faolan.Core;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte test for the 0x2005 wallet-update body against retail (hoxxling-login-gameplay capture):
	// the very first coin loot pushed [u64 balance=2][u32 reason=3] - body hex 000000000000000200000003.
	public class UpdateCashTests
	{
		[Fact]
		public void UpdateCashBody_Matches_CapturedFirstCoinLoot()
		{
			var s = new FaolanStream();
			UpdateCashBody.WriteUpdateCashBody(s, 2, UpdateCashReason.Gain);

			Assert.Equal("000000000000000200000003", Convert.ToHexStringLower(s.ToArray()));
		}

		[Fact]
		public void UpdateCashBody_Spend_UsesReasonZero()
		{
			// The DemoL80 capture shows a DECREASED balance with reason 0 (a purchase): 0x2638 after 0x26E8.
			var s = new FaolanStream();
			UpdateCashBody.WriteUpdateCashBody(s, 0x2638, UpdateCashReason.Spend);

			Assert.Equal("000000000000263800000000", Convert.ToHexStringLower(s.ToArray()));
		}
	}
}
