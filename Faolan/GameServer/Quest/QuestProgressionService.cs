using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	/// <summary>
	///     The multi-stage quest-chain supersession engine. Each stage of a chain is its own quest id; completing a
	///     stage's goal supersedes it with the next.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The captured wire sequence (Blablatest_..._casilda_quest_first_stage, fr1356/1446/1605) is:
	///     </para>
	///     <code>
	///     QuestGoal(completing, goalId, required)   the objective counter hits its required count
	///     QuestIIR(completing, StateActive=2)       journal: active or update
	///     QuestIIR(completing, StateCompleted=4)    journal: completed
	///     QuestFullUpdate(next)                     supersession: the client swaps the displayed quest to the next stage
	///     </code>
	///     <para>
	///         The wire output is identical for every goal type; the type (Kill, LootFromNpc, TalkToNpc, Escort) only
	///         selects which server trigger matches, so the three trigger owners - CombatService for a kill,
	///         LootService for a corpse bag, ConversationService for a talk - all funnel through this one engine. The
	///         Tortage intro chain is 13479 (kill Tuut) -&gt; 13563 (loot Tuut's bag) -&gt; 13480 (talk Casilda) -&gt;
	///         12407 (escort) -&gt; end.
	///     </para>
	/// </remarks>
	public sealed class QuestProgressionService
	{
		// How close, in horizontal world units, the player must get to a ReachArea goal's marker to solve it. The markers
		// are approximate - Shipwrecked's "Enter the jungle" is at 416/701, about 28 u off the spawn-to-Saddur path - so
		// this is generous enough to fire while walking past, yet far enough (94 u) not to trip at the spawn.
		private const float ReachRadius = 40f;
		private readonly IChatServerDispatcher _agents;
		private readonly IDatabaseRepository _database;

		// The inventory half of a hand-in: a Deliver goal takes what it hands over. Reached through the economy port,
		// like every other cross-cutting outbound operation.
		private readonly IEconomyOutbound _economy;
		private readonly ILogger _logger;
		private readonly IQuestOutbound _outbound;
		private readonly QuestRewardService _rewards;
		private readonly TortageIntroService _tortageIntro;

		// A quest line ending is one of retail's daily challenges, and the tick rides the completion it is measured
		// against - see DailyChallengeService for why the hook is the chain end rather than every stage.
		private readonly DailyChallengeService _dailyChallenges;

		public QuestProgressionService(IDatabaseRepository database, IQuestOutbound outbound, IEconomyOutbound economy, TortageIntroService tortageIntro,
			IChatServerDispatcher agents, QuestRewardService rewards, DailyChallengeService dailyChallenges, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
			_economy = economy;
			_tortageIntro = tortageIntro;
			_agents = agents;
			_rewards = rewards;
			_dailyChallenges = dailyChallenges;
			_logger = logger;
		}

		/// <summary>
		///     Accepts a quest for the player: persists it active with its first objective seeded, and returns the
		///     definition so the caller can render it.
		/// </summary>
		/// <remarks>
		///     Lives here rather than on the dialogue service because a grant has two callers: a conversation node that
		///     hands the quest over, and a team-mate's accepted quest share. <paramref name="source" /> is for the log line
		///     only.
		/// </remarks>
		/// <returns>
		///     Null when the player already holds the quest - accepting twice must not re-grant - or when it has no
		///     definition. The state is persisted either way; there is simply nothing to draw.
		/// </returns>
		public async Task<QuestDefinition?> Grant(INetworkClient client, uint questId, string source)
		{
			var character = client.Session.Character;

			var existing = await _database.GetCharacterQuests(character.Id);
			if (existing.Any(q => q.QuestId == questId))
				return null; // already accepted (active) or completed - don't re-grant

			var def = await _database.GetQuestDefinition(questId);

			// Seed the quest's first objective so kills can advance it: GoalRequired is the goal record's decoded count
			// (QuestGoalDefinition.RequiredCount) and GoalTargetName the seeded kill target (QuestGoalDefinition.TargetName,
			// which is server-side and sourced per quest because it is not on the wire). With both set, AdvanceKillGoals
			// matches a kill of that NPC and advances or completes the quest - Casilda's 13479 is kill 1 "Scavenger Tuut".
			// A talk or collect goal leaves TargetName null, so there is no kill matching and the count is 0.
			var firstGoal = def == null ? null : QuestProgress.InPlayOrder(def.Goals).FirstOrDefault();
			var goalRequired = firstGoal is { RequiredCount: > 0 }
				? firstGoal.RequiredCount
				: 0u;
			var goalTargetName = firstGoal?.TargetName;

			await _database.SetCharacterQuest(character.Id, questId, QuestState.Active, goalRequired: goalRequired, goalTargetName: goalTargetName);
			_outbound.ForgetQuestMarkers(character.Id);
			_logger.LogInformation("Quest {QuestId} granted to {Name} from {Source} (goalRequired={Req}, target={Target}).", questId, character.Name, source, goalRequired,
				goalTargetName ?? "-");
			return def;
		}

		// The two retail grant packets in order: the QuestLogList, which populates the J-panel with all active quests
		// and the just-granted one tracked first, then the QuestFullUpdate carrying that quest's detail. Both the order
		// and the presence matter - the list is what makes the quest appear in the log at all.
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

		/// <summary>
		///     A kill of the NPC by the player: advances any active kill goal targeting it.
		/// </summary>
		/// <remarks>
		///     Matched primarily by the stable template id, which is the goal's TrailingRefId, with the name as a fallback
		///     for group-kill goals - see QuestProgress.MatchingKillGoal. A null
		///     <paramref name="templateId" /> means no type id is known, so the match is by name only.
		/// </remarks>
		public Task AdvanceKillGoals(INetworkClient client, uint? templateId, string npcName)
		{
			return Advance(client, QuestGoalType.Kill, npcName, templateId);
		}

		/// <summary>
		///     The player received <paramref name="grantedItemId" /> out of <paramref name="sourceNpcName" />'s corpse bag:
		///     advances the loot-from-NPC goal that asks for exactly that item from exactly that NPC.
		/// </summary>
		/// <remarks>
		///     Called per granted item, not per loot action: the goal is satisfied by holding the quest item rather than by
		///     having opened the bag it was in, so a take that failed to reach the backpack - a full bag returns the item -
		///     advances nothing.
		/// </remarks>
		public Task AdvanceLootGoals(INetworkClient client, string sourceNpcName, uint grantedItemId, uint? sourceTemplateId = null)
		{
			return Advance(client, QuestGoalType.LootFromNpc, sourceNpcName, grantedItemId, npcTemplateId: sourceTemplateId);
		}

		/// <summary>
		///     The player opened <paramref name="npcName" />'s greeting: advances any active talk goal targeting it.
		/// </summary>
		/// <remarks>
		///     <paramref name="npcTemplateId" /> is the greeted NPC's stable type; the name stays for goals with no
		///     resolved template, since the greeter may be on a map whose creatures are not imported.
		/// </remarks>
		public Task AdvanceTalkGoals(INetworkClient client, string npcName, uint? npcTemplateId = null)
		{
			return Advance(client, QuestGoalType.TalkToNpc, npcName, npcTemplateId: npcTemplateId);
		}

		/// <summary>
		///     The escort reached its destination: completes any active Escort goal for the escortee template.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Retail ends the Tortage escort when the player uses the city door, decoded from both beach-to-city
		///         captures: c2s UseObject, then QuestGoal 12407/3544 1/1, then QuestIIR Active and Completed, and only
		///         then does Casilda's farewell open. The door is the trigger and the conversation is a consequence.
		///     </para>
		///     <para>
		///         Which door is data-driven: the object that opens the escortee's server-opened farewell is the object the
		///         escort was leading to (ConversationNode.TriggerWorldObjectId), so the caller resolves the NPC from the
		///         used object and this only completes the goal.
		///     </para>
		///     <para>
		///         Matched by the escortee's template id, which is what an Escort goal's TrailingRefId holds (Casilda 9411
		///         on quest 12407, the only escort in the shipped data, resolving to a real NpcTemplate). That is not true
		///         of the other name-targeted goal types: a TalkToNpc goal's TrailingRefId is the item the player hands
		///         over, and a LootFromNpc goal carries none at all, so those two still match by name - see
		///         ConversationService.TryReplayConversation and QuestProgress.MatchingLootGoal.
		///     </para>
		/// </remarks>
		public Task AdvanceEscortGoals(INetworkClient client, uint npcTemplateId)
		{
			return Advance(client, QuestGoalType.Escort, null!, npcTemplateId);
		}

		/// <summary>
		///     The player used a world object - a door, a gate, a lever: completes any active goal that is about using that
		///     object. Called from the same <c>UseDynel</c> choke point the escort hand-off uses, on the begin phase only.
		/// </summary>
		/// <remarks>
		///     The Acheronian Gate is the case it exists for. "Unlock the Acheronian gate" (12369/3492) is not a ReachArea
		///     goal: the gate stands on its own marker, so a proximity reading would complete the objective before the key
		///     had been looted and without the gate ever being touched. Retail's order is unambiguous - target the gate,
		///     send the use, and only then does the goal tick (`Hoxxling_mage-heraldofxotli_login_gameplay` fr12193 select,
		///     fr12196 use, fr12204 goal 3492 1/1).
		/// </remarks>
		public Task AdvanceUseObjectGoals(INetworkClient client, uint worldObjectId)
		{
			return Advance(client, QuestGoalType.UseWorldObject, null!, worldObjectId);
		}

		/// <summary>
		///     The player's rank in the named faction, or null when that cannot be answered - which is the case while there
		///     is no faction and reputation system.
		/// </summary>
		/// <remarks>
		///     This is the one place that changes when there is one. Null means "not met" in
		///     QuestProgress.PreconditionMet, so the three rank-gated goals stay inert and start working the moment this
		///     can answer, with no goal re-wired and no capture re-harvested.
		///     Open work tracked in Gitea #310.
		/// </remarks>
		private static uint? ResolveFactionRank(string factionTitle)
		{
			return null;
		}

		/// <summary>
		///     Whether the player is currently escorting the NPC template - an active quest with an unfinished Escort goal
		///     pointing at it through the goal's TrailingRefId.
		/// </summary>
		/// <remarks>
		///     The caller needs this before advancing, to tell the one door use that ends an escort from every later use of
		///     the same door: the farewell plays once, and the zone line behaves normally afterwards.
		/// </remarks>
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

		/// <summary>
		///     The ReachArea goals the player still needs to reach, as in-memory targets for the move-handler proximity
		///     check (WorldEntity.PendingReachGoals).
		/// </summary>
		/// <remarks>
		///     Loaded once at the first move so the per-move test hits no database. Goals already solved are skipped, so a
		///     relog past the area does not re-fire, as is every non-ReachArea goal. A ReachArea goal has no TargetName:
		///     its objective is the (MapId, MarkerX, MarkerY) point, which is the objective's world location.
		/// </remarks>
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

					// A reach goal's whole target is its pin, so a placeholder marker would make it solvable by
					// standing in the corner of the map. `(50, 50)` appears on nine different playfields in the shipped
					// data and `(1, 1)` on three, which is not what a place looks like - see QuestProgress. A goal
					// carrying the client's real trigger region is exempt: the volume is the target then, and the
					// marker is only a map pin.
					if (goal.Region == null && !QuestProgress.HasReachablePin(goal))
					{
						_logger.LogDebug("Quest {Quest} goal {Goal} is typed ReachArea but has no usable marker ({X}/{Y} on map {Map}); not tracked.", quest.QuestId, goal.GoalId,
							goal.MarkerX, goal.MarkerY, goal.MapId);
						continue;
					}

					// The client's real trigger volume where the goal carries one, else the marker radius. The region's
					// own MapId wins when the goal has none: goal 14073 carries no map at all, and its region
					// (`BossRoom`) is what says which playfield it is on.
					targets.Add(new ReachGoalTarget(quest.QuestId, goal.GoalId, goal.Region?.MapId ?? goal.MapId ?? 0, goal.MarkerX ?? 0, goal.MarkerY ?? 0,
						ReachRadius * ReachRadius, goal.Region));
				}
			}

			return targets;
		}

		/// <summary>
		///     Solves goal <paramref name="goalId" /> directly - the reach-a-spot proximity path. Finds the active quest
		///     that owns the goal, primary or secondary, and jumps that goal to its required count; the quest completes
		///     only once every goal is done. No target-name match is needed, because reaching the spot has already decided
		///     it is solved.
		/// </summary>
		/// <remarks>Advances scripted objectives without bespoke per-quest code.</remarks>
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

		/// <summary>
		///     The player interacted with a quest-pickup world object - a QuestItemEffect dynel such as a quarry stone
		///     brick. One item per call.
		/// </summary>
		/// <remarks>
		///     Resolve only: it deliberately does not advance the goal. The caller grants the item, and the grant is what
		///     ticks the goal (AdvanceCollectGoals, off GrantItemToBackpack); advancing here as well would double-count
		///     every pickup.
		/// </remarks>
		/// <returns>
		///     The item the node should hand over - the TrailingRefId of the first active, still-incomplete CollectItem
		///     goal - or null when no collect quest is active, which is what makes the brick nodes inert for a player
		///     without Shipwrecked V.
		/// </returns>
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

		/// <summary>
		///     An item reached the player's inventory - advances any active CollectItem goal asking for it.
		/// </summary>
		/// <remarks>
		///     Fired from the one grant choke point (GrantItemToBackpack), so it covers every way an item arrives: a
		///     quest-pickup node, a mob's corpse bag, a vendor purchase, a harvest, a claim, `.additem`. That is
		///     deliberate: a gather objective is "hold N of item X", and the client's own quest data says nothing about how
		///     the item is obtained - the goal's typed reference (LDB category 50200) names an item and stops there. Wiring
		///     per-source triggers would mean guessing loot against pickup against bought per quest, and a wrong guess
		///     makes the quest silently un-completable.
		/// </remarks>
		public Task AdvanceCollectGoals(INetworkClient client, uint itemTemplateId, uint count = 1)
		{
			// `count` is the stack size granted; a stack of three advances a collect goal by three.
			return Advance(client, QuestGoalType.CollectItem, null!, itemTemplateId, Math.Max(1u, count));
		}

		/// <summary>
		///     The player used an item out of a bag - advances any active UseItem goal asking for it.
		/// </summary>
		/// <remarks>
		///     Fired from <c>UseInventoryItem</c>, the one right-click choke point, after the item's own effect has been
		///     applied, so an item that is both a consumable and a quest objective does both. One use is one tick
		///     regardless of the stack: a stack of five vials is five uses, not one worth five.
		/// </remarks>
		public Task AdvanceUseGoals(INetworkClient client, uint itemTemplateId)
		{
			return Advance(client, QuestGoalType.UseItem, null!, itemTemplateId);
		}

		/// <summary>
		///     Advances the goal matching <paramref name="triggerType" /> and <paramref name="targetName" /> for each of
		///     the player's active quests.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A quest can have several goals, so the trigger ticks the matching goal rather than always the first; the
		///         quest completes only once every goal has reached its required count. A tick that leaves the quest
		///         incomplete - this goal not yet done, or a sibling goal still pending - refreshes the HUD and stays
		///         Active. A complete quest supersedes to the next stage, or, for a chainless kill, awaits turn-in at the
		///         giver.
		///     </para>
		///     <para>
		///         There are two id parameters because <paramref name="targetTemplateId" /> does not mean one thing across
		///         the trigger types: it is the killed or escorted NPC for Kill and Escort, but the item for Collect and
		///         Loot. <paramref name="npcTemplateId" /> is always the NPC - the bag's source creature on a loot, the
		///         greeted creature on a talk - so the loot path can match on both halves of what it knows. Kill and Escort
		///         leave it null, because their NPC is already in the other slot.
		///     </para>
		/// </remarks>
		/// <param name="step">
		///     How much a single trigger is worth: 1 for a kill, talk or loot, but a granted stack advances a collect goal
		///     by its whole count, so picking up three bricks at once is three of the three.
		/// </param>
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
				// Match the goal to advance: a kill by the stable target template id with the name as fallback; a
				// collect by its item id (TrailingRefId); an escort by the escortee's template (TrailingRefId); a loot
				// by the bag's source NPC and the item received; a talk by the greeted NPC.
				//
				// The loot and talk paths pass the NPC's template alongside its name, so they match the same way the
				// kill path does - id first, name only where the goal has no resolved template. A talk goal's own
				// TrailingRefId is no help there, being the hand-over item rather than the NPC; the resolved
				// `TargetTemplateId` column is what gives those two an id to match on at all.
				var goal = triggerType switch
				{
					QuestGoalType.Kill        => QuestProgress.MatchingKillGoal(def.Goals, targetTemplateId, targetName),
					QuestGoalType.CollectItem => QuestProgress.MatchingCollectGoal(def.Goals, targetTemplateId ?? 0),
					QuestGoalType.UseItem     => QuestProgress.MatchingUseGoal(def.Goals, targetTemplateId ?? 0),
					QuestGoalType.LootFromNpc => QuestProgress.MatchingLootGoal(def.Goals, targetName, targetTemplateId ?? 0, npcTemplateId),
					QuestGoalType.Escort      => QuestProgress.MatchingEscortGoal(def.Goals, targetTemplateId ?? 0),
					QuestGoalType.UseWorldObject => QuestProgress.MatchingUseObjectGoal(def.Goals, targetTemplateId ?? 0),
					_                         => QuestProgress.MatchingGoal(def.Goals, triggerType, targetName, npcTemplateId)
				};
				if (goal == null)
					continue;

				// A stated precondition must hold before the goal can advance. Enforced here, at the trigger, rather than
				// by refusing to wire the goal: the level check works today, and the faction-rank one starts working the
				// day ResolveFactionRank has a real answer, with no re-harvest and no re-wiring.
				if (!QuestProgress.PreconditionMet(goal, character.Level, ResolveFactionRank))
				{
					await _agents.SendSystemMessage(character.Id, goal.RequiredLevel is { } lvl
						? $"You must be level {lvl} for this."
						: "You are not of high enough standing for this yet.");
					continue;
				}

				// A Deliver goal is a hand-in, so the player must actually be carrying what it hands over. Without this
				// the talk alone solves it and "deliver a supply crate to Croiss" completes by walking up empty-handed.
				if (!await HoldsDeliveryItem(character.Id, goal))
				{
					// Answered rather than dropped: the player is on the right quest talking to the right NPC, so
					// silence reads as a broken quest. This only fires once the goal has already matched.
					await _agents.SendSystemMessage(character.Id, "You are not carrying what this delivery needs.");
					continue;
				}

				// An "unlock X" goal wants the key in hand. The same shape as the delivery check above, and a separate
				// rule for one reason: this one does not take the item, so the key stays in the bag (see
				// QuestProgress.RequiredHeldItemId). Without it, "Unlock the Acheronian gate" is solved by shoving the
				// gate empty-handed.
				if (!await HoldsRequiredItem(character.Id, goal))
				{
					await _agents.SendSystemMessage(character.Id, "It is locked. You need the key.");
					continue;
				}

				var required = GoalRequired(quest, def, goal);
				// A talk goal is binary: talking to the NPC solves it, jumping to the required count regardless of the
				// display count. That lets a "deliver N to X" hand-in - Shipwrecked V's "deliver 3 bricks to Turach",
				// RequiredCount 3 for the captured objective text - complete in one talk. Every other trigger is +1.
				var current = await GoalProgressOf(character.Id, quest, def, goal);
				if (current >= required)
					continue; // already complete (a re-fired trigger on a satisfied objective)

				var progress = triggerType == QuestGoalType.TalkToNpc
					? required
					: Math.Min(required, current + step);

				// The hand-in takes the goods. Placed after the `current >= required` re-fire guard above, so talking to
				// the recipient a second time cannot take a second set; HoldsDeliveryItem has already verified the count
				// is present.
				//
				// The corpus shows this removal (`--report quest-container`): in xxx_mage-necromancer_login_play2 the
				// 0x71 slot is emptied at packet 5765 and quest 12371 - whose completing goal is "Deliver 3 stone bricks
				// to Turach" - completes four packets later at 5769.
				if (QuestProgress.IsDelivery(goal))
					await ConsumeDeliveryItems(client!, character.Id, goal, required);

				changed = true;
				await PersistGoalProgress(character.Id, quest, def, goal, progress);

				// The goal's first advance can spring an ambush - the Quarry Bat Demon on the first of three stone
				// bricks. First only: the captured session picks up all three and gets exactly one Bat Demon, so the
				// gate is `current == 0` rather than every tick. Persisted progress is what makes it survive a relog, so
				// a player who already holds a brick is not ambushed again.
				if (current == 0 && await _database.GetNpcAmbushForGoal(def.QuestId, goal.GoalId) is { } ambush)
					_outbound.ScheduleQuestAmbush(client!, ambush);

				// The whole quest is done only when this goal hit its count and every other goal is already complete.
				if (progress >= required && await AllGoalsComplete(character.Id, quest, def, goal, progress))
				{
					await CompleteObjective(client!, character, quest, def, goal, required);

					// The key an "unlock X" goal demanded is spent by the unlocking, and it goes after the completion
					// because that is where retail puts it: fr12205 QuestIIR completed, fr12206 DispatchRemoveItem 0x71
					// slot 0. The delivery consume above lands before its completion for the same reason (fr5765
					// remove, fr5769 completed), so both orderings are retail's.
					await ConsumeRequiredItem(client!, character.Id, goal);
					continue;
				}

				// An objective tick - 2/5 kills, or one of several goals done: refresh the HUD and leave the quest
				// Active. A tick that reaches the required count says Satisfied even though the quest as a whole is not
				// done, which is what retail sends on the third of three stone bricks, and it closes with Advanced.
				await SendGoalUpdate(client!, quest.QuestId, goal.GoalId, progress >= required);
				await _agents.SendSystemMessage(character.Id, $"Objective {progress}/{required}.");
			}

			// A stage change can un-gate or re-gate NPCs - completing Casilda's kill stage despawns Tuut - so re-run the
			// player's interest pass and gated visibility tracks the new quest state immediately.
			if (changed)
				await _outbound.RefreshQuestVisibility(client!);
		}

		/// <summary>
		///     The key an "unlock X" goal demanded is spent by the unlocking. A no-op for every other goal.
		/// </summary>
		/// <remarks>
		///     The rule is "the goal that spends it takes it", not "a completed quest clears its container", and the
		///     second reading is refuted rather than merely unchosen. Measured by <c>--report quest-container</c> over the
		///     whole corpus: of 17 quest completions that happened while the 0x71 container held something, 7 kept their
		///     items. The decisive pair is one chain in one capture:
		///     <list type="bullet">
		///         <item>
		///             13563 "Get the Scavenger's Key", a <c>LootFromNpc</c> acquire goal, completes at packet 1431
		///             holding the key - and the key stays, so a completion did not clear the container.
		///         </item>
		///         <item>
		///             13480 "Free Casilda" completes at 1899 and the key is removed at 1902, because that is the step
		///             that uses it.
		///         </item>
		///     </list>
		///     Only the <c>UseWorldObject</c> half is wired, because it is the only one whose goal names the item it
		///     spends (<c>QuestItemId</c>, such as 12369's gate goal naming 193186). "Free Casilda" spends a key that
		///     belongs to an earlier quest's goal and no column says so, so wiring that would mean inventing the link.
		/// </remarks>
		private async Task ConsumeRequiredItem(INetworkClient client, uint characterId, QuestGoalDefinition goal)
		{
			var itemId = QuestProgress.RequiredHeldItemId(goal);
			if (itemId == 0)
				return;

			var taken = await _economy.TakeItemsFromInventory(client, itemId, 1);
			if (taken < 1)
				_logger.LogWarning("Goal {Goal} spent key {Item}, but character {Character} no longer held it - the hold check passed, so the inventory changed under it.", goal.GoalId, itemId, characterId);
		}

		/// <summary>
		///     The other half of the hand-in: HoldsDeliveryItem checks, this takes. <c>required</c> is the count the goal
		///     asks for, which is what the check demanded, so it is what gets handed over - extras the player is carrying
		///     stay theirs.
		/// </summary>
		/// <remarks>
		///     The removal is a decoded fact: in xxx_mage-necromancer_login_play2 the 0x71 slot empties at packet 5765 and
		///     quest 12371 - completing goal "Deliver 3 stone bricks to Turach" - completes at 5769, four packets later.
		///     A short take is logged rather than tolerated silently: the caller has already verified the player holds
		///     enough, so removing fewer means the inventory moved underneath it. The goal still advances, because refusing
		///     to complete a quest the player has legitimately finished is the worse failure of the two.
		/// </remarks>
		private async Task ConsumeDeliveryItems(INetworkClient client, uint characterId, QuestGoalDefinition goal, uint required)
		{
			var wanted = Math.Max(1u, required);
			// The item comes from QuestProgress.DeliveryItemId rather than from the wire field directly: a delivery
			// resolved from the client's catalog sentence names its item in QuestItemId and has no trailing reference.
			var itemId = QuestProgress.DeliveryItemId(goal);
			var taken = await _economy.TakeItemsFromInventory(client, itemId, wanted);
			if (taken < wanted)
			{
				_logger.LogWarning("Hand-in of goal {Goal}: took {Taken} of {Wanted} x item {Item} from character {Character} - the delivery check passed, so the inventory changed mid-hand-in.",
					goal.GoalId, taken, wanted, itemId, characterId);
			}
		}

		/// <summary>
		///     Whether the player is carrying what a Deliver goal hands over. The rule itself is QuestProgress.CanHandIn
		///     and this only supplies the count; ConsumeDeliveryItems takes them once the hand-in goes through.
		/// </summary>
		private async Task<bool> HoldsDeliveryItem(uint characterId, QuestGoalDefinition goal)
		{
			if (!QuestProgress.IsDelivery(goal))
				return true;

			// Summed across slots: a stack can be split over several cells, and a split stack is still the items.
			var itemId = QuestProgress.DeliveryItemId(goal);
			return QuestProgress.CanHandIn(goal, await HeldCount(characterId, itemId));
		}

		/// <summary>
		///     Whether the player is carrying what an "unlock" needs - the gate key. True when the goal names no such item,
		///     so this is a no-op on every other goal type.
		/// </summary>
		/// <remarks>
		///     Separate from <see cref="HoldsDeliveryItem" />, and the difference is what happens next: a delivery's item
		///     is taken when the hand-in goes through, and a key is not. One is a hand-over, the other a prerequisite.
		/// </remarks>
		private async Task<bool> HoldsRequiredItem(uint characterId, QuestGoalDefinition goal)
		{
			var itemId = QuestProgress.RequiredHeldItemId(goal);
			return itemId == 0 || await HeldCount(characterId, itemId) > 0;
		}

		/// <summary>
		///     How many of <paramref name="itemId" /> the character holds, summed across slots: a stack can be split over
		///     several cells, and a split stack is still the items.
		/// </summary>
		private async Task<uint> HeldCount(uint characterId, uint itemId)
		{
			return (await _database.GetCharacterItems(characterId)).Where(i => i.TemplateId == itemId).Aggregate(0u, (sum, i) => sum + Math.Max(1u, i.Count));
		}

		/// <summary>
		///     Whether every goal of the quest has now reached its required count.
		/// </summary>
		/// <remarks>
		///     <paramref name="justTicked" /> and <paramref name="justProgress" /> are the goal written this pass, read
		///     back from the arguments rather than the database, because the write may not be visible yet on a fresh
		///     short-lived context. All other goals are read from their store: the primary scalar, or the secondary rows.
		/// </remarks>
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

		/// <summary>
		///     Completes stage N and supersedes it with stage N+1: the captured wire sequence plus the state flip - mark N
		///     Completed, grant N+1 Active with its goal seeded.
		/// </summary>
		/// <remarks>
		///     The next stage's QuestFullUpdate is what makes the client swap the displayed quest in place. No QuestLogList
		///     is sent, because the supersession reuses the existing log slot (fr1356).
		/// </remarks>
		private async Task CompleteAndSupersede(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def, uint goalId)
		{
			var required = Math.Max(quest.GoalRequired, 1);
			await SendStageCompletion(client, quest.QuestId, goalId);
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Completed, required, required, quest.GoalTargetName);
			_outbound.ForgetQuestMarkers(character.Id);

			// Reached only through CompleteObjective's `NextQuestId is not null` guard, so the successor id is present.
			var nextQuestId = def.NextQuestId!.Value;
			var nextDef = await _database.GetQuestDefinition(nextQuestId);
			var nextGoal = nextDef == null ? null : QuestProgress.InPlayOrder(nextDef.Goals).FirstOrDefault();
			await _database.SetCharacterQuest(character.Id, nextQuestId, QuestState.Active, 0, nextGoal?.RequiredCount ?? 0, nextGoal?.TargetName);
			_outbound.ForgetQuestMarkers(character.Id);
			if (nextDef != null)
				await _outbound.SendQuestFullUpdate(client, nextDef);

			// If the next stage is "talk to NPC X" - 13563 loot leading to 13480 talk Casilda - push X's '?' marker now.
			// The supersession happens away from X, at Tuut's corpse, so without this the marker would not appear until
			// the client re-polls or re-streams her, and she is often still in view, so that never fires.
			if (nextGoal?.GoalType == QuestGoalType.TalkToNpc && (!string.IsNullOrEmpty(nextGoal.TargetName) || nextGoal.TargetTemplateId is > 0))
				await _outbound.RefreshQuestMarkerFor(client, nextGoal.TargetName!, nextGoal.TargetTemplateId);

			// The new stage may carry a ReachArea goal - Shipwrecked II "find the gate key", III "enter the city" - so
			// drop the player's lazy reach cache and the next move reloads it and starts watching the new area.
			_outbound.InvalidateReachGoals(client);

			// A new escort stage means the escortee becomes the player's companion and follows them, stepped along by
			// the world tick. Data-driven off the Escort goal, resolved by the escortee's template id (TrailingRefId,
			// e.g. Casilda 9411) rather than a hard-coded name; the player's world handle is its ClientInstance.
			if (nextGoal is { GoalType: QuestGoalType.Escort, TrailingRefId: not 0 })
			{
				_outbound.SetNpcFollowByTemplate(character.MapId, nextGoal.TrailingRefId, character.Id, true);
				// After-quest world effects now that the escort is active - the beach chain-wall collision drops,
				// data-driven off WorldObject.EffectQuestId and map-scoped, so it no-ops off the beach. Then clear the
				// escortee's captive struggle pose so she stands and walks as she follows.
				await _outbound.ApplyQuestWorldEffects(client);
				await _tortageIntro.ClearCasildaCaptivePose(client);
			}

			_logger.LogInformation("Quest {Quest} completed for {Char}; superseded by {Next} (goal {Type}, target {Target}).", quest.QuestId, character.Name, nextQuestId, nextGoal?.GoalType,
				nextGoal?.TargetName ?? "-");
			await _agents.SendSystemMessage(character.Id, "Quest updated.");
		}

		// The final stage of a chain, or a chainless loot or talk goal: complete it outright, with no successor to grant.
		private async Task CompleteFinal(INetworkClient client, Character character, CharacterQuest quest, uint goalId, uint required)
		{
			await SendStageCompletion(client, quest.QuestId, goalId);
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Completed, required, required, quest.GoalTargetName);
			_outbound.ForgetQuestMarkers(character.Id);

			// Retail ticks DailyQuestsCompleted here and not on a superseded stage: the chain end is the event its five
			// captured occurrences line up with, one for one.
			await _dailyChallenges.RecordQuestLineCompleted(client, character.Id, DateTimeOffset.UtcNow);

			_logger.LogInformation("Quest {Quest} completed for {Char} (chain end).", quest.QuestId, character.Name);
			await _agents.SendSystemMessage(character.Id, "Quest complete!");
		}

		/// <summary>
		///     The objective for <paramref name="quest" />'s goal has hit its required count: completes the stage.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Shared by the trigger path (Advance) and the direct-solve path (SolveGoal): the completion is identical,
		///         only how the objective is reached differs. It supersedes to the next stage, holds for turn-in on a
		///         chainless kill, or completes outright, then runs the goal's completion side-effects. Keyed off the goal
		///         - its type and target - rather than the trigger, so both callers share one code path. It does not run
		///         RefreshQuestVisibility; the caller does that once after its loop.
		///     </para>
		///     <para>
		///         The completion packet names the goal that just completed, not <c>def.GoalId</c>. On a single-goal quest
		///         those are the same id; on a multi-goal quest retail closes 12371 on g3497, 10238 on g3490 and 10287 on
		///         g7363 - the last goal - while <c>def.GoalId</c> holds the quest's head goal (3495, 1448, 400).
		///         [V2: same four captures as the Advanced pairs]
		///     </para>
		/// </remarks>
		private async Task CompleteObjective(INetworkClient client, Character character, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition goal, uint required)
		{
			if (def.NextQuestId is not null)
				await CompleteAndSupersede(client, character, quest, def, goal.GoalId); // the escort follow / beach wall fire from here when the NEXT stage is an Escort
			else if (goal.GoalType == QuestGoalType.Kill)
				await ObjectiveCompleteAwaitTurnIn(client, character, quest, goal.GoalId, required); // legacy #47 (chainless kill)
			else
				await CompleteFinal(client, character, quest, goal.GoalId, required); // chainless loot/talk -> just complete

			// A quest that offers a choice of rewards presents it now, and deliberately not on the turn-in path above:
			// that one leaves the quest Active awaiting a hand-in, and the reward belongs to completion. The capture puts
			// the offer inside the completion burst, after the QuestIIR state flip, which is where this lands.
			//
			// The XP and money the quest's own record states are paid on the same condition and for the same reason: the
			// turn-in path has not completed anything yet, so it pays at its own flip
			// (ConversationService.TryTurnInQuests) rather than here.
			if (goal.GoalType != QuestGoalType.Kill || def.NextQuestId is not null)
			{
				await _rewards.GrantCompletionRewards(client, quest.QuestId);
				await _rewards.OfferOnCompletion(client, quest.QuestId);
			}
		}

		private async Task<uint> GoalProgressOf(uint characterId, CharacterQuest quest, QuestDefinition def, QuestGoalDefinition goal)
		{
			if (IsPrimaryGoal(def, goal))
				return quest.GoalProgress;

			var rows = await _database.GetCharacterQuestGoals(characterId, quest.QuestId);
			return rows.FirstOrDefault(r => r.GoalId == goal.GoalId)?.Progress ?? 0;
		}

		/// <summary>
		///     The path for a chainless kill quest: the kill finishes the objective but the quest stays Active and is
		///     marked Completed when the player turns it in at the giver (ConversationService.TryTurnInQuests), at which
		///     point the '?' giver marker appears.
		/// </summary>
		/// <remarks>
		///     Kept so a standalone kill-and-return quest works; the Casilda chain, where every stage has a successor,
		///     never reaches here.
		/// </remarks>
		private async Task ObjectiveCompleteAwaitTurnIn(INetworkClient client, Character character, CharacterQuest quest, uint goalId, uint required)
		{
			await _database.SetCharacterQuest(character.Id, quest.QuestId, QuestState.Active, required, quest.GoalRequired, quest.GoalTargetName);
			_outbound.ForgetQuestMarkers(character.Id);
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
			_outbound.ForgetQuestMarkers(characterId); // after either branch
		}

		/// <summary>
		///     The HUD update for a goal that moved but did not finish the whole quest.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Retail has exactly two shapes here, and neither carries a QuestIIR - the journal state flip (Active 2 to
		///         Completed 4) belongs to completion only, in SendStageCompletion:
		///     </para>
		///     <code>
		///     still short of the count        -&gt; QuestGoal(Progress)
		///     count reached, quest not done   -&gt; QuestGoal(Satisfied), QuestGoal(Advanced)
		///     </code>
		///     <para>
		///         Advanced is the hand-off marker on the goal being left behind: it rides in the same frame as that goal's
		///         own Satisfied, and the successor goal's Satisfied follows whenever it is actually solved, up to eight
		///         minutes later in the corpus. It means "this one closed", not "the next goal opened".
		///     </para>
		///     <para>
		///         [V2: all 10 captured Advanced sightings are that pair - 10238 g1448, 12369 g7134, 12371 g3495, 10287
		///         g400 across Hoxxling_mage-heraldofxotli_login_gameplay{,2}, xxx_mage-{demo,necromancer}_login_play*,
		///         loot-drag-1538. The last goal of a quest (10238 g3490, 12369 g3492, 12371 g3497, 10287 g7363) sends
		///         Satisfied then QuestIIR 2/4 and no Advanced. No QuestIIR appears in any Progress or Advanced frame.]
		///     </para>
		/// </remarks>
		private async Task SendGoalUpdate(INetworkClient client, uint questId, uint goalId, bool satisfied)
		{
			await _outbound.SendQuestGoal(client, questId, goalId, satisfied
				? QuestGoalUpdate.Satisfied
				: QuestGoalUpdate.Progress);

			if (satisfied)
				await _outbound.SendQuestGoal(client, questId, goalId, QuestGoalUpdate.Advanced);
		}

		// The shared head of every completion: QuestGoal (the goal is met), then QuestIIR active(2) then completed(4).
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

		// Per-goal progress plumbing: the primary (lowest-Sequence) goal lives in the scalar CharacterQuest fields, and
		// secondary goals live in CharacterQuestGoal rows. These helpers route to the right store by goal identity.

		private static bool IsPrimaryGoal(QuestDefinition def, QuestGoalDefinition goal)
		{
			var primary = QuestProgress.InPlayOrder(def.Goals).FirstOrDefault();
			return primary != null && primary.GoalId == goal.GoalId;
		}

		/// <summary>
		///     Solves any ReachArea quest goal the player has just walked into range of. Called from the move handler on
		///     every accepted player move.
		/// </summary>
		/// <remarks>
		///     The goal targets are cached on the player's WorldEntity and loaded lazily once, so this stays a pure
		///     in-memory distance test with no database hit until a goal actually fires. A solved target is removed so it
		///     fires exactly once; SolveGoal handles the tick, completion and chain supersession. It lives here rather than
		///     in the move handler because deciding whether a goal is reached is quest logic, and both halves it needs -
		///     LoadReachGoals and SolveGoal - belong to this service.
		/// </remarks>
		public async Task CheckReachGoals(INetworkClient client, WorldEntity player, Vector3 position)
		{
			var pending = player.Knowledge.PendingReachGoals ??= await LoadReachGoals(client.Session.Character.Id);
			if (pending.Count == 0)
				return;

			// Snapshot the in-range targets before solving: SolveGoal can complete a stage whose supersession calls
			// InvalidateReachGoals, setting PendingReachGoals to null for a reload on the next move, so the live list must
			// not be indexed across a solve. Removes are null-safe, and a target solved after an invalidation is excluded
			// from the reload because LoadReachGoals skips completed goals, so nothing double-fires.
			var reached = pending.Where(t => t.MapId == player.MapId && ReachedTarget(t, position)).ToList();
			foreach (var target in reached)
			{
				player.Knowledge.PendingReachGoals?.Remove(target);
				_logger.LogInformation("#262: player {Char} reached quest {Quest} goal {Goal} area ({X},{Z}).", client.Session.Character.Name, target.QuestId, target.GoalId, target.X, target.Z);
				await SolveGoal(client, target.GoalId);
			}
		}

		// Inside the goal's client trigger volume where it has one - the full prism test, so a goal region stacked over
		// another does not fire from the wrong floor - else within the marker radius, a horizontal circle around the
		// goal's map pin.
		private static bool ReachedTarget(ReachGoalTarget t, Vector3 position)
		{
			return t.Region != null
				? EnvironmentService.Contains(t.Region, position)
				: (position.X - t.X) * (position.X - t.X) + (position.Z - t.Z) * (position.Z - t.Z) <= t.RadiusSq;
		}
	}
}
