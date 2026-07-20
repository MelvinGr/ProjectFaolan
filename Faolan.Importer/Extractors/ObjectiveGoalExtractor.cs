using System.Collections.Generic;
using System.Text;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Harvests the @609 OBJECTIVE GOALS of each active journal quest out of captured snapshots. QuestObjectiveRegion
	// builds a quest's objective region from QuestGoalDefinition rows, so without these a quest cannot ride the login
	// snapshot at all (#389) - and with the live restore path gone, cannot appear in the journal at all.
	//
	// Every one of the 1686 Quest_c records in the corpus carries its goals, so this is pure harvest, no inference.
	public static class ObjectiveGoalExtractor
	{
		public sealed record Goal(
			uint QuestId,
			uint GoalId,
			uint Sequence,
			uint Separator,
			uint SubId,
			uint MapId,
			uint MarkerX,
			uint MarkerY,
			uint Extra5,
			uint Extra6,
			uint RequiredCount,
			CompletedQuestExtractor.TextRef Objective,
			CompletedQuestExtractor.TextRef? Trailing);

		public static List<Goal> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var byGoal = new Dictionary<uint, Goal>();
			if (packets == null)
				return [];

			foreach (var p in packets)
			{
				if (!SnapshotJournalReader.TryRead(p.Bytes, out _, out var journal))
					continue;

				foreach (var record in journal)
				{
					if (record.Objectives is not { Goals.Count: > 0 } objectives)
						continue;

					foreach (var g in objectives.Goals)
					{
						// GoalId is the table's PK and is globally unique across the corpus (verified: 158 distinct
						// ids, zero used by more than one quest), so first writer wins.
						if (byGoal.ContainsKey(g.GoalId))
							continue;

						var objective = CompletedQuestExtractor.ParseRecord(Encoding.Latin1.GetString(g.Objective));
						if (objective == null)
							continue;

						byGoal[g.GoalId] = new Goal(
							record.QuestId,
							g.GoalId,
							g.N,
							g.K,
							g.SubId,
							g.MapId,
							g.MarkerX,
							g.MarkerY,
							g.Reserved.Length > 5 ? g.Reserved[5] : 0,
							g.Reserved.Length > 6 ? g.Reserved[6] : 0,
							g.RequiredCount,
							objective,
							CompletedQuestExtractor.ParseRecord(Encoding.Latin1.GetString(g.TextRecord)));
					}
				}
			}

			return [.. byGoal.Values];
		}
	}
}
