using System;
using Faolan.Core.Data;
using Xunit;

namespace Faolan.Tests
{
	// Locks the TradepostSellItem (C->S 0x200A) request body grammar against the necrol80 sell_item_on_ah capture
	// (F375): [nBankItemSlot:u32][nPrice:u64]. The player listed bank slot 6 for 1,000,000 tin. The handler
	// (GameServerListener.HandleTradepostSell) reads exactly these two fields off the body.
	public class TradepostSellBodyTests
	{
		[Fact]
		public void SellItemBody_DecodesSlotAndPrice()
		{
			var body = Convert.FromHexString("0000000600000000000f4240");
			var s = new FaolanStream(body);

			var bankSlot = s.ReadUInt32();
			var price = s.ReadUInt64();

			Assert.Equal(6u, bankSlot);
			Assert.Equal(1_000_000ul, price);
			Assert.Equal(body.Length, (int)s.Position); // the whole 12-byte body is consumed
		}
	}
}
