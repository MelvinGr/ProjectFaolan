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
			var body = Faolan.GameServer.Economy.Shop.ShopReplayData.DispatchShopDataBody;
			Assert.Equal("0000c75b00001e7e", Convert.ToHexStringLower(body.AsSpan(0, 8).ToArray()));
		}

		[Fact]
		public void VendorBuyPrice_ExceedsSellPrice_SoWaresCantBeFlipped()
		{
			// A vendor must sell dearer than it buys back, or a ware could be bought and re-sold for profit.
			// Same level signal both ways (sell off Items.Level, buy off the entry TailLevel).
			const byte level = 40;
			const uint count = 1;
			Assert.True(GameServerListener.VendorBuyPriceTin(level, count) > GameServerListener.VendorPriceTin(level, count));
		}

		[Fact]
		public void VendorBuyPrice_ScalesWithCount()
		{
			Assert.Equal(GameServerListener.VendorBuyPriceTin(10, 1) * 3, GameServerListener.VendorBuyPriceTin(10, 3));
		}

		[Fact]
		public void VendorBuyPrice_NeverFree()
		{
			// Level-0 wares (the common case while Items.Level is mostly unmapped) still cost something.
			Assert.True(GameServerListener.VendorBuyPriceTin(0, 1) >= GameServerListener.VendorBuyMarkup);
		}
	}
}
