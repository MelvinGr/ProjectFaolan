using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	// DECIDES what a quest NPC is worth to a given player - the marker byte over its head, whether it owes them a quest
	// item off its corpse, and which quests they hold. It reads the DB and answers a question; it sends nothing and
	// touches no world model.
	//
	// This is the state-and-decision half of the quest-marker path (#826). The WIRE half stays on WorldBroadcaster,
	// which owns the "which entity is this, and who can see it" questions (`RefreshQuestMarkerFor`,
	// `RefreshQuestGiverMarker`, `QuestMarkerStateForHandle`, `SendQuestMarker`) and calls in here for the answer. The
	// split is what lets the same answer serve both the interest-spawn push and the 0x2020-poll reply without either
	// path re-deriving it.
	public sealed class QuestMarkerResolver
	{
		// DispatchSetQuestMarker (0x2008) state byte (decoded #241): 0x02 = '!' (quest available), 0x04 = '?' (ready
		// to turn in), 0x00 = none. 0x01 = the SILVER '!' - a quest NPC with nothing for you right now (#559).
		//
		// The silver state was missed by #241, which only saw three values. It is the second most common on the wire:
		// of the 301 replies in xxx_mage-necromancer_login_play2, 181 are 0x00, 74 are 0x01, 30 are 0x02, 16 are 0x04.
		// Turach and Laranga mirror each other across that capture and pin what it means - Turach is 0x01 at login
		// (his quest needs Laranga first), 0x04 once she points at him, 0x01 again while his own quest is in progress,
		// 0x04 when the bricks are collected, 0x01 after the hand-in. So 0x01 covers BOTH "prerequisite not met" and
		// "held, or already turned in - nothing more here".
		//
		// ⚠️ 0x00 and 0x01 are NOT interchangeable: 52 NPCs in that capture only ever returned 0x00, and they are the
		// ones that give no quest at all. Silver says "this NPC is part of a chain"; none says "this NPC is scenery".
		public const byte None = 0x00, Unavailable = 0x01, Available = 0x02, TurnIn = 0x04;

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// Quest-giver NPC template id -> granted quest id, cached once (small set).
		private Dictionary<uint, uint> _questGivers = null!;

		public QuestMarkerResolver(IDatabaseRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// Whether this NPC template gives a quest at all - the cheap gate the spawn push applies before computing a
		// state it would only throw away.
		public async Task<bool> IsGiver(uint npcTemplateId)
		{
			_questGivers ??= await _database.GetQuestGiverMap();
			return _questGivers.ContainsKey(npcTemplateId);
		}

		// The quest-marker state byte (#241) for a viewer + a giver NPC in view, from the player's CharacterQuests:
		// no quest held -> '!' (0x02); goals complete -> '?' (0x04); active/in-progress -> cleared (0x00). Returns
		// 0x00 for a non-giver. Shared by the interest-spawn push AND the 0x2020-poll reply so they never disagree.
		//
		// It takes the ENTITY rather than a name because its two questions want different keys (#646): "is this the NPC
		// my next talk goal names" is name-vs-name, since a talk goal's target is a string in the client's own RDB data
		// (#521), while "does this NPC give a quest" is answered by the TEMPLATE, which is what the dialogue graph the
		// giver map is derived from is keyed by.
		public async Task<byte> MarkerStateFor(INetworkClient client, WorldEntity npc)
		{
			var character = client.Session.Character;
			var npcName = npc?.Name;
			if (npc == null || npcName == null || character == null)
				return None;

			// '?' (0x04) when the player has an ACTIVE quest whose current goal is to TALK to this NPC - she's the
			// next step (e.g. the Casilda chain's talk-to-free stage 13480, active after looting Tuut's key). This is a
			// SEPARATE source from the giver logic below: the giver quest (13479) is already Completed/superseded by
			// then, so without this Casilda showed no '?'. Decoded from the captive-quest capture: retail sends 0x2008
			// state 0x04 to her handle at the loot->talk transition (Blablatest fr1455).
			// ⚠️ This runs once per 0x2020 spawn poll, and the client polls once per NPC it is loading - ~77 of them on
			// the Tortage beach. A query per ACTIVE quest here made each poll ~12 DB round trips / ~257ms, so the
			// client's serialized poll queue took ~20s to drain and every request behind it - including its 5s latency
			// check - measured that backlog. One batched read instead; order is preserved, so the FIRST match wins.
			// The giver logic below needs the raw rows in EVERY state (held / completed), so that stays a plain read.
			var quests = await _database.GetCharacterQuests(character.Id);
			foreach (var (_, qDef) in await _database.GetCharacterQuestsWithDefinitions(character.Id, QuestState.Active))
			{
				// #521: template first, name second - the same test the talk TRIGGER uses, so the marker and what
				// actually fires on the greeting cannot disagree.
				var qGoal = QuestProgress.InPlayOrder(qDef.Goals).FirstOrDefault();
				if (qGoal?.GoalType == QuestGoalType.TalkToNpc && QuestProgress.TargetsNpc(qGoal, npc.NpcTemplateId, npcName))
					return TurnIn;
			}

			_questGivers ??= await _database.GetQuestGiverMap();
			if (!_questGivers.TryGetValue(npc.NpcTemplateId, out var questId))
				return None;

			// A giver's '!'/'?' belongs ONLY to the NPC on the grant-quest's own map. Gate on the grant quest's MapId
			// (its QuestDefinition, #244): only the giver's own map shows it. This used to also be what stopped a
			// same-NAMED NPC world-wide from wearing the marker - "Halen van Dummy" is the internal name of 27
			// unrelated templates - but the lookup above is keyed by TEMPLATE since #646, so the map gate is back to
			// meaning only what it says.
			var giverQuest = await _database.GetQuestDefinition(questId);
			if (giverQuest != null && giverQuest.MapId != npc.MapId)
				return None;

			var held = Array.Find(quests, q => q.QuestId == questId);
			// '?' once the objective is done but the quest is still ACTIVE (awaiting turn-in at the giver - a quest is
			// only marked Completed on turn-in, not on the kill, #47/#241). Otherwise, held or not, the choice is
			// between the GOLD '!' and the SILVER one, and it is the same question both times: does this NPC have
			// something for you RIGHT NOW (#559)?
			//   * not held, and every earlier step of its line is done -> GOLD, it is yours to take;
			//   * not held, but the line has not reached it -> SILVER. This is Turach at login in
			//     `xxx_mage-necromancer_login_play2_tortage_city_first-quests`: his "The Quarry" is step 5 of line 241
			//     and the player has done none of steps 1-4, so retail greys him rather than drawing no marker;
			//   * held and in progress -> SILVER, he is still part of the chain with nothing to give;
			//   * COMPLETED -> no marker at all, which retail states outright (see the branch below).
			if (held is { State: QuestState.Active, GoalRequired: > 0 } && held.GoalProgress >= held.GoalRequired)
				return TurnIn;

			// ⛔ A COMPLETED quest draws NO marker. Retail is explicit about it: in
			// xxx_mage-necromancer_login_play3 Tina carries '?' (0x04) at t=59.3 and 69.6, quest 10287 completes at
			// t=101.7, and at t=102.0 she is set to 0x00 - three packets, the instant it finishes. This branch used to
			// return SILVER for `held != null`, which is true of a completed quest too, so every giver a character had
			// ever finished wore a permanent "available soon" marker. Found on a level-80 with the whole Destiny line
			// done: every Tortage giver was silver.
			if (held is { State: QuestState.Completed })
				return None;

			// Held and still in progress: silver, he is part of the chain with nothing to give right now.
			if (held != null)
				return Unavailable;

			return giverQuest != null && !await IsLineReady(giverQuest, quests)
				? Unavailable
				: Available;
		}

		// The QUEST ITEM the killer needs off `npcName`'s corpse - the item id of their active LootFromNpc goal targeting
		// it (#261), or null when they have no such goal. A non-null result forces the bag drop (the item must be
		// lootable, not a 50/50 roll) and names what to put in it. Both goals in the game data want an actual key:
		// Saddur -> 193186 "Acheronian Gate Key", Scavenger Tuut -> 319224 "Scavenger's Key", each item type 113 (Quest).
		public async Task<uint?> ActiveLootGoalItemFor(uint characterId, string npcName, uint npcTemplateId = 0)
		{
			if (string.IsNullOrEmpty(npcName) && npcTemplateId == 0)
				return null;

			// One batched read rather than a query per active quest - this sits on the LOOT path, so it ran per kill
			// that could drop a bag.
			foreach (var (quest, def) in await _database.GetCharacterQuestsWithDefinitions(characterId, QuestState.Active))
			{
				// #521: template first, name second - the same test MatchingLootGoal applies when the item is actually
				// taken. The two must agree, or the drop is forced for a goal the take then refuses to advance.
				var goal = QuestProgress.InPlayOrder(def.Goals).FirstOrDefault();
				if (goal is not { GoalType: QuestGoalType.LootFromNpc } || !QuestProgress.TargetsNpc(goal, npcTemplateId, npcName))
					continue;

				// A goal that names no item cannot be satisfied: the objective is RECEIVING the item, and there is none
				// to receive or to match a take against (QuestProgress.MatchingLootGoal). Forcing a bag drop for it just
				// produced an empty bag that looked lootable and advanced nothing. Every LootFromNpc goal in the data
				// names its item, so this is a gap in the imported quest rather than a shape to support.
				if (goal.QuestItemId is not { } questItemId || questItemId == 0)
				{
					_logger.LogError("Quest {Quest} goal {Goal} loots '{Npc}' but names no QuestItemId, so it can never complete - the goal needs its item harvested.", quest.QuestId, goal.GoalId,
						npcName);
					return null;
				}

				return questItemId;
			}

			return null;
		}

		// Refresh the entity's cached quest-id sets from the DB - what the NPC visibility gates read.
		public async Task LoadActiveQuestIds(WorldEntity entity)
		{
			var characterId = entity.Client.Session.Character?.Id;
			if (characterId == null)
				return;

			var quests = await _database.GetCharacterQuests(characterId.Value);
			entity!.ActiveQuestIds.Clear();
			entity.CompletedQuestIds.Clear();
			foreach (var quest in quests)
			{
				if (quest.State == QuestState.Active)
					entity.ActiveQuestIds.Add(quest.QuestId);
				else if (quest.State == QuestState.Completed)
					entity.CompletedQuestIds.Add(quest.QuestId); // #262: drives "hide once done" NPC gating
			}
		}

		// #559: whether `quest`'s line has actually reached it - every EARLIER step of the same quest line completed.
		// This is what separates the gold '!' from the silver one for a quest the player does not hold: retail greys a
		// giver whose quest exists but is not yet reachable, rather than drawing no marker.
		//
		// ⚠️ JournalSequence 0 is a legitimate captured value, not "unordered" - 74 captured quests carry it and 182
		// lines have it on every step (see GetQuestDefinitionsByLine). A line ordered entirely by 0s cannot say what
		// comes first, so a 0 on either side is skipped rather than guessed at: an unorderable line stays GOLD, which is
		// the behaviour before this gate existed. Only a genuine lower sequence gates.
		private async Task<bool> IsLineReady(QuestDefinition quest, CharacterQuest[] quests)
		{
			return quest.JournalSequence == 0 || LineReached(quest, await _database.GetQuestDefinitionsByLine(quest.QuestLineId), quests);
		}

		// The rule itself, split from the fetch so it can be tested against the real quest lines without a database.
		public static bool LineReached(QuestDefinition quest, IEnumerable<QuestDefinition> line, CharacterQuest[] quests)
		{
			foreach (var step in line)
			{
				if (step.JournalSequence == 0 || step.JournalSequence >= quest.JournalSequence)
					continue;

				if (Array.Find(quests, q => q.QuestId == step.QuestId) is not { State: QuestState.Completed })
					return false;
			}

			return true;
		}
	}
}
