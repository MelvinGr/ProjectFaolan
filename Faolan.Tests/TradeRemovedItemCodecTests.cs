using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// The trade remove pair. No capture carries it - the captured session never took an item back off the
	// table - so what these gate is that the recovered shape stays the shape, and that it stays the exact mirror of
	// the captured add pair, which is the whole argument for building it at all.
	public class TradeRemovedItemCodecTests
	{
		// One scalar, big-endian, no header: the client's method entry is `DispatchTradeRemovedItem(i_nItemPos)`, and
		// every sibling scalar-parameter method on this interface writes its argument bare.
		[Theory]
		[InlineData(0u, "00000000")]
		[InlineData(3u, "00000003")]
		[InlineData(0x1234u, "00001234")]
		public void Build_IsTheBareBigEndianWindowSlot(uint slot, string hex)
		{
			Assert.Equal(Convert.FromHexString(hex), TradeRemovedItemCodec.Build(slot));
		}

		[Theory]
		[InlineData(0u)]
		[InlineData(7u)]
		[InlineData(uint.MaxValue)]
		public void RoundTrip(uint slot)
		{
			Assert.Equal(slot, TradeRemovedItemCodec.Parse(TradeRemovedItemCodec.Build(slot)).Slot);
		}

		[Fact]
		public void Parse_RejectsAShortOrOverlongBody()
		{
			Assert.ThrowsAny<Exception>(() => TradeRemovedItemCodec.Parse(new byte[3]));
			Assert.ThrowsAny<Exception>(() => TradeRemovedItemCodec.Parse(new byte[5]));
		}

		// The pair mirrors the captured add pair exactly: partner-facing opcode first, own-facing second, one body for
		// both. If that ever stops holding, the reasoning that let us build this without a capture has stopped holding.
		[Fact]
		public void ThePair_MirrorsTheCapturedAddPair()
		{
			Assert.Equal(
				[GameCharInterfaceMethods.DispatchTradeAddedItem, GameCharInterfaceMethods.DispatchTradePartnerAddedItem],
				TradeAddedItemCodec.HandledMethods);
			Assert.Equal(
				[GameCharInterfaceMethods.DispatchTradeRemovedItem, GameCharInterfaceMethods.DispatchTradePartnerRemovedItem],
				TradeRemovedItemCodec.HandledMethods);

			// ...and the partner-facing member is the lower of each pair, one slot below the own-facing one.
			Assert.Equal(1u, (uint)GameCharInterfaceMethods.DispatchTradeAddedItem - (uint)GameCharInterfaceMethods.DispatchTradePartnerAddedItem);
			Assert.Equal(1u, (uint)GameCharInterfaceMethods.DispatchTradeRemovedItem - (uint)GameCharInterfaceMethods.DispatchTradePartnerRemovedItem);
		}
	}
}
