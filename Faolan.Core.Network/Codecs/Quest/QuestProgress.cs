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

		// LDB category 51000 is `npc_template.name`, so a goal whose TrailingCategory is 51000 has an NPC TEMPLATE ID in
		// its TrailingRefId - and only then. The other three categories a goal's target reference can carry (50200
		// `item.name`, 51100 `categorytree.description`, 51200 `bounded_areas`) are different id spaces entirely.
		public const uint NpcTemplateNameCategory = 51000;

		// Whether a goal is a DELIVERY - a hand-in that takes something OUT of the player's hands, rather than an
		// objective that puts something in them. TWO shapes, because the evidence arrives two ways:
		//
		//   CAPTURED  the client's kind tag (`Separator`) says `Deliver` and the trailing reference types the thing
		//             being handed over as an item. Available for the 170 goals a capture has shown.
		//   RESOLVED  a TalkToNpc goal carrying a `QuestItemId` (#704). The other 7,147 goals come from the client's
		//             quest CATALOG, which carries the objective sentence and no kind tag at all - so a hand-in there
		//             is one whose item was resolved from its own words ("Give the Crate of Steel to Turach" ->
		//             Items 31055) and written to the server-side column.
		//
		// ⚠️ The captured shape deliberately does NOT test GoalType - a hand-in is typed TalkToNpc, since the talk is
		// what triggers it, so GoalType alone cannot tell a delivery from an ordinary talk goal. The resolved shape
		// tests it for the opposite reason: `QuestItemId` is also what a LootFromNpc goal names (the item its target's
		// corpse bag must hold), and a loot goal is emphatically not a hand-in. TalkToNpc + a quest item IS one.
		public static bool IsDelivery(QuestGoalDefinition goal)
		{
			if (goal == null)
				return false;

			return goal.Separator == (uint)QuestGoalKind.Deliver && goal.TrailingCategory == ItemNameCategory && goal.TrailingRefId != 0
			       || goal.GoalType == QuestGoalType.TalkToNpc && goal.QuestItemId is > 0;
		}

		// The item a delivery hands over - what must be HELD to complete it and what is taken when it completes. The
		// resolved shape's `QuestItemId` wins: a goal that has both is one whose item someone confirmed against the
		// catalog, which is strictly better evidence than the raw wire reference. 0 when the goal is not a delivery.
		public static uint DeliveryItemId(QuestGoalDefinition goal)
		{
			return !IsDelivery(goal)
				? 0
				: goal.QuestItemId ?? goal.TrailingRefId;
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

			// PARTIAL goals are excluded: they have no verb, target or count, so nothing about them says "kill this".
			// (They no longer DEFAULT to Kill either - the un-typed default is `None` since #522 - but a partial row can
			// still be typed by a later harvest, and it must not match while it is half-filled.)
			var kills = goals.Where(g => g.GoalType == QuestGoalType.Kill && !g.Partial).OrderBy(g => g.Sequence).ToList();

			// ⚠️ Both guards below are the same mistake caught twice: a goal that never named an NPC still reached the
			// by-id branch, where its TrailingRefId was compared against a template id it is not.
			//
			//   templateId 0 - the one caller passes `WorldEntity.NpcTemplateId`, a non-nullable uint that is 0 for
			//     anything with no template behind it, while this method's contract says a null/0 means "no type id
			//     known". Before the guard, killing such a creature advanced an arbitrary kill goal of EVERY open quest.
			//
			//   a non-NPC target reference - a Kill goal's TrailingRefId is only a template id when the goal's own
			//     TYPED reference says so (`TrailingCategory` 51000 `npc_template.name`). Six shipped kill goals point
			//     at a monster CATEGORY instead (51100 `categorytree.description` - "Kill Atzel's Watch", "Kill Undeads
			//     in Xibaluku"), whose ids run 246,944..10,367,287 in an id space of their own. Comparing one to a
			//     template id is a category error that happens to be safe only while no template id collides with it.
			//     Category 0 is allowed through: that is a hand-authored or editor goal with no typed reference at all,
			//     where TrailingRefId means what the person who set it meant.
			if (templateId is > 0 and { } id
			    && kills.FirstOrDefault(g => g.TrailingRefId == id && g.TrailingCategory is 0 or NpcTemplateNameCategory) is { } byId)
				return byId;

			return string.IsNullOrEmpty(name)
				? null
				: kills.FirstOrDefault(g => !string.IsNullOrEmpty(g.TargetName) && g.TargetName.Equals(name, StringComparison.OrdinalIgnoreCase));
		}
	}
}
