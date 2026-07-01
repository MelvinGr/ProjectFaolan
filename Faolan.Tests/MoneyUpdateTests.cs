using System;
using Faolan.Core;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte test for the 0x2005 wallet-update body against retail (hoxxling-login-gameplay capture):
	// the very first coin loot pushed [u64 balance=2][u32 reason=3] - body hex 000000000000000200000003.
	public class MoneyUpdateTests
	{
		[Fact]
		public void MoneyUpdateBody_Matches_CapturedFirstCoinLoot()
		{
			var s = new FaolanStream();
			GameServerListener.WriteMoneyUpdateBody(s, 2, MoneyUpdateReason.Gain);

			Assert.Equal("000000000000000200000003", Convert.ToHexStringLower(s.ToArray()));
		}

		[Fact]
		public void MoneyUpdateBody_Spend_UsesReasonZero()
		{
			// The demol80 capture shows a DECREASED balance with reason 0 (a purchase): 0x2638 after 0x26E8.
			var s = new FaolanStream();
			GameServerListener.WriteMoneyUpdateBody(s, 0x2638, MoneyUpdateReason.Spend);

			Assert.Equal("000000000000263800000000", Convert.ToHexStringLower(s.ToArray()));
		}
	}
}
