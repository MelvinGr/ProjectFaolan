using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Harvests each quest line's step order from the two wire producers of the client's `MainQuest_c`: the
	// quest index blocks of captured login snapshots, and the MainQuestUpdateIIR the server sends when a quest on the
	// line is granted mid-session.
	//
	// A block is sent for every quest a character holds, and its pair list is the line's whole catalog -
	// `(questId, sequence)` for every step, completed or not, the same order the completed journal states one step at a
	// time. So a line is ordered by the first capture of a character who merely held a quest on it, where the
	// completed journal needs a character who finished it. That covered 47 snapshot-eligible quests whose lines had no
	// order at all, and every one of them sits on a captured block, because "snapshot-eligible" means captured.
	//
	// The IIR's Add list is the same vector (`MainQuest_c +0x34`, filled by `FUN_00918560` where the block's reader
	// `FUN_00922210` fills it from the snapshot - the pair is written in the opposite order on the wire, ordinal first,
	// which is why the two codecs name the halves differently). It reaches a line the snapshot does not when the
	// character accepted the quest in a session that never relogged. The two producers agree on every quest the
	// corpus carries in both (LineOrderExtractorTests), so a disagreement between them is reported like one between
	// two blocks.
	//
	// The sequence is 1-based but not strictly monotonic (branch/chapter steps repeat one) - it is carried,
	// never re-derived from the list position. First producer wins; one that DISAGREES with an earlier one is
	// reported, not averaged, because the order is line data and two captures differing would mean it is not.
	public static class LineOrderExtractor
	{
		public sealed record Step(uint QuestLineId, uint Sequence);

		public sealed record Result(Dictionary<uint, Step> ByQuest, List<string> Disagreements, int OnIndexBlocks, int OnlyOnUpdateLists)
		{
			public void Deconstruct(out Dictionary<uint, Step> byQuest, out List<string> disagreements)
			{
				byQuest = ByQuest;
				disagreements = Disagreements;
			}
		}

		public static Result Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var byQuest = new Dictionary<uint, Step>();
			var disagreements = new List<string>();
			var onBlocks = new HashSet<uint>();
			var onLists = new HashSet<uint>();
			if (packets == null)
				return new Result(byQuest, disagreements, 0, 0);

			void Add(uint questId, Step step, string producer, HashSet<uint> seenBy)
			{
				seenBy.Add(questId);
				if (byQuest.TryGetValue(questId, out var seen))
				{
					if (seen != step)
						disagreements.Add($"quest {questId}: {producer} disagrees with an earlier producer - line {seen.QuestLineId} seq {seen.Sequence} vs line {step.QuestLineId} seq {step.Sequence}");

					return;
				}

				byQuest[questId] = step;
			}

			foreach (var p in packets)
			{
				if (SnapshotJournalReader.TryRead(p.Bytes, out _, out _, out var index) && index != null)
				{
					foreach (var block in index.Blocks)
					{
						if (block.AreaId == 0)
							continue;

						for (var i = 0; i < block.AreaQuestIds.Count; i++)
							Add(block.AreaQuestIds[i], new Step(block.AreaId, block.AreaQuestSequences[i]), "an index block", onBlocks);
					}

					continue;
				}

				if (p is not { IsIirCarrier: true, SubOpcode: MainQuestUpdateCodec.Opcode } || !p.TryParse(MainQuestUpdateCodec.Parse, out var list))
					continue;

				// Only an Add carries the catalog; a Remove is the empty list (MainQuestUpdateCodec).
				if (list.Operation != MainQuestUpdateOp.Add || list.QuestLineId == 0)
					continue;

				foreach (var entry in list.Entries)
				{
					if (entry.QuestId != 0)
						Add(entry.QuestId, new Step(list.QuestLineId, entry.Ordinal), "a MainQuestUpdate list", onLists);
				}
			}

			// The list count is what the second producer adds - quests no block carries - not what it merely saw first.
			return new Result(byQuest, disagreements, onBlocks.Count, onLists.Count(q => !onBlocks.Contains(q)));
		}
	}
}
