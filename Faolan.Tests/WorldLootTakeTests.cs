using System;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Golden tests for the loot-all completion packets, pinned to the hoxxling gameplay capture (bag 0x000021fe,
	// frames 1491 / 4470). Retail sends no inventory-add for normal loot - the client moves the item from the bag to
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
			// Explicitly [0] - a one-element list naming slot 0, which is what this capture sends.
			Assert.Equal("700101620000c87a000021fe0000000000000007e200000000", Convert.ToHexStringLower(LootRemoveItemsCodec.Build(Bag, 0)));
		}

		// Per-drag ack names the looted display Pos in the trailing u32: loot-drag-1538 frame 3266 (bag 0x22ec, dragged
		// the item at Pos 1) sends trailing=1; the emptying drag at Pos 0 (fr3277) sends 0. The 0x000007e2 landmark is
		// constant.
		[Fact]
		public void LootRemoveItems_RemovedPos_MatchesDragCapture()
		{
			Assert.Equal("700101620000c87a000022ec0000000000000007e200000001", Convert.ToHexStringLower(LootRemoveItemsCodec.Build(0x000022ec, 1)));
		}

		// The removed-slot list is a real list, and retail uses it. Measured over all 148 LootRemoveItems records
		// in the corpus the element count takes 0, 1, 2, 3 and 4 - so a loot-all of n items is one message naming all n
		// slots, not n messages, and not a single `[0]`.
		//
		// Both goldens are from NecroL80_mage-necromancer_login_combat: bag 0x8e8 held three items and bag 0x8ea two.
		// The list-frame words are the giveaway that these are counts and not landmarks - 0x0fc4 = 4 * 0x3F1 and
		// 0x0bd3 = 3 * 0x3F1, next to the 0x07e2 (= 2 * 0x3F1) that the single-element captures made look constant.
		[Theory, InlineData(0x000008e8u, 3, "700101620000c87a000008e8000000000000000fc4000000000000000100000002"),
			InlineData(0x000008eau, 2, "700101620000c87a000008ea000000000000000bd30000000000000001")]
		public void LootRemoveItems_MultiSlot_MatchesCapture(uint bag, int slots, string expected)
		{
			var positions = new uint[slots];
			for (var i = 0; i < slots; i++)
				positions[i] = (uint)i;

			Assert.Equal(expected, Convert.ToHexStringLower(LootRemoveItemsCodec.Build(bag, positions)));
		}

		// The empty list is a captured shape too - 50 of the 148 records - and it is the bare 0x3F1 the list-frame
		// notes warn reads as a terminator. Every captured bag gets one alongside its removal message, which matches
		// the corpus's own TryLootCash -> TryLootAll ordering: taking the coins removes no item slot.
		[Fact]
		public void LootRemoveItems_EmptyList_IsTheBare0x3F1()
		{
			Assert.Equal("700101620000c87a000021fe0000000000000003f1", Convert.ToHexStringLower(LootRemoveItemsCodec.Build(Bag)));
		}

		// The cash form: xxx_mage-necromancer_login_play1 frame 3156, bag 0x42e4 - 21 tin, and 21 is exactly
		// what the DispatchUpdateCash beside it moves the wallet by. This is the shape the codec refused, because the
		// value straddled a `[u32 reserved][u8 marker]` split that only reads right while the cash is zero.
		[Fact]
		public void LootRemoveItems_Cash_MatchesCapture()
		{
			var captured = "700101620000c87a000042e40000000015000003f1";

			Assert.Equal(captured, Convert.ToHexStringLower(LootRemoveItemsCodec.Build(new LootRemoveItemsIIR(0x000042e4, [], 21))));

			var parsed = LootRemoveItemsCodec.Parse(Convert.FromHexString(captured));
			Assert.Equal(21u, parsed.Cash);
			Assert.Empty(parsed.RemovedPositions);
		}

		// The two are strict alternatives across all 148 captured records: a cash take names no slot, a slot take
		// carries no cash. Reading one grammar both ways is what makes that checkable at all.
		[Fact]
		public void LootRemoveItems_SlotTake_CarriesNoCash()
		{
			var parsed = LootRemoveItemsCodec.Parse(Convert.FromHexString("700101620000c87a000021fe0000000000000007e200000000"));

			Assert.Equal(0u, parsed.Cash);
			Assert.Equal([0u], parsed.RemovedPositions);
		}

		[Fact]
		public void NewAllowedLooterReset_MatchesCapture()
		{
			Assert.Equal("1e2e01390000c87a000021fe000000000000000000", Convert.ToHexStringLower(NewLooterCodec.Build(NewLooters.Allowed(Bag, 0))));
		}

		[Fact]
		public void NewCurrentLooterReset_MatchesCapture()
		{
			Assert.Equal("d06568c80000c87a000021fe000000000000000000", Convert.ToHexStringLower(NewLooterCodec.Build(NewLooters.Current(Bag, 0))));
		}
	}
}
