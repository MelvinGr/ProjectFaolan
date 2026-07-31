using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// Proves the recovered stat-block grammar: the FullChar snapshot's stat block is TWO 0x3F1-framed StatPair_t
	// lists (docs/re/fullplayer-snapshot-reader.md regions 9-10), preceded by two empty framed lists and followed by a
	// trailer. StatList#1 = 17 displayed stats (Level/XP/MaxXP/HP...), StatList#2 = 148 full-sheet stats. Parses the
	// actual SubClassStatBlock bytes with StatList and re-serializes them byte-exact.
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
		public void StatBlock_ParsesAsTwoFramedStatLists_AndRoundTripsByteExact()
		{
			var original = SubClassPostAccessBlockCodec.Build(SubClassStatBlock.CreateFreshBlock());

			var r = new SpanReader(original, nameof(StatListCodec));
			var emptyE = r.ReadU32Be(); // two empty framed lists (List E / F)
			var emptyF = r.ReadU32Be();
			var list1 = StatListCodec.Read(ref r);
			var list2 = StatListCodec.Read(ref r);

			Assert.Equal(ListFrame.Empty, emptyE); // 0x3F1 = empty list (0 records)
			Assert.Equal(ListFrame.Empty, emptyF);
			Assert.Equal(17, list1.Pairs.Count); // displayed stats
			Assert.Equal(148, list2.Pairs.Count); // full sheet
			Assert.Equal(0x46F2u, ListFrame.ForCount(list1.Pairs.Count)); // 18 * 0x3F1
			Assert.Equal(0x24B45u, ListFrame.ForCount(list2.Pairs.Count)); // 149 * 0x3F1

			// The displayed list carries the character stats we derive.
			Assert.Equal(1u, list1.Get(0x36)); // Level (fresh skeleton = 1)
			Assert.Equal(0u, list1.Get(0x34)); // XP
			Assert.Equal(100u, list1.Get(0x15E)); // MaxXP
			Assert.Equal(9800u, list1.Get(0x1B)); // current HP (x100)

			// Re-serialize the parsed structure (leading empties + both lists + the remaining trailer) byte-exact.
			using var w = new FaolanStream();
			w.WriteUInt32(emptyE).WriteUInt32(emptyF);
			StatListCodec.Write(w, list1);
			StatListCodec.Write(w, list2);
			w.WriteArray(r.ReadRemaining().ToArray());

			Assert.Equal(original, w.ToArray());
		}
	}
}
