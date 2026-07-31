using System;
using Faolan.Core;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// Locks the TradepostSellItem (C->S 0x200A) request body grammar against the NecroL80 sell_item_on_ah capture
	// (F375): [nBankItemSlot:u32][nPrice:u64]. The player listed bank slot 6 for 1,000,000 tin. The handler
	// (GameServerListener.HandleTradepostSell) reads exactly these two fields off the body.
	public class TradepostSellBodyTests
	{
		// The "listed" overlay rides on 0x201c bank-slot stat updates: [kind=105][charInst][slot][statId][value].
		// Byte-exact vs the sell capture's F376 (slot 6, stat 258=price=1,000,000, char instance 0x0802e5d4).
		[Fact]
		public void BankSlotStatBody_MatchesCapturedOverlayUpdate()
		{
			var body = SetItemStatCodec.Build(SetItemStats.BankSlot(0x0802e5d4, 6, 258, 1_000_000));

			Assert.Equal("000000690802e5d40000000600000102000f4240", Convert.ToHexStringLower(body));
		}

		// The per-slot REGISTRATION the bank sends before its content is the SAME message with stat 0x17 and the
		// record id packed into the value - it had its own writer until #530 wave 7 (see SetItemStatCodec).
		[Fact]
		public void BankSlotRegistration_IsTheSameMessageWithStat0x17()
		{
			var registration = SetItemStats.SlotRegistration(0x0802e5d4, 3, 0x0A18);

			Assert.Equal("000000690802e5d40000000300000017000f0a18", Convert.ToHexStringLower(SetItemStatCodec.Build(registration)));
			Assert.Equal(registration, SetItemStatCodec.Parse(SetItemStatCodec.Build(registration)));
		}

		// CancelMail (0x2009), capture F403: a u64 mail id.
		[Fact]
		public void CancelMailBody_DecodesMailId()
		{
			var body = Convert.FromHexString("20d82b5b17d2e9d4");
			var s = new FaolanStream(body);

			Assert.Equal(0x20d82b5b17d2e9d4ul, s.ReadUInt64());
			Assert.Equal(body.Length, (int)s.Position);
		}

		// TradepostCancelSellItem (0x200C), capture F397: just the origin bank slot.
		[Fact]
		public void CancelSellBody_DecodesSlot()
		{
			var body = Convert.FromHexString("00000006");
			var s = new FaolanStream(body);

			Assert.Equal(6u, s.ReadUInt32());
			Assert.Equal(body.Length, (int)s.Position);
		}

		// TradepostChangeItemPrice (0x200B), capture F363: same [slot:u32][price:u64] shape as SellItem.
		[Fact]
		public void ChangePriceBody_DecodesSlotAndNewPrice()
		{
			var body = Convert.FromHexString("0000000600000000004c4b40");
			var s = new FaolanStream(body);

			Assert.Equal(6u, s.ReadUInt32());
			Assert.Equal(5_000_000ul, s.ReadUInt64());
			Assert.Equal(body.Length, (int)s.Position);
		}

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
