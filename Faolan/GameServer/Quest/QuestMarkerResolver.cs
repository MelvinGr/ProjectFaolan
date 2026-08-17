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
		// DispatchSetQuestMarker (0x2008) state byte, and the enum is the CLIENT's own - recovered from
		// `n3VisualDynel::SetQuestMarker` (`FUN_005affb0`), which switches the byte straight onto a particle effect
		// (`GamecodeClient_t::SetQuestMarker` @01395460 resolves the ID32 and passes the byte through untouched):
		//
		//   0x00  (no effect - clears whatever was there)   0x02  quest_new_active        = GOLD '!'
		//   0x01  quest_new_inactive       = SILVER '!'     0x04  quest_progress_active   = GOLD '?'
		//   0x03  quest_progress_inactive  = SILVER '?'  <- exists in the client, NEVER sent by retail
		//
		// So the axes are `new`(!) vs `progress`(?), each `active` (bright) or `inactive` (the greyed form the
		// community calls the grey '!'). ⭐ `0x03` is real and unused: zero packets carry it across the whole corpus
		// (`--report quest-markers`), so retail's server collapses whatever it was for onto another value. We do not
		// send it either, and a constant for a value nothing emits would be a claim we cannot support.
		//
		// ⛔ The client applies NO level logic here - it plays the effect it is told to. The community's "grey means
		// come back at a higher level", and the far-below-you quest that draws nothing, are both SERVER choices of
		// which byte to send, so neither is recoverable by more RE of the client (#848).
		// [V1: tools/ghidra/DecompileFns.java FNS=005affb0]
		//
		// The silver state was missed by #241, which only saw three values. It is the second most common on the wire:
		// of the 301 replies in xxx_mage-necromancer_login_play2, 181 are 0x00, 74 are 0x01, 30 are 0x02, 16 are 0x04.
		//
		// ⚠️ 0x00 and 0x01 are NOT interchangeable: 52 NPCs in that capture only ever returned 0x00, and they are the
		// ones no quest touches at all. Silver says "a chain runs through this NPC"; none says "this NPC is scenery".
		//
		// ⚠️ **Silver is measured to be mostly NOT about what this NPC grants** (#848, `--report quest-markers` over
		// 34,837 resolved marker packets). Of the 456 silver packets retail sent, only 31% are on an NPC that grants
		// anything at all, while **85% are on an NPC some UNTOUCHED quest's goal will send the player to** - a
		// relation this class does not read. Gold '!' is the opposite and matches what we do: 68% grant a quest the
		// player has neither taken nor finished, and *zero* are on a grant the player already holds.
		//
		// So `MarkerStateFor` returning None for any non-giver is a real gap: 38 of the 62 NPC templates retail ever
		// lit grant nothing, and we would send 0x00 for every one of them.
		//
		// ⭐⭐ The condition is the quest LINE, not the quest. Casilda looked like the counterexample - 13480 targets
		// her and is untouched while she is dark 178 times - until the relation is read one level up: 13480 is in
		// line 1054, the line the player is already ON. Of the 257 packets about an NPC whose every line the player
		// had already started, **zero** carried silver (229 dark, 27 gold '?', 1 gold '!'). So silver means "a chain
		// you have not begun runs through here", and once you are on that chain the NPC goes dark until it is your
		// turn to act on them. Turach stays silver because lines 222 and 1305 pass through him untouched - not
		// because of an unharvested grant.
		//
		// ⚠️ NECESSARY, not sufficient: an untouched line lights an NPC only 20% of the time. The second gate is
		// LEVEL, and it is two-sided - retail lights a marker only for a quest at or near the player's own level:
		//
		//   delta (player - quest)   -76/-75/-57  |   -7   |  -2..+1  |  +56  |  +57/+75
		//   lit / observations          0 / 243   | 0 / 10 | 299/382  | 0/127 |  0 / 37
		//
		// Both cutoffs are BRACKETED, not pinned - the corpus is sparse in delta and the gaps either side of the band
		// hold no observation at all: a quest ABOVE the player is dark somewhere in `-7 < T <= -2`, one BELOW them
		// somewhere in `1 < T <= 56`. Picking a number inside an unobserved gap would be inventing data, which is why
		// nothing here acts on any of it yet (#848).
		//
		// ⚠️ That measurement only exists because the completed-quest ARCHIVE is read out of the login snapshot: the
		// live QuestIIR traffic is ONE packet in the level-80 captures, so without it every quest they ever finished
		// reads as "never taken" and their darkness looks like a level rule when it is just completion.
		public const byte None = 0x00, Unavailable = 0x01, Available = 0x02, TurnIn = 0x04;

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// Quest-giver NPC template id -> EVERY quest it grants, cached once (small set).
		private Dictionary<uint, uint[]> _questGivers = null!;

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
			if (!_questGivers.TryGetValue(npc.NpcTemplateId, out var questIds))
				return None;

			// ⭐ The marker describes the NEXT quest this NPC can GRANT you, and a giver is not one quest - Turach hands
			// out two, Laranga two, Cramaleico two. So the answer is a fold over all of them:
			//
			//   * one of theirs is held with its objective done   -> '?' (0x04), turn it in here;
			//   * some grant is still OUTSTANDING (neither held nor completed):
			//       - its line has reached it -> GOLD '!' (0x02), it is yours to take;
			//       - it has not             -> SILVER '!' (0x01), "there is something here you cannot take yet";
			//   * nothing outstanding at all                      -> NO marker. The giver is spent.
			//
			// That last clause is what a carried quest hits, and it is the whole difference between Casilda and Alyssa.
			// Casilda grants exactly ONE quest, so the instant you accept it she has nothing left to offer and goes
			// blank - `Blablatest_mage-necromancer-create_casilda_quest_first_stage` and SIX other captures record the
			// same 0x02 -> 0x00 -> 0x04 -> 0x00 walk on her, and across all 275 captures she is 0x00 x178 / 0x02 x170 /
			// 0x04 x21 and NEVER 0x01. Alyssa sits on a four-step line ending in "Report back to Alyssa" and goes
			// SILVER eight frames after granting hers (Hoxxling_..._login_gameplay2, grant frame 9603 -> 0x01 frame
			// 9611). Both are "carrying the giver's quest"; the marker differs because what is left to grant differs.
			//
			// ⚠️ Silver is therefore NOT simply "your level is too low", which is how the community documents the grey
			// '!', NOR simply "the chain has not reached them". It is "they have one you cannot take yet" and the
			// captures do not separate the two reasons - Gitea #848 holds what is still unexplained (Turach and Laranga
			// stay silver even past their last harvested grant, which fits an unharvested further quest but is not
			// proven).
			var outstanding = false;
			var offerable = false;
			foreach (var questId in questIds)
			{
				// ⛔ There is deliberately NO map gate here. `QuestDefinition.MapId` is the objective's MARKER PIN -
				// where the quest sends you - not where its giver stands, and gating on it suppressed markers retail
				// demonstrably sends: it blocks 15 of the 28 giver quests in the shipped data, among them Turach and
				// Laranga (quests on 4010, they stand on 4020), Alyssa (10800 vs 4020), Caor, Oracle Vateesa and
				// Hadrathus - and the captures show retail marking every one of those.
				//
				// The gate came from #244, where it did double duty as the thing stopping a same-NAMED NPC world-wide
				// from wearing the marker ("Halen van Dummy" is the internal name of 27 unrelated templates). That job
				// belongs to the TEMPLATE-keyed lookup above since #646, so removing the gate does not give it back.
				// docs/subsystems/quests.md already said MapId "cannot be a general match key - it is a marker pin".
				var giverQuest = await _database.GetQuestDefinition(questId);
				var held = Array.Find(quests, q => q.QuestId == questId);
				// '?' once the objective is done but the quest is still ACTIVE (awaiting turn-in at the giver - a quest
				// is only marked Completed on turn-in, not on the kill, #47/#241). It outranks the rest: a giver you can
				// hand something to says so before it advertises what it has left.
				if (held is { State: QuestState.Active, GoalRequired: > 0 } && held.GoalProgress >= held.GoalRequired)
					return TurnIn;

				// Carried, or already finished: this one is not on offer. Retail is explicit about the finished half in
				// xxx_mage-necromancer_login_play3 - Tina carries '?' at t=59.3 and 69.6, quest 10287 completes at
				// t=101.7, and she is set to 0x00 at t=102.0, three packets, the instant it finishes.
				if (held != null)
					continue;

				outstanding = true;
				offerable |= giverQuest == null || await IsLineReady(giverQuest, quests);
			}

			return !outstanding ? None : offerable ? Available : Unavailable;
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
