using System.Collections.Generic;
using Faolan.Core.Network.Codecs.Quest;
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
		// The captured objective goals per quest, exactly as the wire carried them - the oracle the importer compares
		// a rebuilt region against (ImportOps.VerifyObjectiveRegions).
		//
		// EVERY capture of the quest, not just the first, and each one TAGGED with its record's `XpReward`: retail has
		// more than one spelling of the same quest's region, and F0 is what selects it. Quest 10238's goal 1448 carries
		// its trailing text as `<remoteformat …></remoteformat>` (95 B) in every capture whose record has F0=150, and
		// as the 21-byte-shorter `<localized … flags="" …>` in every capture whose record has F0=175 - 20 of 20
		// occurrences, no exceptions, with every other scalar on the goal identical. So "reproduces one capture but not
		// another" is not necessarily a broken region: it can be a faithful region of the OTHER variant. The verifier
		// compares like with like by grouping on this tag; what F0 itself means is still undecoded (#389).
		public static Dictionary<uint, List<(uint Variant, List<QuestObjectiveGoal> Goals)>> CapturedRegions(IReadOnlyList<CapturedPacket> packets)
		{
			var byQuest = new Dictionary<uint, List<(uint Variant, List<QuestObjectiveGoal> Goals)>>();
			if (packets == null)
				return byQuest;

			foreach (var p in packets)
			{
				if (!SnapshotJournalReader.TryRead(p.Bytes, out _, out var journal))
					continue;

				foreach (var record in journal)
				{
					if (record.Objectives is { Goals.Count: > 0 } objectives)
					{
						(byQuest.TryGetValue(record.QuestId, out var all)
							? all
							: byQuest[record.QuestId] = []).Add((record.XpReward, objectives.Goals));
					}
				}
			}

			return byQuest;
		}

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

					for (var gi = 0; gi < objectives.Goals.Count; gi++)
					{
						var g = objectives.Goals[gi];
						// GoalId is the table's PK and is globally unique across the corpus (verified: 158 distinct
						// ids, zero used by more than one quest), so first writer wins.
						if (byGoal.ContainsKey(g.GoalId))
							continue;

						var objective = CompletedQuestExtractor.ParseRecord(g.Objective);
						if (objective == null)
							continue;

						byGoal[g.GoalId] = new Goal(record.QuestId, g.GoalId, g.N, (uint)gi, g.K, g.SubId, g.MapId, g.MarkerX, g.MarkerY, g.Unknown1, g.HideMapMarker, g.GoalIdRepeat, g.Reserved.Length > 1
							? g.Reserved[1]
							: 0, g.Reserved.Length > 2
							? g.Reserved[2]
							: 0, g.Reserved.Length > 3
							? g.Reserved[3]
							: 0, g.Reserved.Length > 4
							? g.Reserved[4]
							: 0, g.Reserved.Length > 5
							? g.Reserved[5]
							: 0, g.Reserved.Length > 6
							? g.Reserved[6]
							: 0, g.RequiredCount, objective, CompletedQuestExtractor.ParseRecord(g.TextRecord));
					}
				}
			}

			return [.. byGoal.Values];
		}

		public sealed record Goal(
			uint QuestId,
			uint GoalId,
			uint Sequence,
			uint RegionOrder,
			uint Separator,
			uint SubId,
			uint MapId,
			uint MarkerX,
			uint MarkerY,
			uint Unknown1,
			bool HideMapMarker,
			uint GoalIdEcho,
			uint Extra1,
			uint Extra2,
			uint Extra3,
			uint Extra4,
			uint Extra5,
			uint Extra6,
			uint RequiredCount,
			CompletedQuestExtractor.TextRef Objective,
			CompletedQuestExtractor.TextRef? Trailing);
	}
}
