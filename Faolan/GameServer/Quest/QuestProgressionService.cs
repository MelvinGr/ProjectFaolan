using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Server.ChatServer;
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
		private readonly IChatServerDispatcher _agents;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly IQuestOutbound _outbound;
		private readonly QuestHandlerService _questHandler;

		public QuestProgressionService(IDatabaseRepository database, IQuestOutbound outbound, QuestHandlerService questHandler, IChatServerDispatcher agents, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
			_questHandler = questHandler;
			_agents = agents;
			_logger = logger;
		}

		// A kill of `npcName` by the player: advance any active kill goal targeting it.
		public Task AdvanceKillGoals(INetworkClient client, string npcName)
		{
			return Advance(client, QuestGoalType.Kill, npcName);
		}

		// The player looted `sourceNpcName`'s corpse bag: advance any active loot-from-NPC goal targeting it. The quest
		// item (the scavenger's key) is RDB content we don't materialise, so the bag's SOURCE NPC name is the match key.
		public Task AdvanceLootGoals(INetworkClient client, string sourceNpcName)
		{
			return Advance(client, QuestGoalType.LootFromNpc, sourceNpcName);
		}

		// The player opened `npcName`'s greeting: advance any active talk goal targeting it (e.g. freeing Casilda).
		public Task AdvanceTalkGoals(INetworkClient client, string npcName)
		{
			return Advance(client, QuestGoalType.TalkToNpc, npcName);
		}

		// #356: a Lua EFFECT script resolved goal `goalId` (player:SolveGoal(goalId)) - the RDB quest engine's way of
		// completing an objective. Finds the active quest that OWNS `goalId` (primary or secondary, #357) and jumps that
		// goal to its required count; the quest completes only once every goal is done (no target-name match: the script
		// already decided it's solved). This closes the Lua effect->quest-state loop so scripted quests advance without
		// bespoke C#.
		public async Task SolveGoal(INetworkClient client, uint goalId)
		{
			var character = client?.Session?.Character;
			if (character == null || goalId == 0)
				return;

			var changed = false;
			foreach (var quest in await _database.GetCharacterQuests(character.Id))
			{
				if (quest.State != QuestState.Active)
					continue;

				var def = await _database.GetQuestDefinition(quest.QuestId);
				var goal = def?.Goals.FirstOrDefault(g => g.GoalId == goalId);
				if (goal == null)
					continue;

				var required = GoalRequired(quest, def, goal);
				if (await GoalProgressOf(character.Id, quest, def, goal) >= required)
					continue; // already solved

				changed = true;
				await PersistGoalProgress(character.Id, quest, def, goal, required); // solve = jump to required
				if (await AllGoalsComplete(character.Id, quest, def, goal, required))
					await CompleteObjective(client, character, quest, def, goal, required);
				else
					await SendGoalUpdate(client, quest.QuestId, goal.GoalId, required); // this goal done, others pending

				_logger.LogInformation("Quest {Quest} goal {Goal} solved by Lua effect for {Char}.", quest.QuestId, goalId, character.Name);
			}

			if (changed)
				await _outbound.RefreshQuestVisibility(client);
		}

		// Advance the goal of `triggerType`/`targetName` for each of the player's active quests. #357: a quest can have
		// several goals, so the trigger ticks the MATCHING goal (not always goal[0]); the quest only completes once EVERY
		// goal has reached its required count. A tick that leaves the quest incomplete (this goal not yet done, or a
		// sibling goal still pending) just refreshes the HUD and stays Active; a complete quest supersedes to the next
		// stage, or (chainless kill) awaits turn-in at the giver - the legacy #47 path.
		private async Task Advance(INetworkClient client, QuestGoalType triggerType, string targetName)
		{
			var character = client?.Session?.Character;
			if (character == null || string.IsNullOrEmpty(targetName))
				return;

			// Snapshot the quests up front: CompleteAndSupersede writes the next stage Active, but the captured chain
			// never re-matches the same trigger on the just-granted stage, so iterating the snapshot is safe.
			var changed = false;
			foreach (var quest in await _database.GetCharacterQuests(character.Id))
			{
				if (quest.State != QuestState.Active)
					continue;

				var def = await _database.GetQuestDefinition(quest.QuestId);
				if (def == null)
					continue;

				var goal = QuestProgress.MatchingGoal(def.Goals, triggerType, targetName);
				if (goal == null)
					continue;

				var required = GoalRequired(quest, def, goal);
				var progress = await GoalProgressOf(character.Id, quest, def, goal) + 1;
				if (progress > required)
					continue; // this goal is already complete (a re-fired trigger on a satisfied objective)

				changed = true;
				await PersistGoalProgress(character.Id, quest, def, goal, progress);

				// The whole quest is done only when THIS goal hit its count AND every other goal is already complete.
				if (progress >= required && await AllGoalsComplete(character.Id, quest, def, goal, progress))
				{
					await CompleteObjective(client, character, quest, def, goal, required);
					continue;
				}

				// Objective tick (e.g. 2/5 kills, or one of several goals done): refresh the HUD, leave the quest Active.
				await SendGoalUpdate(client, quest.QuestId, goal.GoalId, progress);
				await _agents.SendSystemMessage(character.Id, $"Objective {progress}/{required}.");
			}

			// #261: a stage change can un-gate or re-gate NPCs (e.g. completing Casilda's kill stage despawns Tuut) -
			// re-run the player's interest pass so gated visibility tracks the new quest state immediately.
			if (changed)
				await _outbound.RefreshQuestVisibility(client);
		}

		// Whether every goal of the quest has now reached its required count. `justTicked`/`justProgress` is the goal we
		// just wrote this pass (read back from the argument rather than the DB, since the write may not be visible yet on
		// a fresh short-lived context). All other goals are read from their store (primary scalar / secondary rows).
		private async Task<bool> AllGoalsComplete(uint characterId, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition justTicked, uint justProgress)
		{
			var primary = def.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
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
		private async Task CompleteAndSupersede(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def)
		{
			var required = Math.Max(quest.GoalRequired, 1);
			await SendStageCompletion(client, quest.QuestId, def.GoalId, required);
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Completed, required, required, quest.GoalTargetName);

			// Reached only via CompleteObjective's `NextQuestId is not null` guard, so the successor id is present.
			var nextQuestId = def.NextQuestId!.Value;
			var nextDef = await _database.GetQuestDefinition(nextQuestId);
			var nextGoal = nextDef?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
			await _database.SetCharacterQuest(character.Id, nextQuestId, QuestState.Active, 0, nextGoal?.RequiredCount ?? 0, nextGoal?.TargetName);
			if (nextDef != null)
				await _outbound.SendQuestFullUpdate(client, nextDef);

			// #267: if the next stage is "talk to NPC X" (e.g. 13563 loot -> 13480 talk Casilda), push X's '?' marker
			// now. The supersession happens away from X (looting Tuut's corpse), so without this her '?' wouldn't appear
			// until the client re-polls/re-streams her - and she's often still in view, so that never fires.
			if (nextGoal?.GoalType == QuestGoalType.TalkToNpc && !string.IsNullOrEmpty(nextGoal.TargetName))
				await _outbound.RefreshQuestMarkerByName(client, nextGoal.TargetName);

			_logger.LogInformation("Quest {Quest} completed for {Char}; superseded by {Next} (goal {Type}, target {Target}).", quest.QuestId, character.Name, nextQuestId, nextGoal?.GoalType,
				nextGoal?.TargetName ?? "-");
			await _agents.SendSystemMessage(character.Id, "Quest updated.");
		}

		// The final stage of a chain (or a chainless loot/talk goal) - complete it outright, no successor to grant.
		private async Task CompleteFinal(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def, uint required)
		{
			await SendStageCompletion(client, quest.QuestId, def.GoalId, required);
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Completed, required, required, quest.GoalTargetName);
			_logger.LogInformation("Quest {Quest} completed for {Char} (chain end).", quest.QuestId, character.Name);
			await _agents.SendSystemMessage(character.Id, "Quest complete!");
		}

		// The objective for `quest`'s goal has hit its required count: complete the stage. Shared by the trigger path
		// (Advance) and the Lua-solve path (SolveGoal) - the completion is identical, only how the objective is REACHED
		// differs. Supersede to the next stage, hold for turn-in (legacy #47 chainless kill), or complete outright, then
		// run the goal's completion side-effects. Keyed off the GOAL (its type + target), not the trigger, so both callers
		// share one code path. Does NOT run RefreshQuestVisibility - the caller does that once after its loop.
		private async Task CompleteObjective(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition goal, uint required)
		{
			if (def.NextQuestId is not null)
				await CompleteAndSupersede(client, character, quest, def);
			else if (goal.GoalType == QuestGoalType.Kill)
				await ObjectiveCompleteAwaitTurnIn(client, character, quest, def, required); // legacy #47 (chainless kill)
			else
				await CompleteFinal(client, character, quest, def, required); // chainless loot/talk -> just complete

			// #262: talking to Casilda is what FREES her - on that talk-goal completion she becomes the player's
			// escort companion and follows them (the world tick steps her along, retail-style). Keyed by NAME
			// (handles are session-volatile); the player's world-entity handle is its ClientInstance.
			if (goal.GoalType == QuestGoalType.TalkToNpc && string.Equals(goal.TargetName, "Casilda", StringComparison.OrdinalIgnoreCase))
			{
				_outbound.SetNpcFollowByName(character.MapId, "Casilda", client.Session.Account.ClientInstance, true);
				// #267: freeing Casilda drops the beach "invisible wall" - the chain decor's collision (its mesh
				// doesn't render for us, so it reads as an invisible barrier). Retail flips it off with a SimpleDynel
				// visual-change to the chain dynel at this exact moment (Hoxxling gameplay idx 2053).
				await _questHandler.DropBeachWall(client);
			}
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
		private async Task ObjectiveCompleteAwaitTurnIn(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def, uint required)
		{
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Active, required, quest.GoalRequired, quest.GoalTargetName);
			await _outbound.SendQuestGoal(client, quest.QuestId, def.GoalId, required);
			await _outbound.SendQuestIir(client, quest.QuestId, WorldQuest.StateActive);
			await _agents.SendSystemMessage(character.Id, $"Objective complete ({required}/{required}). Return to the quest giver.");
		}

		private async Task PersistGoalProgress(uint characterId, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition goal, uint progress)
		{
			if (IsPrimaryGoal(def, goal))
				await _database.SetCharacterQuest(characterId, quest.QuestId, QuestState.Active, progress, quest.GoalRequired, quest.GoalTargetName);
			else
				await _database.SetCharacterQuestGoal(characterId, quest.QuestId, goal.GoalId, progress);
		}

		// The HUD update for a goal that advanced but did NOT complete the whole quest: the goal counter + a journal
		// active-refresh, leaving the quest Active. (Completion sends the 2->4 sequence via CompleteObjective instead.)
		private async Task SendGoalUpdate(INetworkClient client, uint questId, uint goalId, uint progress)
		{
			await _outbound.SendQuestGoal(client, questId, goalId, progress);
			await _outbound.SendQuestIir(client, questId, WorldQuest.StateActive);
		}

		// The shared head of every completion: QuestGoal (counter at required) then QuestIIR active(2) -> completed(4).
		private async Task SendStageCompletion(INetworkClient client, uint questId, uint goalId, uint required)
		{
			await _outbound.SendQuestGoal(client, questId, goalId, required);
			await _outbound.SendQuestIir(client, questId, WorldQuest.StateActive);
			await _outbound.SendQuestIir(client, questId, WorldQuest.StateCompleted);
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
			var primary = def.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
			return primary != null && primary.GoalId == goal.GoalId;
		}
	}
}
