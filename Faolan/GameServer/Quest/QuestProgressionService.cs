using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Server.AgentServer;
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
	// See docs/handoff-2026-06-21-casilda-quest-chain.md. Replaces the single-stage #47 turn-in for chained quests.
	public sealed class QuestProgressionService
	{
		private readonly IAgentServerDispatcher _agents;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly IQuestOutbound _outbound;

		public QuestProgressionService(IDatabaseRepository database, IQuestOutbound outbound, IAgentServerDispatcher agents, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
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

		// Advance every active goal of `triggerType` whose target matches `targetName` for the player's quests. Each
		// match ticks the objective; reaching the required count completes the stage (supersede to the next, or, for a
		// chainless kill quest, leave it Active awaiting turn-in at the giver - the legacy #47 path).
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
				var goal = def?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
				if (!QuestProgress.Advances(quest, goal, triggerType, targetName))
					continue;

				changed = true;

				var required = Math.Max(quest.GoalRequired, 1);
				var progress = quest.GoalProgress + 1;
				if (progress < required)
				{
					// Objective tick (e.g. 2/5 kills): persist progress, refresh the HUD, leave the quest Active.
					await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Active, progress, quest.GoalRequired, quest.GoalTargetName);
					await _outbound.SendQuestGoal(client, quest.QuestId, def.GoalId, progress);
					await _outbound.SendQuestIir(client, quest.QuestId, WorldQuest.StateActive);
					await _agents.SendSystemMessage(character.Id, $"Objective {progress}/{required}.");
					continue;
				}

				if (def.NextQuestId != 0)
					await CompleteAndSupersede(client, character, quest, def);
				else if (triggerType == QuestGoalType.Kill)
					await ObjectiveCompleteAwaitTurnIn(client, character, quest, def, required); // legacy #47 (chainless kill)
				else
					await CompleteFinal(client, character, quest, def, required); // chainless loot/talk -> just complete

				// #262: talking to Casilda is what FREES her - on that talk-goal completion she becomes the player's
				// escort companion and follows them (the world tick steps her along, retail-style). Keyed by NAME
				// (handles are session-volatile); the player's world-entity handle is its ClientInstance.
				if (triggerType == QuestGoalType.TalkToNpc && string.Equals(targetName, "Casilda", StringComparison.OrdinalIgnoreCase))
				{
					_outbound.SetNpcFollowByName(character.MapId, "Casilda", client.Session.Account.ClientInstance, true);
					// #267: freeing Casilda drops the beach "invisible wall" - the chain decor's collision (its mesh
					// doesn't render for us, so it reads as an invisible barrier). Retail flips it off with a SimpleDynel
					// visual-change to the chain dynel at this exact moment (Hoxxling gameplay idx 2053).
					await _outbound.DropBeachWall(client);
				}
			}

			// #261: a stage change can un-gate or re-gate NPCs (e.g. completing Casilda's kill stage despawns Tuut) -
			// re-run the player's interest pass so gated visibility tracks the new quest state immediately.
			if (changed)
				await _outbound.RefreshQuestVisibility(client);
		}

		// Complete stage N and supersede it with stage N+1: the captured wire sequence + the state flip (mark N
		// Completed, grant N+1 Active with its goal seeded). The next stage's QuestFullUpdate is what makes the client
		// swap the displayed quest in place (no QuestLogList - the supersession reuses the existing log slot, fr1356).
		private async Task CompleteAndSupersede(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def)
		{
			var required = Math.Max(quest.GoalRequired, 1);
			await SendStageCompletion(client, quest.QuestId, def.GoalId, required);
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Completed, required, required, quest.GoalTargetName);

			var nextDef = await _database.GetQuestDefinition(def.NextQuestId);
			var nextGoal = nextDef?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
			await _database.SetCharacterQuest(character.Id, def.NextQuestId, QuestState.Active, 0, nextGoal?.RequiredCount ?? 0, nextGoal?.TargetName);
			if (nextDef != null)
				await _outbound.SendQuestFullUpdate(client, nextDef);

			// #267: if the next stage is "talk to NPC X" (e.g. 13563 loot -> 13480 talk Casilda), push X's '?' marker
			// now. The supersession happens away from X (looting Tuut's corpse), so without this her '?' wouldn't appear
			// until the client re-polls/re-streams her - and she's often still in view, so that never fires.
			if (nextGoal?.GoalType == QuestGoalType.TalkToNpc && !string.IsNullOrEmpty(nextGoal.TargetName))
				await _outbound.RefreshQuestMarkerByName(client, nextGoal.TargetName);

			_logger.LogInformation("Quest {Quest} completed for {Char}; superseded by {Next} (goal {Type}, target {Target}).", quest.QuestId, character.Name, def.NextQuestId, nextGoal?.GoalType, nextGoal?.TargetName ?? "-");
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

		// The shared head of every completion: QuestGoal (counter at required) then QuestIIR active(2) -> completed(4).
		private async Task SendStageCompletion(INetworkClient client, uint questId, uint goalId, uint required)
		{
			await _outbound.SendQuestGoal(client, questId, goalId, required);
			await _outbound.SendQuestIir(client, questId, WorldQuest.StateActive);
			await _outbound.SendQuestIir(client, questId, WorldQuest.StateCompleted);
		}
	}
}
