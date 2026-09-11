using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// The recovered stat-list grammar: the FullChar snapshot's stat block is two 0x3F1-framed StatPair_t lists
	// (docs/re/fullplayer-snapshot-reader.md regions 9-10). StatList#1 = 17 displayed stats (Level/XP/MaxXP/HP...),
	// StatList#2 = 148 full-sheet stats. The block that composes them is gated in SubClassPostAccessBlockTests
	// (all 151 captures, byte-exact); this file is the list itself.
	public class StatListCodecTests
	{
		[Fact]
		public void Set_OverridesEveryOccurrence()
		{
			var list = new StatList
			{
				Pairs = [(0x36, 1), (0x1B, 9800), (0x36, 1)]
			};
			list.Set(0x36, 40);
			Assert.Equal(40u, list.Get(0x36));
			Assert.All(list.Pairs.FindAll(p => p.Id == 0x36), p => Assert.Equal(40u, p.Value));
		}

		[Fact]
		public void FreshBlockStatLists_RoundTripByteExact()
		{
			var block = SubClassStatBlock.CreateFreshBlock();

			Assert.Equal(17, block.Displayed.Pairs.Count); // displayed stats
			Assert.Equal(148, block.Sheet.Pairs.Count); // full sheet, 74 pairs twice
			Assert.Equal(0x46F2u, ListFrame.ForCount(block.Displayed.Pairs.Count)); // 18 * 0x3F1
			Assert.Equal(0x24B45u, ListFrame.ForCount(block.Sheet.Pairs.Count)); // 149 * 0x3F1

			// The displayed list carries the character stats we derive.
			Assert.Equal(1u, block.Displayed.Get(0x36)); // Level (fresh skeleton = 1)
			Assert.Equal(0u, block.Displayed.Get(0x34)); // XP
			Assert.Equal(100u, block.Displayed.Get(0x15E)); // MaxXP
			Assert.Equal(9800u, block.Displayed.Get(0x1B)); // current HP (x100)

			foreach (var list in new[] { block.Displayed, block.Sheet })
			{
				var bytes = StatListCodec.Build(list);
				Assert.Equal(bytes, StatListCodec.Build(StatListCodec.Parse(bytes)));
			}
		}
	}
}
