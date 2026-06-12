using System;
using Faolan.Core.Data;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte tests for the vendor sell flow against the necrol80 sell_items_to_trader capture.
	public class ShopTests
	{
		[Fact]
		public void ItemSoldBody_Matches_CapturedSlot5Sale()
		{
			// Retail's DispatchItemSoldToShop after SellItem(container 0x68, slot 5): 0000006808006f3100000005.
			var s = new FaolanStream();
			GameServerListener.WriteItemSoldBody(s, 0x68, 0x08006F31, 5);

			Assert.Equal("0000006808006f3100000005", Convert.ToHexStringLower(s.ToArray()));
		}

		[Fact]
		public void ShopReplayBlob_CarriesTheCapturedShopId()
		{
			// The PoC blob must start with the captured shop ID32 (0xC75B:0x1E7E) - guards against the blob
			// being regenerated from the wrong capture/offset.
			var body = Faolan.GameServer.Shop.ShopReplayData.DispatchShopDataBody;
			Assert.Equal("0000c75b00001e7e", Convert.ToHexStringLower(body.AsSpan(0, 8).ToArray()));
		}
	}
}
