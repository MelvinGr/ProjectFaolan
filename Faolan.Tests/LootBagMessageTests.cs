using System;
using Xunit;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Tests
{
	// Locks the typed LootBagFullUpdateIIR (0xF096251E) contents codec to the captured wire bytes (#187): the
	// trailing ProtoLootBagIIR (cash + looter ids + item inventory) is lifted out of the loot-bag body and rebuilt
	// BYTE-EXACT, and a from-scratch construction (BuildLootBag) reproduces the same bytes when fed the captured
	// cash + inventory. Record captured from the "Chest [N7FU]" loot bag. Reader decoded by vtable-xref
	// (docs/iir-subclass-readers.md); the contents protobuf round-trips losslessly through protobuf-net.
	public class LootBagMessageTests
	{
		// Full loot-bag IIR record (after the 0x2000 carrier prefix):
		// [type=0000C87A][handle][0][transform][u32 len][ProtoSimpleDynelIIR][kd-tree][u32 len][ProtoLootBagIIR].
		private const string RecordHex =
			"0000c87a0000219f000000002c0a0f0d77861444150a772f431d8bcc6c4412140d6f0fc2bc1579e7d0bc1d63d4353c2539d47fbf18dcb0aa0f0000007d08d6c7df01120c4368657374205b4e3746555d1a613c72656d6f7465666f726d61742069643d223133373832222063617465676f72793d22353032303022206b65793d22212c3125565a6323292a63746b2d7a4e464e784922206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e280140d66b60010037e452000f6a190000000d6f626a6563745f636c6f736564000000007f7fffee7f7fffee7f7fffeeff7fffeeff7fffeeff7fffee3f80000000000000000000011808001204080010001a04080010002204080010002a0408001000380042f9010a050873109f43100a180022750800127108b7e5d2e0b1ec8aec201204080010011204081a10011204082e10031a530a140d325b3cee15116db43c1dae637bc5250749266512140d325b3cee15116db43c1dae637bc525074926651a140d325b3cee15116db43c1dae637bc5250749266525d21535002dd21535003801453831464322750801127108b8e5d2e0b1ec8aec201204080010011204081a10011204082e10031a530a140df9ad8ee915cb73ec6e1dbd224735256066dff012140df9ad8ee915cb73ec6e1dbd224735256066dff01a140df9ad8ee915cb73ec6e1dbd224735256066dff025b38939002db38939003801454c374a45";

		[Fact]
		public void BuildContents_RoundTripsByteExact()
		{
			var record = Convert.FromHexString(RecordHex);
			Assert.True(LootBagMessageCodec.TryParse(record, out var loot, out var start));

			var rebuilt = LootBagMessageCodec.BuildContents(loot);
			var captured = record.AsSpan(start).ToArray(); // [u32 len][ProtoLootBagIIR] to end of record
			Assert.Equal(Convert.ToHexString(captured), Convert.ToHexString(rebuilt));
		}

		[Fact]
		public void BuildLootBag_FromCashAndInventory_ReproducesContents()
		{
			// Constructing the contents from the captured cash + inventory reproduces the captured blob byte-exact -
			// the from-scratch path a live drop would take (cash + dropped items), proven against retail bytes.
			var record = Convert.FromHexString(RecordHex);
			Assert.True(LootBagMessageCodec.TryParse(record, out var loot, out var start));

			var constructed = LootBagMessageCodec.BuildContents(LootBagMessageCodec.BuildLootBag(loot.Cash, loot.Inventory));
			var captured = record.AsSpan(start).ToArray();
			Assert.Equal(Convert.ToHexString(captured), Convert.ToHexString(constructed));
		}

		[Fact]
		public void Parse_ReadsTypedContents()
		{
			var record = Convert.FromHexString(RecordHex);

			Assert.True(LootBagMessageCodec.TryParse(record, out var loot, out var start));
			Assert.Equal(0u, loot.Cash);
			Assert.NotNull(loot.Inventory);
			Assert.Equal(2, loot.Inventory.Items.Count); // the chest holds two items
			Assert.NotNull(loot.OwnerId); // empty {0,0} owner is present on captured bags
			Assert.Equal(0u, loot.OwnerId.Instance);

			// The contents are the final length-prefixed blob; everything before `start` is identity + kd-tree.
			Assert.True(start > 0 && start + 4 < record.Length);
		}
	}
}
