using System;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// Pure quest objective-tracking rules (docs/re/quests-and-combat.md) - testable without the DB/network.
	public static class QuestProgress
	{
		// True if a trigger of `triggerType` against `targetName` advances this quest's goal (#261): the quest is Active,
		// its first goal is of that type and names that target, and the objective isn't already complete. The trigger
		// fires per goal type - a kill (CombatService), a corpse-bag loot (LootService), or a talk (ConversationService).
		// Required is clamped to >= 1 so a goal seeded with RequiredCount 0 still completes on the first trigger.
		public static bool Advances(CharacterQuest quest, QuestGoalDefinition goal, QuestGoalType triggerType, string targetName)
		{
			return quest.State == QuestState.Active && goal != null && goal.GoalType == triggerType && !string.IsNullOrEmpty(goal.TargetName) && !string.IsNullOrEmpty(targetName) &&
			       goal.TargetName.Equals(targetName, StringComparison.OrdinalIgnoreCase) && quest.GoalProgress < Math.Max(quest.GoalRequired, 1);
		}

		// True if a Lua `SolveGoal(goalId)` effect (the RDB quest engine, #356) completes this quest's goal: the quest is
		// Active, its first goal IS goal `goalId`, and the objective isn't already complete. Unlike Advances, a Lua solve
		// is not gated on a target NAME - the script itself decided the goal is solved, so any goal type resolves.
		public static bool SolvesGoal(CharacterQuest quest, QuestGoalDefinition goal, uint goalId)
		{
			return quest.State == QuestState.Active && goal != null && goalId != 0 && goal.GoalId == goalId &&
			       quest.GoalProgress < Math.Max(quest.GoalRequired, 1);
		}

		// True if killing an NPC named `npcName` advances this quest's kill objective (active, matching, not yet done).
		public static bool AdvancesOnKill(CharacterQuest quest, string npcName)
		{
			return quest.State == QuestState.Active && !string.IsNullOrEmpty(quest.GoalTargetName) && !string.IsNullOrEmpty(npcName) &&
			       quest.GoalTargetName.Equals(npcName, StringComparison.OrdinalIgnoreCase) && quest.GoalProgress < quest.GoalRequired;
		}
	}
}
