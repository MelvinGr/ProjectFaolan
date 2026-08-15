using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Economy;
using Faolan.GameServer.World;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	// The multi-stage quest-chain SUPERSESSION engine (Gitea #261). Each stage of a chain is its own quest id;
	// completing a stage's goal supersedes it with the next via the exact captured wire sequence
	// (Blablatest_..._casilda_quest_first_stage, fr1356/1446/1605):
	//
	//   QuestGoal(completing, goalId, required)   -- the objective counter hits its required count
	//   QuestIIR(completing, StateActive=2)       -- journal: active/update
	//   QuestIIR(completing, StateCompleted=4)    -- journal: completed
	//   QuestFullUpdate(next)                      -- supersession: the client swaps the displayed quest to the next stage
	//
	// The wire output is IDENTICAL for every goal type; the type (Kill/LootFromNpc/TalkToNpc/Escort) only selects which
	// SERVER trigger matches, so the three trigger owners - CombatService (kill), LootService (corpse-bag loot),
	// ConversationService (talk) - all funnel through this one engine. The Tortage intro chain is
	// 13479 (kill Tuut) -> 13563 (loot Tuut's bag) -> 13480 (talk Casilda) -> 12407 (escort) -> end.
	// Replaces the single-stage #47 turn-in for chained quests.
	public sealed class QuestProgressionService
	{
		// How close (world units, horizontal) the player must get to a ReachArea goal's marker to solve it (#262). The
		// markers are approximate (Shipwrecked's "Enter the jungle" is at 416/701, ~28u off the spawn->Saddur path), so
		// this is generous enough to fire while walking past yet far enough (94u) not to trip at the spawn.
		private const float ReachRadius = 40f;
		private readonly IChatServerDispatcher _agents;
		private readonly IDatabaseRepository _database;

		// The inventory half of a hand-in: a Deliver goal TAKES what it hands over. Reached through the economy port
		// rather than the listener itself, like every other cross-cutting outbound op (CLAUDE.md "narrow port").
		private readonly IEconomyOutbound _economy;
		private readonly ILogger _logger;
		private readonly IQuestOutbound _outbound;
		private readonly QuestRewardService _rewards;
		private readonly QuestHandlerService _questHandler;

		public QuestProgressionService(IDatabaseRepository database, IQuestOutbound outbound, IEconomyOutbound economy, QuestHandlerService questHandler,
			IChatServerDispatcher agents, QuestRewardService rewards, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
			_economy = economy;
			_questHandler = questHandler;
			_agents = agents;
			_rewards = rewards;
			_logger = logger;
		}

		// ACCEPT a quest for the player: persist it ACTIVE with its first objective seeded, and return the definition so
		// the caller can render it. Returns null when the player already holds it (accepting twice must not re-grant) or
		// it has no definition - the state is persisted either way, there is just nothing to draw.
		//
		// Lives here rather than on the dialogue service because a grant has two callers now: a conversation node that
		// hands the quest over, and a team-mate's accepted quest SHARE (#101). `source` is for the log line only.
		public async Task<QuestDefinition?> Grant(INetworkClient client, uint questId, string source)
		{
			var character = client.Session.Character;

			var existing = await _database.GetCharacterQuests(character.Id);
			if (existing.Any(q => q.QuestId == questId))
				return null; // already accepted (active) or completed - don't re-grant

			var def = await _database.GetQuestDefinition(questId);

			// Seed the quest's first objective so kills can advance it (#47): GoalRequired = the goal record's decoded
			// count (QuestGoalDefinition.RequiredCount), GoalTargetName = the seeded kill target (QuestGoalDefinition.
			// TargetName - server-side, sourced per quest since it's not on the wire). With both set, AdvanceKillGoals
			// matches a kill of that NPC and advances/completes the quest (e.g. Casilda's 13479 = kill 1 "Scavenger
			// Tuut"). A talk/collect goal leaves TargetName null - no kill matching, count 0.
			var firstGoal = def == null ? null : QuestProgress.InPlayOrder(def.Goals).FirstOrDefault();
			var goalRequired = firstGoal is { RequiredCount: > 0 }
				? firstGoal.RequiredCount
				: 0u;
			var goalTargetName = firstGoal?.TargetName;

			await _database.SetCharacterQuest(character.Id, questId, QuestState.Active, goalRequired: goalRequired, goalTargetName: goalTargetName);
			_logger.LogInformation("Quest {QuestId} granted to {Name} from {Source} (goalRequired={Req}, target={Target}).", questId, character.Name, source, goalRequired,
				goalTargetName ?? "-");
			return def;
		}

		// The two retail grant packets in order: the QuestLogList (populates the J-panel - all active quests, the
		// just-granted one tracked first) then the QuestFullUpdate (that quest's detail). Order AND presence both
		// matter - the list is what makes the quest appear in the log at all (decoded from the grant capture).
		public async Task SendGrantPackets(INetworkClient client, QuestDefinition grantDef)
		{
			var character = client.Session.Character;
			var active = await _database.GetCharacterQuests(character.Id, QuestState.Active);
			var ids = new List<uint>
			{
				grantDef.QuestId
			};
			ids.AddRange(active.Where(q => q.QuestId != grantDef.QuestId).Select(q => q.QuestId));

			await _outbound.SendQuestLogList(client, grantDef, ids);
			await _outbound.SendQuestFullUpdate(client, grantDef);
		}

		// A kill of the NPC (`templateId` = its NpcTemplateId, `npcName` = its name) by the player: advance any active
		// kill goal targeting it. Matched primarily by the STABLE template id (the goal's TrailingRefId), with the name
		// as a fallback for group-kill goals - see QuestProgress.MatchingKillGoal (#262). templateId is nullable (null =
		// no type id known, match by name only).
		public Task AdvanceKillGoals(INetworkClient client, uint? templateId, string npcName)
		{
			return Advance(client, QuestGoalType.Kill, npcName, templateId);
		}

		// The player RECEIVED `grantedItemId` out of `sourceNpcName`'s corpse bag: advance the loot-from-NPC goal that
		// asks for exactly that item from exactly that NPC. Called per granted item, not per loot action - the goal is
		// satisfied by HOLDING the quest item, not by having opened the bag it was in, so a take that failed to reach the
		// backpack (full bag - the item goes back) advances nothing.
		public Task AdvanceLootGoals(INetworkClient client, string sourceNpcName, uint grantedItemId, uint? sourceTemplateId = null)
		{
			return Advance(client, QuestGoalType.LootFromNpc, sourceNpcName, grantedItemId, npcTemplateId: sourceTemplateId);
		}

		// The player opened `npcName`'s greeting: advance any active talk goal targeting it (e.g. freeing Casilda).
		// `npcTemplateId` is the greeted NPC's stable type (#521); the name stays for goals that have no resolved
		// template - the greeter may be on a map whose creatures we have not imported.
		public Task AdvanceTalkGoals(INetworkClient client, string npcName, uint? npcTemplateId = null)
		{
			return Advance(client, QuestGoalType.TalkToNpc, npcName, npcTemplateId: npcTemplateId);
		}

		// The escort reached its destination: complete any active Escort goal for the escortee TEMPLATE (#521/#523).
		//
		// Retail ends the Tortage escort when the player USES THE CITY DOOR - decoded from both beach-to-city captures:
		// c2s UseObject -> QuestGoal 12407/3544 1/1 -> QuestIIR Active then Completed, and only THEN does Casilda's
		// farewell open. So the door is the trigger and the conversation is a consequence, not the cause.
		//
		// Which door is data-driven, not hard-coded: the object that opens the escortee's server-opened farewell is the
		// object the escort was leading to (ConversationNode.TriggerWorldObjectId), so the caller resolves the NPC from
		// the used object and this just completes the goal.
		//
		// #646: matched by the escortee's TEMPLATE id, which is what an Escort goal's TrailingRefId actually holds
		// (Casilda 9411 on quest 12407 - the only escort in the shipped data, and it resolves to a real NpcTemplate).
		// That is NOT true of the other name-targeted goal types: a TalkToNpc goal's TrailingRefId is the item the
		// player hands over, and a LootFromNpc goal carries none at all, so those two still match by name (see
		// ConversationService.TryReplayConversation and QuestProgress.MatchingLootGoal).
		public Task AdvanceEscortGoals(INetworkClient client, uint npcTemplateId)
		{
			return Advance(client, QuestGoalType.Escort, null!, npcTemplateId);
		}

		// The player's rank in the named faction, or NULL when that cannot be answered - which is the case today,
		// because the faction/reputation system does not exist yet (Gitea #310).
		//
		// ⚠️ This is the ONE place that changes when it does. Null means "not met" in QuestProgress.PreconditionMet, so
		// the three rank-gated goals stay inert exactly as they are now, and start working the moment this can answer -
		// no goal has to be re-wired and no capture re-harvested. Deliberately a seam, not a TODO buried in a caller.
		private static uint? ResolveFactionRank(string factionTitle)
		{
			return null;
		}

		// Whether the player is CURRENTLY escorting the NPC TEMPLATE - an active quest with an unfinished Escort goal
		// pointing at it (the goal's TrailingRefId, #646).
		//
		// The caller needs this BEFORE advancing, to tell the one door use that ends an escort from every later use of
		// the same door: the farewell plays once, and the zone line behaves normally afterwards.
		public async Task<bool> HasActiveEscortGoal(uint characterId, uint npcTemplateId)
		{
			foreach (var (quest, def) in await _database.GetCharacterQuestsWithDefinitions(characterId, QuestState.Active))
			{
				var goal = def.Goals.FirstOrDefault(g => g.GoalType == QuestGoalType.Escort && g.TrailingRefId == npcTemplateId);
				if (goal == null)
					continue;

				if (await GoalProgressOf(characterId, quest, def, goal) < GoalRequired(quest, def, goal))
					return true;
			}

			return false;
		}

		// #262: the ReachArea goals the player still needs to reach, as in-memory targets for the move-handler proximity
		// check (WorldEntity.PendingReachGoals). Loaded ONCE at first move so the per-move test hits no DB; skips goals
		// already solved (a relog past the area doesn't re-fire) and every non-ReachArea goal. A ReachArea goal has no
		// TargetName - its objective is the (MapId, MarkerX, MarkerY) point, which IS the objective's world location.
		public async Task<List<ReachGoalTarget>> LoadReachGoals(uint characterId)
		{
			var targets = new List<ReachGoalTarget>();
			foreach (var (quest, def) in await _database.GetCharacterQuestsWithDefinitions(characterId, QuestState.Active))
			{
				foreach (var goal in def.Goals.Where(g => g.GoalType == QuestGoalType.ReachArea))
				{
					var required = GoalRequired(quest, def, goal);
					if (await GoalProgressOf(characterId, quest, def, goal) >= required)
						continue; // already reached

					// ⚠️ A reach goal's whole target IS its pin, so a PLACEHOLDER marker makes it solvable by standing
					// in the corner of the map (#522). `(50, 50)` is on nine different playfields in the shipped data
					// and `(1, 1)` on three, which is not what a place looks like - see QuestProgress. A goal carrying
					// the client's real trigger REGION is exempt: the volume is the target then, and the marker is
					// only a map pin.
					if (goal.Region == null && !QuestProgress.HasReachablePin(goal))
					{
						_logger.LogDebug("Quest {Quest} goal {Goal} is typed ReachArea but has no usable marker ({X}/{Y} on map {Map}); not tracked.", quest.QuestId, goal.GoalId,
							goal.MarkerX, goal.MarkerY, goal.MapId);
						continue;
					}

					// The client's real trigger volume where the goal carries one (#521), else the marker radius. The
					// region's own MapId wins when the goal has none: goal 14073 carries no map at all, and its
					// region (`BossRoom`) is what says which playfield it is on.
					targets.Add(new ReachGoalTarget(quest.QuestId, goal.GoalId, goal.Region?.MapId ?? goal.MapId ?? 0, goal.MarkerX ?? 0, goal.MarkerY ?? 0,
						ReachRadius * ReachRadius, goal.Region));
				}
			}

			return targets;
		}

		// #356: solve goal `goalId` directly (the reach-a-spot / proximity path in GameServerListener.World.cs). Finds
		// the active quest that OWNS `goalId` (primary or secondary, #357) and jumps that goal to its required count; the
		// quest completes only once every goal is done (no target-name match: reaching the spot already decided it's
		// solved). Advances scripted objectives without bespoke per-quest C#.
		public async Task SolveGoal(INetworkClient client, uint goalId)
		{
			var character = client.Session.Character;
			if (character == null || goalId == 0)
				return;

			var changed = false;
			foreach (var (quest, def) in await _database.GetCharacterQuestsWithDefinitions(character.Id, QuestState.Active))
			{
				var goal = def.Goals.FirstOrDefault(g => g.GoalId == goalId);
				if (goal == null)
					continue;

				var required = GoalRequired(quest, def, goal);
				if (await GoalProgressOf(character.Id, quest, def, goal) >= required)
					continue; // already solved

				changed = true;
				await PersistGoalProgress(character.Id, quest, def, goal, required); // solve = jump to required
				if (await AllGoalsComplete(character.Id, quest, def, goal, required))
					await CompleteObjective(client!, character, quest, def!, goal, required);
				else
					await SendGoalUpdate(client!, quest.QuestId, goal.GoalId, true); // this goal done, others pending -> Satisfied + Advanced

				_logger.LogInformation("Quest {Quest} goal {Goal} solved (reach) for {Char}.", quest.QuestId, goalId, character.Name);
			}

			if (changed)
				await _outbound.RefreshQuestVisibility(client!);
		}

		// #262: the player interacted with a quest-pickup world object (a QuestItemEffect dynel, e.g. a quarry stone
		// brick). Returns the item the node should hand over - the TrailingRefId of the first active, still-incomplete
		// CollectItem goal - or null when no collect quest is active, which is what makes the brick nodes inert for a
		// player without Shipwrecked V. One item per call.
		//
		// ⚠️ RESOLVE ONLY - it deliberately does NOT advance the goal. The caller grants the item, and the GRANT is
		// what ticks the goal now (AdvanceCollectGoals, off GrantItemToBackpack). Advancing here as well double-counted
		// every pickup.
		public async Task<uint?> ResolveCollectPickupItem(INetworkClient client)
		{
			var character = client.Session.Character;

			foreach (var (quest, def) in await _database.GetCharacterQuestsWithDefinitions(character.Id, QuestState.Active))
			{
				var goal = def.Goals.FirstOrDefault(g => g.GoalType == QuestGoalType.CollectItem);
				if (goal == null || goal.TrailingRefId == 0)
					continue;

				if (await GoalProgressOf(character.Id, quest, def, goal) >= GoalRequired(quest, def, goal))
					continue; // this collect goal is already full

				return goal.TrailingRefId;
			}

			return null;
		}

		// An item reached the player's inventory - advance any active CollectItem goal asking for it. Fired from the
		// one grant choke point (GrantItemToBackpack), so it covers EVERY way an item arrives: a quest-pickup node, a
		// mob's corpse bag, a vendor purchase, a harvest, a claim, `.additem`.
		//
		// That is the point: a gather objective is "hold N of item X", and the client's own quest data says nothing
		// about HOW the item is obtained - the goal's typed reference (LDB category 50200) names an item and stops
		// there. Wiring per-source triggers instead would mean guessing loot-vs-pickup-vs-bought per quest, and a
		// wrong guess makes the quest silently un-completable. Counting arrivals needs no such guess.
		public Task AdvanceCollectGoals(INetworkClient client, uint itemTemplateId, uint count = 1)
		{
			// `count` is the stack size granted; a stack of 3 advances a collect goal by 3.
			return Advance(client, QuestGoalType.CollectItem, null!, itemTemplateId, Math.Max(1u, count));
		}

		// The player USED an item out of a bag - advance any active UseItem goal asking for it (#522). Fired from
		// `UseInventoryItem`, the one right-click choke point, AFTER the item's own effect has been applied, so an
		// item that is both a consumable and a quest objective does both.
		//
		// One use is one tick regardless of the stack: a stack of five vials is five uses, not one worth five.
		public Task AdvanceUseGoals(INetworkClient client, uint itemTemplateId)
		{
			return Advance(client, QuestGoalType.UseItem, null!, itemTemplateId);
		}

		// Advance the goal of `triggerType`/`targetName` for each of the player's active quests. #357: a quest can have
		// several goals, so the trigger ticks the MATCHING goal (not always goal[0]); the quest only completes once EVERY
		// goal has reached its required count. A tick that leaves the quest incomplete (this goal not yet done, or a
		// sibling goal still pending) just refreshes the HUD and stays Active; a complete quest supersedes to the next
		// stage, or (chainless kill) awaits turn-in at the giver - the legacy #47 path.
		// `step` is how much a single trigger is worth - 1 for a kill/talk/loot, but a granted STACK advances a collect
		// goal by its whole count (picking up 3 bricks at once is 3 of the 3).
		//
		// ⚠️ TWO id parameters, because `targetTemplateId` does not mean one thing across the trigger types: it is the
		// killed/escorted NPC for Kill and Escort, but the ITEM for Collect and Loot. `npcTemplateId` is always the NPC
		// (#521) - the bag's source creature on a loot, the greeted creature on a talk - so the loot path can match on
		// both halves of what it knows. Kill and Escort leave it null; their NPC is already in the other slot.
		private async Task Advance(INetworkClient client, QuestGoalType triggerType, string targetName, uint? targetTemplateId = null, uint step = 1,
			uint? npcTemplateId = null)
		{
			var character = client.Session.Character;
			if (character == null || (string.IsNullOrEmpty(targetName) && targetTemplateId is null))
				return;

			// Snapshot the quests up front: CompleteAndSupersede writes the next stage Active, but the captured chain
			// never re-matches the same trigger on the just-granted stage, so iterating the snapshot is safe.
			var changed = false;
			foreach (var (quest, def) in await _database.GetCharacterQuestsWithDefinitions(character.Id, QuestState.Active))
			{
				// Match the goal to advance: a kill by the STABLE target template id, name as fallback; a collect by its
				// item id (TrailingRefId); an ESCORT by the escortee's template (TrailingRefId, #646); a LOOT by the bag's
				// source NPC AND the item received; a TALK by the greeted NPC - #262.
				//
				// #521: the loot and talk paths now pass the NPC's TEMPLATE alongside its name, so they match the same
				// way the kill path always has - id first, name only where the goal has no resolved template. A talk
				// goal's own TrailingRefId is no help there (it is the hand-over ITEM, not the NPC); the resolved
				// `TargetTemplateId` column is what gives those two an id to match on at all.
				var goal = triggerType switch
				{
					QuestGoalType.Kill        => QuestProgress.MatchingKillGoal(def.Goals, targetTemplateId, targetName),
					QuestGoalType.CollectItem => QuestProgress.MatchingCollectGoal(def.Goals, targetTemplateId ?? 0),
					QuestGoalType.UseItem     => QuestProgress.MatchingUseGoal(def.Goals, targetTemplateId ?? 0),
					QuestGoalType.LootFromNpc => QuestProgress.MatchingLootGoal(def.Goals, targetName, targetTemplateId ?? 0, npcTemplateId),
					QuestGoalType.Escort      => QuestProgress.MatchingEscortGoal(def.Goals, targetTemplateId ?? 0),
					_                         => QuestProgress.MatchingGoal(def.Goals, triggerType, targetName, npcTemplateId)
				};
				if (goal == null)
					continue;

				// #521: a stated PRECONDITION must hold before the goal can advance. Enforced HERE, at the trigger, rather
				// than by refusing to wire the goal: the level check works today, and the faction-rank one starts working
				// the day #310 gives ResolveFactionRank a real answer - with no re-harvest and no re-wiring.
				if (!QuestProgress.PreconditionMet(goal, character.Level, ResolveFactionRank))
				{
					await _agents.SendSystemMessage(character.Id, goal.RequiredLevel is { } lvl
						? $"You must be level {lvl} for this."
						: "You are not of high enough standing for this yet.");
					continue;
				}

				// #521: a DELIVER goal is a HAND-IN, so the player must actually be CARRYING what it hands over.
				// Without this the talk alone solves it and "deliver a supply crate to Croiss" completes by walking up
				// empty-handed - the goal would be wired but not really played.
				if (!await HoldsDeliveryItem(character.Id, goal))
				{
					// Say so rather than no-op: the player IS on the right quest talking to the right NPC, so silence
					// reads as a broken quest. This only fires once the goal has already matched.
					await _agents.SendSystemMessage(character.Id, "You are not carrying what this delivery needs.");
					continue;
				}

				var required = GoalRequired(quest, def, goal);
				// A talk goal is binary - talking to the NPC SOLVES it (jumps to required), regardless of the display
				// count. This lets a "deliver N to X" hand-in (e.g. Shipwrecked V's "deliver 3 bricks to Turach",
				// RequiredCount 3 for the captured objective text) complete in one talk. Every other trigger is +1.
				var current = await GoalProgressOf(character.Id, quest, def, goal);
				if (current >= required)
					continue; // already complete (a re-fired trigger on a satisfied objective)

				var progress = triggerType == QuestGoalType.TalkToNpc
					? required
					: Math.Min(required, current + step);

				// The hand-in TAKES the goods. Placed AFTER the `current >= required` re-fire guard above, so talking to
				// the recipient a second time cannot take a second set. Nothing in the corpus shows the removal - retail
				// completes the delivery and moves on without a captured DispatchRemoveItem - so this is the maintainer's call
				// (2026-08-01) rather than a decoded fact: a delivery the player walks away from still holding is not a
				// delivery. HoldsDeliveryItem has already verified the count is present.
				if (QuestProgress.IsDelivery(goal))
					await ConsumeDeliveryItems(client!, character.Id, goal, required);

				changed = true;
				await PersistGoalProgress(character.Id, quest, def, goal, progress);

				// #528: the goal's FIRST advance can spring an ambush - the Quarry Bat Demon on the first of three
				// stone bricks. First only: the captured session picks up all three and gets exactly one Bat Demon, so
				// the gate is `current == 0` and not "every tick". Persisted progress is what makes it survive a relog
				// (a player who already holds a brick does not get ambushed again).
				if (current == 0 && await _database.GetNpcAmbushForGoal(def.QuestId, goal.GoalId) is { } ambush)
					_outbound.ScheduleQuestAmbush(client!, ambush);

				// The whole quest is done only when THIS goal hit its count AND every other goal is already complete.
				if (progress >= required && await AllGoalsComplete(character.Id, quest, def, goal, progress))
				{
					await CompleteObjective(client!, character, quest, def, goal, required);
					continue;
				}

				// Objective tick (e.g. 2/5 kills, or one of several goals done): refresh the HUD, leave the quest Active.
				// A tick that REACHES the required count says Satisfied even though the quest as a whole is not done -
				// that is what retail sends on the third of three stone bricks (#525), and it closes with Advanced.
				await SendGoalUpdate(client!, quest.QuestId, goal.GoalId, progress >= required);
				await _agents.SendSystemMessage(character.Id, $"Objective {progress}/{required}.");
			}

			// #261: a stage change can un-gate or re-gate NPCs (e.g. completing Casilda's kill stage despawns Tuut) -
			// re-run the player's interest pass so gated visibility tracks the new quest state immediately.
			if (changed)
				await _outbound.RefreshQuestVisibility(client!);
		}

		// The other half of the hand-in (#521): HoldsDeliveryItem checks, this TAKES. `required` is the count the goal asks for,
		// which is what the check demanded, so it is what gets handed over - extras the player is carrying stay theirs.
		//
		// ⚠️ Nothing in the corpus shows the removal (retail completes the delivery without a captured
		// DispatchRemoveItem in the frames around it), so this is a design decision - the maintainer's, 2026-08-01 - and not a
		// decoded fact: a delivery you walk away from still holding the goods is not a delivery. Recorded here because
		// the day a capture shows otherwise, this is the one place to change.
		//
		// A short take is LOGGED rather than tolerated silently: the caller has already verified the player holds
		// enough, so removing fewer means the inventory moved underneath us. The goal still advances - refusing to
		// complete a quest the player has legitimately finished is the worse failure of the two.
		private async Task ConsumeDeliveryItems(INetworkClient client, uint characterId, QuestGoalDefinition goal, uint required)
		{
			var wanted = Math.Max(1u, required);
			// The item comes from QuestProgress.DeliveryItemId, not from the wire field directly: a delivery resolved
			// from the client's catalog sentence names its item in QuestItemId and has no trailing reference at all.
			var itemId = QuestProgress.DeliveryItemId(goal);
			var taken = await _economy.TakeItemsFromInventory(client, itemId, wanted);
			if (taken < wanted)
			{
				_logger.LogWarning("Hand-in of goal {Goal}: took {Taken} of {Wanted} x item {Item} from character {Character} - the delivery check passed, so the inventory changed mid-hand-in.",
					goal.GoalId, taken, wanted, itemId, characterId);
			}
		}

		// Whether the player is carrying what a DELIVER goal hands over; the rule itself is QuestProgress.CanHandIn and
		// this only supplies the count. ConsumeDeliveryItems is what takes them once the hand-in goes through.
		private async Task<bool> HoldsDeliveryItem(uint characterId, QuestGoalDefinition goal)
		{
			if (!QuestProgress.IsDelivery(goal))
				return true;

			// Summed across slots: a stack can be split over several cells, and a split stack is still the items.
			var itemId = QuestProgress.DeliveryItemId(goal);
			var held = (await _database.GetCharacterItems(characterId)).Where(i => i.TemplateId == itemId).Aggregate(0u, (sum, i) => sum + Math.Max(1u, i.Count));
			return QuestProgress.CanHandIn(goal, held);
		}

		// Whether every goal of the quest has now reached its required count. `justTicked`/`justProgress` is the goal we
		// just wrote this pass (read back from the argument rather than the DB, since the write may not be visible yet on
		// a fresh short-lived context). All other goals are read from their store (primary scalar / secondary rows).
		private async Task<bool> AllGoalsComplete(uint characterId, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition justTicked, uint justProgress)
		{
			var primary = QuestProgress.InPlayOrder(def.Goals).FirstOrDefault();
			var rows = await _database.GetCharacterQuestGoals(characterId, quest.QuestId);
			return QuestProgress.AllGoalsComplete(def.Goals, g =>
			{
				if (g.GoalId == justTicked.GoalId)
					return justProgress;
				if (primary != null && g.GoalId == primary.GoalId)
					return quest.GoalProgress;

				return rows.FirstOrDefault(r => r.GoalId == g.GoalId)?.Progress ?? 0;
			});
		}

		// Complete stage N and supersede it with stage N+1: the captured wire sequence + the state flip (mark N
		// Completed, grant N+1 Active with its goal seeded). The next stage's QuestFullUpdate is what makes the client
		// swap the displayed quest in place (no QuestLogList - the supersession reuses the existing log slot, fr1356).
		private async Task CompleteAndSupersede(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def, uint goalId)
		{
			var required = Math.Max(quest.GoalRequired, 1);
			await SendStageCompletion(client, quest.QuestId, goalId);
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Completed, required, required, quest.GoalTargetName);

			// Reached only via CompleteObjective's `NextQuestId is not null` guard, so the successor id is present.
			var nextQuestId = def.NextQuestId!.Value;
			var nextDef = await _database.GetQuestDefinition(nextQuestId);
			var nextGoal = nextDef == null ? null : QuestProgress.InPlayOrder(nextDef.Goals).FirstOrDefault();
			await _database.SetCharacterQuest(character.Id, nextQuestId, QuestState.Active, 0, nextGoal?.RequiredCount ?? 0, nextGoal?.TargetName);
			if (nextDef != null)
				await _outbound.SendQuestFullUpdate(client, nextDef);

			// #267: if the next stage is "talk to NPC X" (e.g. 13563 loot -> 13480 talk Casilda), push X's '?' marker
			// now. The supersession happens away from X (looting Tuut's corpse), so without this her '?' wouldn't appear
			// until the client re-polls/re-streams her - and she's often still in view, so that never fires.
			if (nextGoal?.GoalType == QuestGoalType.TalkToNpc && (!string.IsNullOrEmpty(nextGoal.TargetName) || nextGoal.TargetTemplateId is > 0))
				await _outbound.RefreshQuestMarkerFor(client, nextGoal.TargetName!, nextGoal.TargetTemplateId);

			// #262: the new stage may carry a ReachArea goal (Shipwrecked II "find the gate key", III "enter the city")
			// - drop the player's once-lazy reach cache so the next move reloads it and starts watching the new area.
			_outbound.InvalidateReachGoals(client);

			// #262: a new ESCORT stage means the escortee becomes the player's companion and follows them (the world
			// tick steps her along). DATA-DRIVEN off the Escort goal - resolved by the escortee's TEMPLATE id
			// (TrailingRefId, e.g. Casilda 9411), not a hard-coded name; the player's world handle is its ClientInstance.
			if (nextGoal is { GoalType: QuestGoalType.Escort, TrailingRefId: not 0 })
			{
				_outbound.SetNpcFollowByTemplate(character.MapId, nextGoal.TrailingRefId, character.Id, true);
				// After-quest world effects now that the escort is active - e.g. the beach chain-wall collision drops
				// (data-driven off WorldObject.EffectQuestId; map-scoped, so it no-ops off the beach). Then clear the
				// escortee's captive struggle pose so she stands/walks as she follows.
				await _outbound.ApplyQuestWorldEffects(client);
				await _questHandler.ClearCasildaCaptivePose(client);
			}

			_logger.LogInformation("Quest {Quest} completed for {Char}; superseded by {Next} (goal {Type}, target {Target}).", quest.QuestId, character.Name, nextQuestId, nextGoal?.GoalType,
				nextGoal?.TargetName ?? "-");
			await _agents.SendSystemMessage(character.Id, "Quest updated.");
		}

		// The final stage of a chain (or a chainless loot/talk goal) - complete it outright, no successor to grant.
		private async Task CompleteFinal(INetworkClient client, Character character, CharacterQuest quest, uint goalId, uint required)
		{
			await SendStageCompletion(client, quest.QuestId, goalId);
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Completed, required, required, quest.GoalTargetName);
			_logger.LogInformation("Quest {Quest} completed for {Char} (chain end).", quest.QuestId, character.Name);
			await _agents.SendSystemMessage(character.Id, "Quest complete!");
		}

		// The objective for `quest`'s goal has hit its required count: complete the stage. Shared by the trigger path
		// (Advance) and the Lua-solve path (SolveGoal) - the completion is identical, only how the objective is REACHED
		// differs. Supersede to the next stage, hold for turn-in (legacy #47 chainless kill), or complete outright, then
		// run the goal's completion side-effects. Keyed off the GOAL (its type + target), not the trigger, so both callers
		// share one code path. Does NOT run RefreshQuestVisibility - the caller does that once after its loop.
		//
		// The completion packet names the goal that JUST completed, not `def.GoalId`. On a single-goal quest those are
		// the same id, which is why the difference went unseen; on a multi-goal quest retail closes 12371 on g3497,
		// 10238 on g3490 and 10287 on g7363 - the LAST goal - while `def.GoalId` holds the quest's head goal (3495,
		// 1448, 400). [V2: same four captures as the Advanced pairs above]
		private async Task CompleteObjective(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition goal, uint required)
		{
			if (def.NextQuestId is not null)
				await CompleteAndSupersede(client, character, quest, def, goal.GoalId); // the escort follow / beach wall fire from here when the NEXT stage is an Escort
			else if (goal.GoalType == QuestGoalType.Kill)
				await ObjectiveCompleteAwaitTurnIn(client, character, quest, goal.GoalId, required); // legacy #47 (chainless kill)
			else
				await CompleteFinal(client, character, quest, goal.GoalId, required); // chainless loot/talk -> just complete

			// #815: a quest that offers a CHOICE of rewards presents it now. Deliberately NOT on the turn-in path above
			// - that one leaves the quest Active awaiting a hand-in, and the reward belongs to completion. The capture
			// puts the offer inside the completion burst, after the QuestIIR state flip, which is where this lands.
			if (goal.GoalType != QuestGoalType.Kill || def.NextQuestId is not null)
				await _rewards.OfferOnCompletion(client, quest.QuestId);
		}

		private async Task<uint> GoalProgressOf(uint characterId, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition goal)
		{
			if (IsPrimaryGoal(def, goal))
				return quest.GoalProgress;

			var rows = await _database.GetCharacterQuestGoals(characterId, quest.QuestId);
			return rows.FirstOrDefault(r => r.GoalId == goal.GoalId)?.Progress ?? 0;
		}

		// Legacy #47 path for a CHAINLESS kill quest: the kill finishes the objective but the quest stays Active and is
		// marked Completed when the player turns it in at the giver (ConversationService.TryTurnInQuests). The '?' giver
		// marker then appears. Preserved so a future standalone kill-and-return quest still works; the Casilda chain
		// (every stage has a successor) never reaches here.
		private async Task ObjectiveCompleteAwaitTurnIn(INetworkClient client, Character character, CharacterQuest quest, uint goalId, uint required)
		{
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Active, required, quest.GoalRequired, quest.GoalTargetName);
			await _outbound.SendQuestGoal(client, quest.QuestId, goalId, QuestGoalUpdate.Satisfied);
			await _outbound.SendQuestIir(client, quest.QuestId, QuestCodec.StateActive);
			await _agents.SendSystemMessage(character.Id, $"Objective complete ({required}/{required}). Return to the quest giver.");
		}

		private async Task PersistGoalProgress(uint characterId, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition goal, uint progress)
		{
			if (IsPrimaryGoal(def, goal))
				await _database.SetCharacterQuest(characterId, quest.QuestId, QuestState.Active, progress, quest.GoalRequired, quest.GoalTargetName);
			else
				await _database.SetCharacterQuestGoal(characterId, quest.QuestId, goal.GoalId, progress);
		}

		// The HUD update for a goal that moved but did NOT finish the whole quest. Retail has exactly two shapes here,
		// and NEITHER carries a QuestIIR - the journal state flip (Active 2 -> Completed 4) belongs to completion only
		// (SendStageCompletion):
		//
		//   still short of the count  ->  QuestGoal(Progress)
		//   count reached, quest not done  ->  QuestGoal(Satisfied), QuestGoal(Advanced)
		//
		// Advanced (#527) is the hand-off marker on the goal being LEFT BEHIND: it rides in the same frame as that
		// goal's own Satisfied, and the SUCCESSOR goal's Satisfied follows whenever it is actually solved (up to eight
		// minutes later in the corpus) - so it is not "the next goal opened", it is "this one closed".
		//
		// [V2: all 10 captured Advanced sightings are that pair - 10238 g1448, 12369 g7134, 12371 g3495, 10287 g400
		// across Hoxxling_mage-heraldofxotli_login_gameplay{,2}, xxx_mage-{demo,necromancer}_login_play*,
		// loot-drag-1538. The last goal of a quest (10238 g3490, 12369 g3492, 12371 g3497, 10287 g7363) sends
		// Satisfied then QuestIIR 2/4 and no Advanced. No QuestIIR appears in any Progress/Advanced frame.]
		private async Task SendGoalUpdate(INetworkClient client, uint questId, uint goalId, bool satisfied)
		{
			await _outbound.SendQuestGoal(client, questId, goalId, satisfied
				? QuestGoalUpdate.Satisfied
				: QuestGoalUpdate.Progress);

			if (satisfied)
				await _outbound.SendQuestGoal(client, questId, goalId, QuestGoalUpdate.Advanced);
		}

		// The shared head of every completion: QuestGoal (the goal is met) then QuestIIR active(2) -> completed(4).
		private async Task SendStageCompletion(INetworkClient client, uint questId, uint goalId)
		{
			await _outbound.SendQuestGoal(client, questId, goalId, QuestGoalUpdate.Satisfied);
			await _outbound.SendQuestIir(client, questId, QuestCodec.StateActive);
			await _outbound.SendQuestIir(client, questId, QuestCodec.StateCompleted);
		}

		private static uint GoalRequired(CharacterQuest quest, QuestDefinition def, QuestGoalDefinition goal)
		{
			return Math.Max(IsPrimaryGoal(def, goal)
				? quest.GoalRequired
				: goal.RequiredCount, 1);
		}

		// --- Per-goal progress plumbing (#357) --------------------------------------------------------------------
		// The PRIMARY (lowest-Sequence) goal lives in the scalar CharacterQuest fields (unchanged, single-goal-exact);
		// SECONDARY goals live in CharacterQuestGoal rows. These helpers route to the right store by goal identity.

		private static bool IsPrimaryGoal(QuestDefinition def, QuestGoalDefinition goal)
		{
			var primary = QuestProgress.InPlayOrder(def.Goals).FirstOrDefault();
			return primary != null && primary.GoalId == goal.GoalId;
		}

		// #262: solve any ReachArea quest goal the player has just walked into range of. Called from the move handler
		// on every accepted player move; the goal targets are cached on the player WorldEntity (lazily loaded once, so
		// this stays a pure in-memory distance test with no DB hit until a goal actually fires). A solved target is
		// removed so it fires exactly once; SolveGoal handles the tick / completion / chain supersession.
		//
		// It lives here rather than in the listener's move handler because deciding WHETHER a goal is reached is quest
		// logic, and both halves it needs - LoadReachGoals and SolveGoal - are this service's own.
		public async Task CheckReachGoals(INetworkClient client, WorldEntity player, Vector3 position)
		{
			var pending = player.PendingReachGoals ??= await LoadReachGoals(client.Session.Character.Id);
			if (pending.Count == 0)
				return;

			// Snapshot the in-range targets BEFORE solving: SolveGoal can complete a stage whose supersession calls
			// InvalidateReachGoals (PendingReachGoals -> null, reloaded next move), so we must not index the live list
			// across a solve. Removes are null-safe; a target solved after an invalidation is simply excluded from the
			// reload (LoadReachGoals skips completed goals), so no double-fire.
			var reached = pending.Where(t => t.MapId == player.MapId && ReachedTarget(t, position)).ToList();
			foreach (var target in reached)
			{
				player.PendingReachGoals?.Remove(target);
				_logger.LogInformation("#262: player {Char} reached quest {Quest} goal {Goal} area ({X},{Z}).", client.Session.Character.Name, target.QuestId, target.GoalId, target.X, target.Z);
				await SolveGoal(client, target.GoalId);
			}
		}

		// Inside the goal's CLIENT trigger volume where it has one (#521) - the full prism test, so a goal region
		// stacked over another does not fire from the wrong floor - else within the marker radius (the #262
		// fallback, a horizontal circle around the goal's map pin).
		private static bool ReachedTarget(ReachGoalTarget t, Vector3 position)
		{
			return t.Region != null
				? EnvironmentService.Contains(t.Region, position)
				: (position.X - t.X) * (position.X - t.X) + (position.Z - t.Z) * (position.Z - t.Z) <= t.RadiusSq;
		}
	}
}
