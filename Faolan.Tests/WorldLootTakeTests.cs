using System;
using Faolan.GameServer.World;
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
		public void LootRemoveItems_MatchesCapture()
		{
			Assert.Equal(
				"700101620000c87a000021fe0000000000000007e200000000",
				Convert.ToHexStringLower(WorldLoot.BuildLootRemoveItemsBody(Bag)));
		}

		[Fact]
		public void NewCurrentLooterReset_MatchesCapture()
		{
			Assert.Equal(
				"d06568c80000c87a000021fe000000000000000000",
				Convert.ToHexStringLower(WorldLoot.BuildLooterResetBody(WorldLoot.NewCurrentLooterSubOpcode, Bag)));
		}

		[Fact]
		public void NewAllowedLooterReset_MatchesCapture()
		{
			Assert.Equal(
				"1e2e01390000c87a000021fe000000000000000000",
				Convert.ToHexStringLower(WorldLoot.BuildLooterResetBody(WorldLoot.NewAllowedLooterSubOpcode, Bag)));
		}

		[Fact]
		public void BagConsumedVisual_MatchesCapture()
		{
			Assert.Equal(
				"305364110000c87a000021fe000000000000000001000101",
				Convert.ToHexStringLower(WorldLoot.BuildBagConsumedVisualBody(Bag)));
		}
	}
}
