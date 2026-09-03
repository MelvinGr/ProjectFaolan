using System.Collections.Generic;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Harvests each quest LINE's step order from the quest INDEX blocks of captured snapshots (#926).
	//
	// A block is sent for every quest a character holds, and its pair list is the line's WHOLE catalog -
	// `(questId, sequence)` for every step, completed or not, the same order the completed journal states one step at a
	// time. So a line is ordered by the first capture of a character who merely HELD a quest on it, where the
	// completed journal needs a character who finished it. That covered 47 snapshot-eligible quests whose lines had no
	// order at all, and every one of them sits on a captured block, because "snapshot-eligible" means captured.
	//
	// ⚠️ The sequence is 1-based but NOT strictly monotonic (branch/chapter steps repeat one, #391) - it is carried,
	// never re-derived from the list position. First block wins; a block that DISAGREES with an earlier one is reported,
	// not averaged, because the order is line data and two captures differing would mean it is not.
	public static class LineOrderExtractor
	{
		public sealed record Step(uint QuestLineId, uint Sequence);

		public static (Dictionary<uint, Step> ByQuest, List<string> Disagreements) Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var byQuest = new Dictionary<uint, Step>();
			var disagreements = new List<string>();
			if (packets == null)
				return (byQuest, disagreements);

			foreach (var p in packets)
			{
				if (!SnapshotJournalReader.TryRead(p.Bytes, out _, out _, out var index) || index == null)
					continue;

				foreach (var block in index.Blocks)
				{
					if (block.AreaId == 0)
						continue;

					for (var i = 0; i < block.AreaQuestIds.Count; i++)
					{
						var step = new Step(block.AreaId, block.AreaQuestSequences[i]);
						if (byQuest.TryGetValue(block.AreaQuestIds[i], out var seen))
						{
							if (seen != step)
								disagreements.Add($"quest {block.AreaQuestIds[i]}: index blocks disagree - line {seen.QuestLineId} seq {seen.Sequence} vs line {step.QuestLineId} seq {step.Sequence}");

							continue;
						}

						byQuest[block.AreaQuestIds[i]] = step;
					}
				}
			}

			return (byQuest, disagreements);
		}
	}
}
