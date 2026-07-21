using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// Pure quest objective-tracking rules (docs/re/quests-and-combat.md) - testable without the DB/network.
	public static class QuestProgress
	{
		// True when EVERY goal of the quest has reached its RequiredCount (#357) - the completion gate for a multi-goal
		// quest. `progressOf` returns a goal's current progress (the primary goal's scalar, or a secondary's per-goal
		// row). RequiredCount is clamped to >= 1 (a goal seeded 0 completes on its first tick). Empty goal set = complete.
		public static bool AllGoalsComplete(IEnumerable<QuestGoalDefinition> goals, Func<QuestGoalDefinition, uint> progressOf)
		{
			return goals.All(g => progressOf(g) >= Math.Max(g.RequiredCount, 1));
		}

		// The goal a trigger of `triggerType` against `targetName` advances (#357): the first (lowest-Sequence) goal of
		// that type naming that target. Multi-goal quests can have several goals; a trigger fires the one it matches, not
		// always goal[0]. Returns null if no goal matches (wrong type/target, or an unsourced target). The Sequence order
		// makes the choice deterministic when two goals share a type+target (rare).
		public static QuestGoalDefinition MatchingGoal(IEnumerable<QuestGoalDefinition> goals, QuestGoalType triggerType, string targetName)
		{
			if (goals == null || string.IsNullOrEmpty(targetName))
				return null;

			return goals
				.OrderBy(g => g.Sequence)
				.FirstOrDefault(g => g.GoalType == triggerType && !string.IsNullOrEmpty(g.TargetName) && g.TargetName.Equals(targetName, StringComparison.OrdinalIgnoreCase));
		}

		// The Kill goal a kill of the NPC with `templateId` advances (#262). The target key is the goal's TrailingRefId -
		// the CAPTURE-DERIVED target template (e.g. Saddur 9500, Scavenger Tuut 11561), which equals the killed entity's
		// NpcTemplateId. That id is STABLE where a name string is fragile: localized, and collision-prone (three distinct
		// "Scavenger Lackey" templates share one name). `name` is a fallback for a goal seeded WITHOUT a matching template
		// id - either a name-only seed, or a "kill N <group>" goal whose TrailingRefId is a monster-GROUP id (no single
		// template), where the shared name is the only key that catches every variant. Kill-typed goals only.
		public static QuestGoalDefinition MatchingKillGoal(IEnumerable<QuestGoalDefinition> goals, uint? templateId, string name)
		{
			if (goals == null)
				return null;

			var kills = goals.Where(g => g.GoalType == QuestGoalType.Kill).OrderBy(g => g.Sequence).ToList();

			if (templateId is { } id && kills.FirstOrDefault(g => g.TrailingRefId == id) is { } byId)
				return byId;

			return string.IsNullOrEmpty(name)
				? null
				: kills.FirstOrDefault(g => !string.IsNullOrEmpty(g.TargetName) && g.TargetName.Equals(name, StringComparison.OrdinalIgnoreCase));
		}
	}
}
