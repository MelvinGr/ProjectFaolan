using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Golden tests for the loot-all completion packets, pinned to the hoxxling gameplay capture (bag 0x000021fe,
	// frames 1491 / 4470). Retail sends NO inventory-add for normal loot - the client moves the item from the bag to
	// the backpack locally on LootRemoveItems; the server just signals removal + closes + fades the emptied bag.
	public class WorldLootTakeTests
	{
		private const uint Bag = 0x000021fe;

		[Fact]
		public void BagConsumedVisual_MatchesCapture()
		{
			Assert.Equal("305364110000c87a000021fe000000000000000001000101", Convert.ToHexStringLower(UpdateSimpleDynelCodec.Build(Bag)));
		}

		[Fact]
		public void LootRemoveItems_MatchesCapture()
		{
			Assert.Equal("700101620000c87a000021fe0000000000000007e200000000", Convert.ToHexStringLower(LootRemoveItemsCodec.Build(Bag)));
		}

		// Per-drag ACK names the looted display Pos in the trailing u32: loot-drag-1538 frame 3266 (bag 0x22ec, dragged
		// the item at Pos 1) sends trailing=1; the emptying drag at Pos 0 (fr3277) sends 0. The 0x000007e2 landmark is
		// constant.
		[Fact]
		public void LootRemoveItems_RemovedPos_MatchesDragCapture()
		{
			Assert.Equal("700101620000c87a000022ec0000000000000007e200000001", Convert.ToHexStringLower(LootRemoveItemsCodec.Build(0x000022ec, 1)));
		}

		[Fact]
		public void NewAllowedLooterReset_MatchesCapture()
		{
			Assert.Equal("1e2e01390000c87a000021fe000000000000000000", Convert.ToHexStringLower(NewLooterCodec.Build(GameServer0X2000Opcodes.NewAllowedLooterIIR, Bag, 0)));
		}

		[Fact]
		public void NewCurrentLooterReset_MatchesCapture()
		{
			Assert.Equal("d06568c80000c87a000021fe000000000000000000", Convert.ToHexStringLower(NewLooterCodec.Build(GameServer0X2000Opcodes.NewCurrentLooterIIR, Bag, 0)));
		}
	}
}
