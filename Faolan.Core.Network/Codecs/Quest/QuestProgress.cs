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
		// PARTIAL goals are skipped: they were harvested mid-session, so the row has no verb, target or required count
		// and nothing can ever advance one. Counting them would make their quest permanently incompletable - see
		// QuestGoalDefinition.Partial. A quest of nothing but partial goals therefore completes on its known goals,
		// which is the same behaviour as before those rows existed.
		public static bool AllGoalsComplete(IEnumerable<QuestGoalDefinition> goals, Func<QuestGoalDefinition, uint> progressOf)
		{
			return goals.Where(g => !g.Partial).All(g => progressOf(g) >= Math.Max(g.RequiredCount, 1));
		}

		// A quest's goals in the order they are PLAYED (#527) - which is the client's `Phase`, not `Sequence`.
		//
		// ⚠️ Those two are not the same field wearing two names, and using `Sequence` for this is the mistake this
		// method exists to stop. `Sequence` is a WIRE VALUE: it is written out as the goal's `N` in the objective
		// region (QuestObjectiveRegions), and it is byte-exact verified against captures - which is why five shipped
		// quests carry `Sequence` = 1 on EVERY goal. Those are parallel objectives ("collect all seven faction
		// documents", "kill all three"), and retail numbers them all 1 because there is no order to state. Renumbering
		// them 1..n to make them sortable changes bytes retail sent, and the golden test says so immediately.
		//
		// `Phase` is the client's own step number out of the quest catalog (RDB 1000104). It is what orders a
		// SEQUENTIAL quest, and the corpus corroborates it: retail's `Advanced` hand-off is only sent on a goal that
		// has a successor, and in every captured multi-goal quest the goal that takes it is the lower phase. Quest
		// 12369 "The Acheronian Gate" is where the two visibly disagree - g7134 "Find the gate key" is phase 1 and
		// takes the `Advanced`, while `Sequence` has it second.
		//
		// Ties (a parallel quest, every goal one phase) fall back to `Sequence` then `GoalId`, so the order is
		// deterministic without pretending the goals are ordered.
		public static IEnumerable<QuestGoalDefinition> InPlayOrder(IEnumerable<QuestGoalDefinition> goals)
		{
			return goals == null
				? []
				: goals.OrderBy(g => g.Phase).ThenBy(g => g.Sequence).ThenBy(g => g.GoalId);
		}

		// LDB category 50200 is `item.name`, so a goal whose TrailingCategory is 50200 has an ITEM TEMPLATE ID in its
		// TrailingRefId (docs/rdb/ldb-string-index.md).
		public const uint ItemNameCategory = 50200;

		// Whether a goal is a DELIVERY - a hand-in that takes something OUT of the player's hands, rather than an
		// objective that puts something in them. Identified by the goal's own captured data: the client's kind tag
		// (`Separator`) says `Deliver`, and the trailing reference types the thing being handed over as an item.
		//
		// Keyed on the captured fields rather than on GoalType, which is OUR wiring - a hand-in is typed TalkToNpc
		// (the talk is what triggers it), so testing GoalType could not tell a delivery from an ordinary talk goal.
		public static bool IsDelivery(QuestGoalDefinition goal)
		{
			return goal != null && goal.Separator == (uint)QuestGoalKind.Deliver
			                    && goal.TrailingCategory == ItemNameCategory && goal.TrailingRefId != 0;
		}

		// Whether `heldCount` of the delivered item is enough to hand `goal` in (#521). Non-deliveries always pass, so
		// this is a no-op on kills, collects and reaches.
		//
		// The FULL count is required: "deliver 3 stone bricks to Turach" is not satisfied by carrying one. RequiredCount
		// is clamped to >= 1 to match every other count rule here (a goal seeded 0 needs one).
		//
		// Without this a delivery completes on the TALK alone, so the player hands over nothing and the objective still
		// closes - the goal reads as wired while never actually being played.
		public static bool CanHandIn(QuestGoalDefinition goal, uint heldCount)
		{
			return !IsDelivery(goal) || heldCount >= Math.Max(1u, goal.RequiredCount);
		}

		// Whether the player meets a goal's stated PRECONDITION (#521). True when the goal has none, so this is a no-op
		// for the overwhelming majority.
		//
		// `factionRank` resolves the player's rank in a named faction, or null when that is not knowable. It is a
		// parameter rather than a lookup because the faction system does not exist yet (#310): passing a resolver that
		// always returns null makes every rank-gated goal correctly UNMET, which is the safe reading - the alternative
		// (assume met) completes those quests early, which is silent and wrong.
		//
		// ⚠️ Unknown is NOT met. That is the whole point of enforcing here rather than refusing to wire the goal: the
		// goal is wired and inert today, and becomes playable the day the resolver can answer - with no re-harvest and
		// no re-wiring.
		public static bool PreconditionMet(QuestGoalDefinition goal, uint playerLevel, Func<string, uint?>? factionRank = null)
		{
			if (goal == null)
				return true;

			if (goal.RequiredLevel is { } level && playerLevel < level)
				return false;

			if (goal.RequiredRankTitle is not { Length: > 0 } title)
				return true;

			var held = factionRank?.Invoke(title);
			return held is { } rank && rank >= Math.Max(1u, goal.RequiredRank ?? 1u);
		}

		// The CollectItem goal for `itemId` (#262): the first (in PLAY order - see InPlayOrder) CollectItem goal whose TrailingRefId is that item
		// (the item a quest-pickup grants, e.g. Shipwrecked V's stone brick 193400). Null if none - the pickup is inert
		// unless a matching collect quest is active, which is what gates the brick nodes to quest-holders.
		public static QuestGoalDefinition? MatchingCollectGoal(IEnumerable<QuestGoalDefinition> goals, uint itemId)
		{
			if (goals == null || itemId == 0)
				return null;

			return InPlayOrder(goals.Where(g => g.GoalType == QuestGoalType.CollectItem && g.TrailingRefId == itemId)).FirstOrDefault();
		}

		// The goal a trigger of `triggerType` against `targetName` advances (#357): the first (in PLAY order - see
		// InPlayOrder) goal of that type naming that target. Multi-goal quests can have several goals; a trigger fires
		// the one it matches, not always goal[0]. Returns null if no goal matches (wrong type/target, or an unsourced
		// target). The play order makes the choice deterministic when two goals share a type+target (rare).
		// The LootFromNpc goal a looted item advances (#261). BOTH must match: the bag's source NPC (the goal's
		// TargetName) and the item the player actually RECEIVED (its QuestItemId). Matching the NPC alone made the goal
		// fire on taking ANY item off that corpse - and on emptying a bag that never held it - so the quest could
		// be solved without the player ever holding the thing the objective names.
		//
		// A goal with no QuestItemId therefore matches NOTHING, deliberately. Every LootFromNpc goal in the game data
		// names its item (3492 Saddur -> 193186, 5867 Scavenger Tuut -> 319224), so one without is a data gap, not a
		// shape to support with a wildcard - and the loot path says so out loud when it meets one
		// (WorldBroadcaster.ActiveLootGoalItemFor) rather than quietly accepting any item in its place.
		public static QuestGoalDefinition? MatchingLootGoal(IEnumerable<QuestGoalDefinition> goals, string sourceNpcName, uint grantedItemId)
		{
			if (goals == null || string.IsNullOrEmpty(sourceNpcName) || grantedItemId == 0)
				return null;

			return InPlayOrder(goals)
				.FirstOrDefault(g => g.GoalType == QuestGoalType.LootFromNpc && g.QuestItemId == grantedItemId && !string.IsNullOrEmpty(g.TargetName)
				                     && g.TargetName.Equals(sourceNpcName, StringComparison.OrdinalIgnoreCase));
		}

		// The Escort goal the escortee with `templateId` advances (#646). An Escort goal's TrailingRefId IS the
		// escortee's NPC template - Casilda 9411 on quest 12407, the only escort in the shipped data - so it matches on
		// the stable id with no name fallback, unlike the kill path. There is nothing for a fallback to catch: an
		// escort has exactly one escortee, and a goal without the id names an NPC we could not resolve anyway.
		public static QuestGoalDefinition? MatchingEscortGoal(IEnumerable<QuestGoalDefinition> goals, uint templateId)
		{
			if (goals == null || templateId == 0)
				return null;

			return goals
				.OrderBy(g => g.Sequence)
				.FirstOrDefault(g => g.GoalType == QuestGoalType.Escort && g.TrailingRefId == templateId);
		}

		public static QuestGoalDefinition? MatchingGoal(IEnumerable<QuestGoalDefinition> goals, QuestGoalType triggerType, string targetName)
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
		public static QuestGoalDefinition? MatchingKillGoal(IEnumerable<QuestGoalDefinition> goals, uint? templateId, string name)
		{
			if (goals == null)
				return null;

			// PARTIAL goals are excluded. They are untyped rows that DEFAULT to Kill with TrailingRefId 0, so the by-id
			// branch below would match any kill whose template id is 0 - a spurious advance on a goal the server has no
			// definition for. The name branch already cannot match one (their TargetName is null).
			var kills = goals.Where(g => g.GoalType == QuestGoalType.Kill && !g.Partial).OrderBy(g => g.Sequence).ToList();

			// ⚠️ ...and `Partial` was only HALF of that argument. **7,170** non-partial goals also sit at
			// `GoalType.Kill` with `TrailingRefId 0` - the untyped rump of the catalog - so a template id of 0 matches
			// the first of them in every active quest. And 0 is reachable: the one caller passes
			// `WorldEntity.NpcTemplateId`, a non-nullable uint that is 0 for anything with no template behind it, so
			// killing such a creature advanced an arbitrary goal of EVERY quest the player had open.
			//
			// A 0 means "no type id known", which is what this method's own contract says - so it takes the NAME
			// branch, exactly as a null does. The three sibling matchers (`MatchingCollectGoal`, `MatchingLootGoal`,
			// `MatchingEscortGoal`) all guard 0 on entry already; this was the one that did not.
			if (templateId is > 0 and { } id && kills.FirstOrDefault(g => g.TrailingRefId == id) is { } byId)
				return byId;

			return string.IsNullOrEmpty(name)
				? null
				: kills.FirstOrDefault(g => !string.IsNullOrEmpty(g.TargetName) && g.TargetName.Equals(name, StringComparison.OrdinalIgnoreCase));
		}
	}
}
