using System;
using Faolan.Core;
using Faolan.GameServer.Economy.Shop;
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
			ShopService.WriteItemSoldBody(s, 0x68, 0x08006F31, 5);

			Assert.Equal("0000006808006f3100000005", Convert.ToHexStringLower(s.ToArray()));
		}

		[Fact]
		public void ShopReplayBlob_CarriesTheCapturedShopId()
		{
			// The PoC blob must start with the captured shop ID32 (0xC75B:0x1E7E) - guards against the blob
			// being regenerated from the wrong capture/offset.
			var body = ShopReplayData.DispatchShopDataBody;
			Assert.Equal("0000c75b00001e7e", Convert.ToHexStringLower(body.AsSpan(0, 8).ToArray()));
		}

		[Fact]
		public void VendorBuyPrice_ExceedsSellPrice_SoWaresCantBeFlipped()
		{
			// A vendor must sell dearer than it buys back, or a ware could be bought and re-sold for profit.
			const uint value = 920;
			const uint count = 1;
			Assert.True(ShopService.VendorBuyPriceTin(value, count) > ShopService.VendorPriceTin(value, count));
		}

		[Fact]
		public void VendorBuyPrice_NeverFree()
		{
			// Even a value-less ware still costs the markup (the shop never gives it away).
			Assert.True(ShopService.VendorBuyPriceTin(0, 1) >= ShopService.VendorBuyMarkup);
		}

		[Fact]
		public void VendorBuyPrice_ScalesWithCount()
		{
			Assert.Equal(ShopService.VendorBuyPriceTin(200, 1) * 3, ShopService.VendorBuyPriceTin(200, 3));
		}

		// The real per-unit sell price = round(Item.Value / 10), proven against every sale in the
		// sell_items_to_trader capture (slot->Item.Value->tin). #137.
		[Theory, InlineData(190u, 19u), InlineData(230u, 23u), InlineData(920u, 92u), InlineData(560u, 56u), InlineData(206u, 21u), InlineData(637u, 64u)]
		// tmpl 3886819
		// tmpl 3887406
		// tmpl 3887364
		// tmpl 3917245 (sold x7 = 392 tin)
		// tmpl 3724525 (round half up: 20.6 -> 21)
		// tmpl 3719048 (63.7 -> 64)
		public void VendorSellPrice_IsRoundedValueOverTen(uint value, uint expectedPerUnit)
		{
			Assert.Equal(expectedPerUnit, ShopService.VendorPriceTin(value, 1));
		}

		[Fact]
		public void VendorSellPrice_ScalesWithCount_AndMatchesTheStackSale()
		{
			// tmpl 3917245: value 560 -> 56/unit, the captured stack of 7 sold for 392 tin.
			Assert.Equal(392u, ShopService.VendorPriceTin(560, 7));
		}

		[Fact]
		public void VendorSellPrice_ZeroValueIsUnsellable()
		{
			// A valueless item (RDB value 0, e.g. the Totem of Origins) yields no price -> the sell handler refuses.
			Assert.Equal(0u, ShopService.VendorPriceTin(0, 1));
			Assert.Equal(0u, ShopService.VendorPriceTin(0, 5));
		}
	}
}
