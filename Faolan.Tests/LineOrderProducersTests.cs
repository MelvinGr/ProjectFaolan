using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// A quest line's step order has two wire producers, both filling the client's `MainQuest_c +0x34` pair vector:
	// the login snapshot's quest index block (one per held quest, `FUN_00922210`) and the mid-session
	// MainQuestUpdateIIR Add list (`FUN_00918560`, sent when a quest on the line is granted). LineOrderExtractor
	// harvests both; this is the gate that they are the same data - on every quest the corpus carries in both,
	// the line and the sequence agree - so the second source fills a JournalSequence with the same authority as the
	// first, and a disagreement between them in a future harvest is a finding rather than noise.
	public class LineOrderProducersTests
	{
		private readonly ITestOutputHelper _output;

		public LineOrderProducersTests(ITestOutputHelper output)
		{
			_output = output;
		}

		[Fact]
		public void TheUpdateListAndTheIndexBlock_StateTheSameStepForEveryQuestInBoth()
		{
			// (questId -> (line, sequence)) from each producer, first occurrence wins, as the extractor does.
			var fromBlocks = new Dictionary<uint, (uint Line, uint Seq)>();
			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				if (sub.QuestIndex is not { } index)
					continue;

				foreach (var block in index.Blocks)
				{
					if (block.AreaId == 0)
						continue;

					for (var i = 0; i < block.AreaQuestIds.Count; i++)
						fromBlocks.TryAdd(block.AreaQuestIds[i], (block.AreaId, block.AreaQuestSequences[i]));
				}
			}

			var fromLists = new Dictionary<uint, (uint Line, uint Seq)>();
			var lists = 0;
			foreach (var (_, body) in CorpusExtract.OfType(MainQuestUpdateCodec.Opcode))
			{
				var declared = (int)body.ReadU32Be(0);
				if (declared + 4 > body.Length)
					continue; // truncated capture

				var list = MainQuestUpdateCodec.Parse(body[4..(4 + declared)]);
				if (list.Operation != MainQuestUpdateOp.Add || list.QuestLineId == 0)
					continue;

				lists++;
				foreach (var entry in list.Entries)
				{
					if (entry.QuestId != 0)
						fromLists.TryAdd(entry.QuestId, (list.QuestLineId, entry.Ordinal));
				}
			}

			Assert.True(lists > 0, "no MainQuestUpdate Add list in the corpus slice");
			Assert.True(fromBlocks.Count > 0, "no index block in the corpus slice");

			var inBoth = fromLists.Keys.Intersect(fromBlocks.Keys).ToList();
			var disagree = inBoth.Where(q => fromLists[q] != fromBlocks[q]).Select(q => $"quest {q}: list says line {fromLists[q].Line} seq {fromLists[q].Seq}, block says line {fromBlocks[q].Line} seq {fromBlocks[q].Seq}").ToList();
			var onlyLists = fromLists.Keys.Except(fromBlocks.Keys).Count();

			_output.WriteLine($"{lists} Add lists naming {fromLists.Count} quests; {fromBlocks.Count} quests on index blocks; {inBoth.Count} in both; {onlyLists} only on a list");

			// The claim is only a gate if the two producers actually overlap in the corpus.
			Assert.True(inBoth.Count > 0, "no quest is carried by both producers - the agreement cannot be tested");
			Assert.Empty(disagree);
		}
	}
}
