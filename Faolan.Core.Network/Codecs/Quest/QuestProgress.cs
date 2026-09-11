using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     Pure quest objective-tracking rules; see docs/re/quests-and-combat.md. Testable without the database or the
	///     network.
	/// </summary>
	public static class QuestProgress
	{
		/// <summary>
		///     True when every goal of the quest has reached its RequiredCount - the completion gate for a multi-goal quest.
		///     <paramref name="progressOf" /> returns a goal's current progress: the primary goal's scalar, or a secondary's
		///     per-goal row. An empty goal set counts as complete, and RequiredCount is clamped to at least 1, so a goal
		///     seeded 0 completes on its first tick.
		/// </summary>
		/// <remarks>
		///     Partial goals are skipped. They were harvested mid-session, so the row has no verb, target or required count
		///     and nothing can ever advance one; counting them would make their quest permanently incompletable. See
		///     QuestGoalDefinition.Partial.
		/// </remarks>
		public static bool AllGoalsComplete(IEnumerable<QuestGoalDefinition> goals, Func<QuestGoalDefinition, uint> progressOf)
		{
			return goals.Where(g => !g.Partial).All(g => progressOf(g) >= Math.Max(g.RequiredCount, 1));
		}

		/// <summary>
		///     A quest's goals in the order they are played, which is the client's Phase rather than Sequence.
		/// </summary>
		/// <remarks>
		///     The two are different fields. Sequence is a wire value - written out as the goal's N in the objective region
		///     (QuestObjectiveRegions) and byte-exact verified against captures - which is why five shipped quests carry
		///     Sequence 1 on every goal: those are parallel objectives ("collect all seven faction documents", "kill all
		///     three") that retail numbers all 1 because there is no order to state. Renumbering them to make them sortable
		///     changes bytes retail sent.
		///     <para>
		///         Phase is the client's own step number out of the quest catalog (RDB 1000104), and it is what orders a
		///         sequential quest. The corpus corroborates it: retail's Advanced hand-off is only sent on a goal that has a
		///         successor, and in every captured multi-goal quest the goal that takes it is the lower phase. Quest 12369
		///         "The Acheronian Gate" is where the two visibly disagree - g7134 "Find the gate key" is phase 1 and takes
		///         the Advanced, while Sequence has it second.
		///     </para>
		///     <para>
		///         Ties - a parallel quest, every goal one phase - fall back to Sequence then GoalId, so the order is
		///         deterministic without pretending the goals are ordered.
		///     </para>
		/// </remarks>
		public static IEnumerable<QuestGoalDefinition> InPlayOrder(IEnumerable<QuestGoalDefinition> goals)
		{
			return goals == null
				? []
				: goals.OrderBy(g => g.Phase).ThenBy(g => g.Sequence).ThenBy(g => g.GoalId);
		}

		/// <summary>
		///     LDB category 50200 is <c>item.name</c>, so a goal whose TrailingCategory is 50200 carries an item template id
		///     in its TrailingRefId. See docs/rdb/ldb-string-index.md.
		/// </summary>
		public const uint ItemNameCategory = 50200;

		/// <summary>
		///     LDB category 51000 is <c>npc_template.name</c>, so a goal whose TrailingCategory is 51000 carries an NPC
		///     template id in its TrailingRefId - and only then. The other three categories a goal's target reference can
		///     carry (50200 <c>item.name</c>, 51100 <c>categorytree.description</c>, 51200 <c>bounded_areas</c>) are
		///     different id spaces entirely.
		/// </summary>
		public const uint NpcTemplateNameCategory = 51000;

		/// <summary>
		///     Whether a goal is a delivery - a hand-in that takes something out of the player's hands, rather than an
		///     objective that puts something in them.
		/// </summary>
		/// <remarks>
		///     Two shapes, because the evidence arrives two ways. The captured shape is the client's kind tag (Separator)
		///     saying Deliver with the trailing reference typing the thing handed over as an item, available for the 170
		///     goals a capture has shown. The resolved shape is a TalkToNpc goal carrying a QuestItemId: the other 7,147
		///     goals come from the client's quest catalog, which carries the objective sentence and no kind tag, so a hand-in
		///     there is one whose item was resolved from its own words ("Give the Crate of Steel to Turach" gives Items
		///     31055) and written to the server-side column.
		///     <para>
		///         The captured shape deliberately does not test GoalType: a hand-in is typed TalkToNpc, since the talk is
		///         what triggers it, so GoalType alone cannot tell a delivery from an ordinary talk goal. The resolved shape
		///         tests it for the opposite reason - QuestItemId is also what a LootFromNpc goal names, the item its target's
		///         corpse bag must hold, and a loot goal is not a hand-in.
		///     </para>
		/// </remarks>
		public static bool IsDelivery(QuestGoalDefinition goal)
		{
			return (goal is { Separator: (uint)QuestGoalKind.Deliver, TrailingCategory: ItemNameCategory } && goal.TrailingRefId != 0)
			       || goal is { GoalType: QuestGoalType.TalkToNpc, QuestItemId: > 0 };
		}

		/// <summary>
		///     The item a delivery hands over: what must be held to complete it, and what is taken when it completes. The
		///     resolved shape's QuestItemId wins, a goal that has both being one whose item was confirmed against the
		///     catalog, which is better evidence than the raw wire reference. Zero when the goal is not a delivery.
		/// </summary>
		public static uint DeliveryItemId(QuestGoalDefinition goal)
		{
			return !IsDelivery(goal)
				? 0
				: goal.QuestItemId ?? goal.TrailingRefId;
		}

		/// <summary>
		///     Whether <paramref name="heldCount" /> of the delivered item is enough to hand the goal in. Non-deliveries
		///     always pass, so this is a no-op on kills, collects and reaches.
		/// </summary>
		/// <remarks>
		///     The full count is required - "deliver 3 stone bricks to Turach" is not satisfied by carrying one - and
		///     RequiredCount is clamped to at least 1, matching every other count rule here. Without this gate a delivery
		///     completes on the talk alone, closing the objective while the player hands over nothing.
		/// </remarks>
		public static bool CanHandIn(QuestGoalDefinition goal, uint heldCount)
		{
			return !IsDelivery(goal) || heldCount >= Math.Max(1u, goal.RequiredCount);
		}

		/// <summary>
		///     Whether the player meets a goal's stated precondition. True when the goal has none, so this is a no-op for the
		///     overwhelming majority.
		/// </summary>
		/// <remarks>
		///     <paramref name="factionRank" /> resolves the player's rank in a named faction, or null when that is not
		///     knowable. It is a parameter rather than a lookup because there is no faction system to ask: a resolver that
		///     always returns null makes every rank-gated goal unmet, which is the safe reading, where assuming the
		///     precondition met completes those quests early and silently. Unknown is deliberately not met, which is what
		///     lets the goal be wired and inert today and become playable the day the resolver can answer, with no re-harvest
		///     and no re-wiring.
		/// </remarks>
		public static bool PreconditionMet(QuestGoalDefinition goal, uint playerLevel, Func<string, uint?>? factionRank = null)
		{
			if (goal.RequiredLevel is { } level && playerLevel < level)
				return false;

			if (goal.RequiredRankTitle is not { Length: > 0 } title)
				return true;

			var held = factionRank?.Invoke(title);
			return held is { } rank && rank >= Math.Max(1u, goal.RequiredRank ?? 1u);
		}

		/// <summary>
		///     The CollectItem goal for <paramref name="itemId" />: the first, in play order, whose TrailingRefId is that
		///     item - the item a quest pickup grants, such as Shipwrecked V's stone brick 193400. Null if none, which is what
		///     keeps a pickup inert unless a matching collect quest is active.
		/// </summary>
		public static QuestGoalDefinition? MatchingCollectGoal(IEnumerable<QuestGoalDefinition> goals, uint itemId)
		{
			return itemId == 0
				? null
				: InPlayOrder(goals.Where(g => g.GoalType == QuestGoalType.CollectItem && g.TrailingRefId == itemId)).FirstOrDefault();
		}

		/// <summary>
		///     The UseItem goal for <paramref name="itemId" />: the first, in play order, whose TrailingRefId is that item.
		///     The same shape as <see cref="MatchingCollectGoal" /> and deliberately a separate type - a collect is satisfied
		///     by holding the item, a use by consuming it, and the one shipped Use goal is an item the quest giver hands you,
		///     so counting its arrival would solve the objective before the player did anything.
		/// </summary>
		public static QuestGoalDefinition? MatchingUseGoal(IEnumerable<QuestGoalDefinition> goals, uint itemId)
		{
			return itemId == 0
				? null
				: InPlayOrder(goals.Where(g => g.GoalType == QuestGoalType.UseItem && g.TrailingRefId == itemId)).FirstOrDefault();
		}

		/// <summary>
		///     The UseWorldObject goal for <paramref name="worldObjectId" />: the first, in play order, pointing at the door,
		///     gate or lever just used. It matches on the goal's own TargetWorldObjectId column rather than TrailingRefId.
		/// </summary>
		public static QuestGoalDefinition? MatchingUseObjectGoal(IEnumerable<QuestGoalDefinition> goals, uint worldObjectId)
		{
			return worldObjectId == 0
				? null
				: InPlayOrder(goals.Where(g => g.GoalType == QuestGoalType.UseWorldObject && g.TargetWorldObjectId == worldObjectId)).FirstOrDefault();
		}

		/// <summary>
		///     The item a goal requires the player to be carrying, without taking it; zero when it requires none.
		/// </summary>
		/// <remarks>
		///     Distinct from <see cref="DeliveryItemId" /> beside it: a delivery hands its item over and the hand-in takes
		///     it, while "unlock the gate with the key" wants the key in hand to open it with. Only UseWorldObject reads
		///     this.
		///     <para>
		///         The rule is that the goal which spends an item takes it, not that a completed quest clears its container.
		///         Measured over the whole corpus (<c>--report quest-container</c>), 7 of the 17 completions that happened
		///         while the quest container held something kept their items. The decisive pair is one chain: 13563 "Get the
		///         Scavenger's Key" is an acquire goal, completes holding the key, and the key stays, while 13480 "Free
		///         Casilda" - the step that uses it - completes and the key goes, one frame after completion
		///         (Hoxxling_mage-heraldofxotli_login_gameplay fr12206).
		///     </para>
		///     <para>
		///         This predicate is only the hold check: it returns the item the goal demands, and the caller decides whether
		///         that demand is satisfied or spent (QuestProgressionService.ConsumeRequiredItem).
		///     </para>
		/// </remarks>
		public static uint RequiredHeldItemId(QuestGoalDefinition goal)
		{
			return goal is { GoalType: QuestGoalType.UseWorldObject, QuestItemId: > 0 }
				? goal.QuestItemId.Value
				: 0;
		}

		/// <summary>
		///     The NPC template a goal targets, or null when it has not been resolved to one.
		/// </summary>
		/// <remarks>
		///     Two sources, which never disagree - the second is where the first came from wherever both exist.
		///     TargetTemplateId is the resolved column, available to every goal type
		///     (ImportOps.ResolveGoalTargetTemplates). TrailingRefId is the goal's own typed reference, but only when its
		///     category says it is an npc_template reference; it is kept as a second source because a hand-authored or
		///     Quest-editor goal can carry a target reference without having been through the resolver.
		///     <para>
		///         Category 0 is allowed through on the reference: that is a goal with no typed reference at all, where
		///         TrailingRefId means what the person who set it meant. Every other category is a different id space - six
		///         shipped kill goals point at a monster category (51100, ids 246,944 to 10,367,287) - and comparing one to a
		///         template id is a category error that is only safe while the two spaces do not collide.
		///     </para>
		/// </remarks>
		public static uint? TargetTemplateOf(QuestGoalDefinition goal)
		{
			return goal.TargetTemplateId > 0
				? goal.TargetTemplateId
				: goal is { TrailingRefId: > 0, TrailingCategory: 0 or NpcTemplateNameCategory }
					? goal.TrailingRefId
					: null;
		}

		/// <summary>
		///     The marker coordinates that are placeholders rather than places. A ReachArea goal needs this gate where a kill
		///     or a talk goal does not, because a reach goal's whole target is its pin: an unset pin makes the goal solvable
		///     by standing in the corner of the map.
		/// </summary>
		/// <remarks>
		///     The tell is that the same coordinate spans different maps. Across the 136 shipped goals that carry a map,
		///     (50, 50) appears on nine different playfields (6110, 6120, 6121, 6122, 6130, 6212, 6310, 6311, 6312) and
		///     (1, 1) on three (2095, 5201, 6400), while every other repeated coordinate is confined to a single map -
		///     (781, 348) six times, all on 3044, and (913, 718) twice, both on 4020 - which is what a genuinely shared
		///     location looks like. A place cannot be on nine playfields at once, so these two are the content editor's
		///     unfilled default.
		/// </remarks>
		public static bool IsPlaceholderMarker(uint? markerX, uint? markerY)
		{
			return (markerX, markerY) is (50, 50) or (1, 1);
		}

		/// <summary>
		///     Whether a goal carries a pin a reach can be judged against: a map, and a marker that is a place. Used by the
		///     runtime trigger and by the importer's typing pass, so a row that slips past one is caught by the other.
		/// </summary>
		public static bool HasReachablePin(QuestGoalDefinition goal)
		{
			return goal.MapId is > 0 && (goal.MarkerX != null || goal.MarkerY != null) && !IsPlaceholderMarker(goal.MarkerX, goal.MarkerY);
		}

		/// <summary>
		///     Whether the goal targets the live NPC identified by (<paramref name="templateId" />,
		///     <paramref name="name" />) - the id-first, name-second pair every target-matching trigger shares.
		/// </summary>
		/// <remarks>
		///     A goal that has a resolved template is matched on the id alone; the name is never consulted as a second
		///     chance. "Saddur" is two templates (2993 on the Stygian Galley, 9500 on Tortage Island), so letting a goal that
		///     names 9500 fall through to a string compare would match the galley one. An unresolved id means unknown, not
		///     no.
		///     <para>
		///         The name branch is not a legacy path and does not shrink as the world data fills in. It is the only key a
		///         goal can use when its target is a group rather than one creature - several templates sharing one name,
		///         where the shared string catches every variant - and it carries the 586 goals whose NPCs live on maps that
		///         have not been imported yet.
		///     </para>
		/// </remarks>
		public static bool TargetsNpc(QuestGoalDefinition goal, uint? templateId, string? name)
		{
			if (TargetTemplateOf(goal) is { } target)
				return templateId is > 0 and { } id && id == target;

			return !string.IsNullOrEmpty(goal.TargetName) && !string.IsNullOrEmpty(name)
			       && goal.TargetName.Equals(name, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		///     The LootFromNpc goal a looted item advances. Both must match: the bag's source NPC, through the goal's target,
		///     and the item the player actually received, through its QuestItemId.
		/// </summary>
		/// <remarks>
		///     Matching the NPC alone fires the goal on taking any item off that corpse, and on emptying a bag that never
		///     held the item, so the quest can be solved without the player ever holding the thing the objective names.
		///     <para>
		///         A goal with no QuestItemId therefore matches nothing, deliberately. Every LootFromNpc goal in the game data
		///         names its item (3492 Saddur to 193186, 5867 Scavenger Tuut to 319224), so one without is a data gap rather
		///         than a shape to support with a wildcard, and the loot path says so when it meets one
		///         (QuestMarkerResolver.ActiveLootGoalItemFor).
		///     </para>
		/// </remarks>
		public static QuestGoalDefinition? MatchingLootGoal(IEnumerable<QuestGoalDefinition> goals, string sourceNpcName, uint grantedItemId,
			uint? sourceTemplateId = null)
		{
			if (grantedItemId == 0 || (string.IsNullOrEmpty(sourceNpcName) && sourceTemplateId is not > 0))
				return null;

			return InPlayOrder(goals)
				.FirstOrDefault(g => g.GoalType == QuestGoalType.LootFromNpc && g.QuestItemId == grantedItemId
				                     && TargetsNpc(g, sourceTemplateId, sourceNpcName));
		}

		/// <summary>
		///     The Escort goal the escortee with <paramref name="templateId" /> advances. An Escort goal's TrailingRefId is
		///     the escortee's NPC template - Casilda 9411 on quest 12407, the only escort in the shipped data - so it matches
		///     on the stable id with no name fallback, unlike the kill path: an escort has exactly one escortee, and a goal
		///     without the id names an NPC that could not be resolved anyway.
		/// </summary>
		public static QuestGoalDefinition? MatchingEscortGoal(IEnumerable<QuestGoalDefinition> goals, uint templateId)
		{
			if (templateId == 0)
				return null;

			return goals
				.OrderBy(g => g.Sequence)
				.FirstOrDefault(g => g.GoalType == QuestGoalType.Escort && g.TrailingRefId == templateId);
		}

		/// <summary>
		///     The goal a trigger of <paramref name="triggerType" /> against <paramref name="targetName" /> advances: the
		///     first goal of that type naming that target. A multi-goal quest can hold several, and a trigger fires the one
		///     it matches rather than always the first. Null when no goal matches - wrong type or target, or an unsourced
		///     target.
		/// </summary>
		public static QuestGoalDefinition? MatchingGoal(IEnumerable<QuestGoalDefinition> goals, QuestGoalType triggerType, string targetName,
			uint? targetTemplateId = null)
		{
			if (string.IsNullOrEmpty(targetName) && targetTemplateId is not > 0)
				return null;

			return goals
				.OrderBy(g => g.Sequence)
				.FirstOrDefault(g => g.GoalType == triggerType && TargetsNpc(g, targetTemplateId, targetName));
		}

		/// <summary>
		///     The Kill goal a kill of the NPC with <paramref name="templateId" /> advances. The target key is the goal's
		///     capture-derived target template (Saddur 9500, Scavenger Tuut 11561), which equals the killed entity's
		///     NpcTemplateId - a stable id where a name string is localized and collision-prone, three distinct "Scavenger
		///     Lackey" templates sharing one name.
		/// </summary>
		/// <remarks>
		///     <paramref name="name" /> is a fallback for a goal seeded without a matching template id: either a name-only
		///     seed, or a "kill N of a group" goal whose reference is a monster-group id with no single template, where the
		///     shared name is the only key that catches every variant. Kill-typed goals only.
		/// </remarks>
		public static QuestGoalDefinition? MatchingKillGoal(IEnumerable<QuestGoalDefinition> goals, uint? templateId, string name)
		{
			// Partial goals are excluded: they have no verb, target or count, so nothing about them says "kill this". A
			// partial row can still be typed by a later harvest, and it must not match while it is half-filled.
			var kills = goals.Where(g => g is { GoalType: QuestGoalType.Kill, Partial: false }).OrderBy(g => g.Sequence).ToList();

			// TargetTemplateOf is what decides whether a goal has a target id at all, and both of its guards matter
			// here. A templateId of 0 - what the one caller passes for anything with no template behind it - must not
			// reach the by-id branch, or killing such a creature advances an arbitrary kill goal of every open quest.
			// Nor may a non-NPC target reference: a Kill goal's TrailingRefId is only a template id when its own typed
			// reference says so (TrailingCategory 51000, npc_template.name), and six shipped kill goals point at a
			// monster category instead (51100, categorytree.description - "Kill Atzel's Watch", "Kill Undeads in
			// Xibaluku"), whose ids run 246,944 to 10,367,287 in an id space of their own.
			//
			// Two passes, and it stays two rather than collapsing into one TargetsNpc scan: a quest can hold both an
			// id-targeted and a name-targeted kill goal, and a single pass would hand the kill to whichever comes first
			// in sequence even when a later goal names the creature's exact template. An id match is better evidence
			// than a string match wherever it sits in the quest.
			if (templateId is > 0 and { } id && kills.FirstOrDefault(g => TargetTemplateOf(g) == id) is { } byId)
				return byId;

			// Name pass, restricted to goals with no target id. A goal that names a template and did not match it above
			// has answered - it is not this creature - and must not get a second chance on a string that two templates
			// share; see TargetsNpc.
			return string.IsNullOrEmpty(name)
				? null
				: kills.FirstOrDefault(g => TargetTemplateOf(g) is null && !string.IsNullOrEmpty(g.TargetName)
				                            && g.TargetName.Equals(name, StringComparison.OrdinalIgnoreCase));
		}
	}
}
