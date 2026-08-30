using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Dialogue
{
	// The NPC dialogue runtime: routes a TalkToNpc / KnubotAnswer to the captured conversation node for the
	// requested (NPC, typeId, answer-path), re-addresses its packets to the live player + clicked NPC, advances
	// the tree, and grants quests reached in a branch. Extracted from GameServerListener as a composed
	// collaborator; quest-journal pushes go through the WorldBroadcaster, branch conditions match the player's quest state.
	public sealed class ConversationService
	{
		// The TypeId every SERVER-INITIATED conversation node carries (#523): a server-opened window has no BCC action
		// to take an action id from. It doubles as the guard that keeps these out of the talk path.
		public const uint ServerInitiatedTypeId = 0;

		// The GameAgent sender instance to use when a packet has none stored (retail's most common value); the real
		// per-conversation instance comes from ConversationPacket.SenderInstance.
		private const uint DefaultSenderInstance = 0x08;
		// The Knubot packets that END a conversation stage: an answer list (the next options) or a close-window.

		// The Knubot packets we CONSTRUCT from the typed codec (KnubotCodec) instead of replaying+substituting (#188):
		// ALL FOUR record types - the spoken line (AppendText), the window-open, the answer list and the close. Their
		// content (XML / options / value) is lifted from the captured record and rebuilt with the LIVE player/NPC
		// endpoints - byte-identical to the substitution path (ConversationConstruct tests) but routed through the
		// typed codec, the step toward sourcing the line content from RDB/Lua instead of the captured packet.

		// A KnubotCloseChatWindow (0xC4AF39EB) body, captured verbatim. It closes the NPC chat window with an EMPTY
		// answer list (the remoteformat is id="0" with no options) - retail's "this NPC has nothing (more) to say"
		// reply. The remoteformat (incl. its key) is byte-identical across every capture, so it is a reusable
		// constant; only the player instance @offset 12 is re-addressed. See SendConversationClose.
		private static readonly byte[] KnubotCloseChatWindowBody =
		[
			0x00, 0x00, 0x00, 0x7C, 0xC4, 0xAF, 0x39, 0xEB, 0x00, 0x00, 0xC3, 0x50, 0x01, 0x31, 0x6E, 0x71, 0x00, 0x00, 0x01, 0x00, 0x00, 0xC3, 0x50, 0x00, 0x01, 0x7E, 0x03, 0x00, 0x00, 0x00,
			0x01, 0x00, 0x5F, 0x3C, 0x72, 0x65, 0x6D, 0x6F, 0x74, 0x65, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3D, 0x22, 0x30, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6F,
			0x72, 0x79, 0x3D, 0x22, 0x31, 0x30, 0x30, 0x22, 0x20, 0x6B, 0x65, 0x79, 0x3D, 0x22, 0x2E, 0x66, 0x7A, 0x54, 0x75, 0x45, 0x79, 0x69, 0x75, 0x37, 0x42, 0x26, 0x23, 0x39, 0x32, 0x3B,
			0x5A, 0x59, 0x5A, 0x4D, 0x6F, 0x71, 0x31, 0x2D, 0x22, 0x20, 0x6B, 0x6E, 0x75, 0x62, 0x6F, 0x74, 0x3D, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3E, 0x3C, 0x2F, 0x72, 0x65, 0x6D, 0x6F, 0x74,
			0x65, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x3E
		];

		private readonly IChatServerDispatcher _agents;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;
		private readonly WorldBroadcaster _broadcaster;
		private readonly NpcBroadcaster _npcs;
		private readonly QuestBroadcaster _questBroadcaster;

		// The live world model - the approach trigger asks it who is standing near the player.
		private readonly WorldSimulation _sim;
		private readonly TravelService _travel;

		// #820: a turn-in is a quest COMPLETION, so it pays the quest's stated reward the same way the objective path
		// does. Reached directly rather than through the progression service, which would only be forwarding.
		private readonly QuestRewardService _rewards;

		public ConversationService(IDatabaseRepository database, ILogger logger, WorldBroadcaster broadcaster, NpcBroadcaster npcs, QuestBroadcaster questBroadcaster, WorldSimulation sim,
			QuestProgressionService questProgression, TravelService travel, IChatServerDispatcher agents, QuestRewardService rewards)
		{
			_database = database;
			_agents = agents;
			_logger = logger;
			_broadcaster = broadcaster;
			_npcs = npcs;
			_questBroadcaster = questBroadcaster;
			_sim = sim;
			_questProgression = questProgression;
			_travel = travel;
			_rewards = rewards;
		}

		// #523: how close the player must come to an NPC for its APPROACH-triggered conversation to open. MEASURED at
		// 12.9 / 13.8 / 10.9 units across three independent captures of Saddur's beach confrontation.
		private const float ApproachRadius = 15f;

		// The NPC TEMPLATES that HAVE an approach-triggered conversation, cached on first use - a small static set, so
		// the per-move check costs one hash lookup for the overwhelming majority of players who are near nobody special.
		private HashSet<uint>? _approachNpcs;

		// #523: open a conversation the player has just walked up to an NPC for - the APPROACH trigger. Saddur's beach
		// confrontation is the worked example: "{hidegoodbye}Who stands before Saddur?" fires on approach, with no
		// click and no object.
		//
		// The radius is MEASURED, not chosen: across three independent captures the window opens at 12.9, 13.8 and 10.9
		// units from Saddur's spawn, so 15 covers all three with a little slack and stays far short of the next NPC
		// (Casilda at 153+). It is deliberately small - a generous radius would open the conversation across a room.
		//
		// ⚠️ This is proximity to the NPC, NOT a bounded-area volume. Only volume-entry is in RDB 1000053 and the two
		// are different mechanisms (#513), so nothing here needs region geometry.
		//
		// Fires ONCE per NPC per session (`ApproachedConversationNpcs`): Saddur's nodes carry no quest gate, so without
		// a latch the window would re-open on every step taken near him. The TEMPLATES that have one at all are cached
		// here (#646 - a display name would open Saddur's confrontation on any NPC that happened to share his name),
		// so a player walking past ordinary NPCs pays one hash lookup and no DB hit.
		public async Task CheckApproach(INetworkClient client, WorldEntity player, Vector3 position)
		{
			var templates = _approachNpcs ??= [.. await _database.GetApproachConversationNpcs()];
			if (templates.Count == 0 || client.Session.ConversationNodeId != null)
				return; // nothing to open, or a conversation is already up

			var zone = _sim.World.ZoneForEntity(player);
			foreach (var npc in zone.EntitiesInRange(position, ApproachRadius, player.Handle))
			{
				if (npc.IsPlayer || !templates.Contains(npc.NpcTemplateId))
					continue;
				if (!client.Session.ApproachedConversationNpcs.Add(npc.Handle))
					continue; // already opened for this NPC this session

				if (await TryOpenApproachConversation(client, npc.NpcTemplateId, npc.Handle))
				{
					_logger.LogInformation("#523: player {Char} approached {Npc} - opening its server-initiated conversation.", client.Session.Character.Name, npc.Name);
					return; // one conversation at a time
				}
			}
		}

		// The player picked option `answerIndex` in the active conversation: resolve it to the option's own LDB id on the
		// screen we last SENT, take the edge that option opens, and serve where it lands. A branch the captures never
		// walked closes the window gracefully so the client never hangs. No-op when no conversation is active.
		//
		// ⚠️ The index is resolved against the screen actually on the player's display, never persisted (#642). The
		// same screen offers different option lists in different quest states - Casilda's greeting has six - so an
		// index means six different things while the option's string id means one.
		public async Task AdvanceConversation(INetworkClient client, uint answerIndex)
		{
			// An active conversation means both slots are set; guarding them here states that invariant once instead of
			// defaulting each further down (a 0 handle would silently address the wrong NPC).
			if (client.Session.ConversationNodeId is not { } fromNodeId || client.Session.ConversationNpcHandle is not { } npcHandle)
				return;

			var npcName = client.Session.ConversationNpcName;
			if (client.Session.ConversationOptions.Count <= answerIndex)
			{
				// The client offered an option the screen we sent does not have. That is not a coverage gap, it is a
				// disagreement about what is on screen, so say so rather than closing as if the branch were un-walked.
				_logger.LogWarning("Conversation: npc={Name} answered option {Index} but the served screen offers {Count}; closing.", npcName, answerIndex,
					client.Session.ConversationOptions.Count);
				await SendConversationClose(client);
				return;
			}

			var optionId = client.Session.ConversationOptions[(int)answerIndex];
			var edge = await PickConversationEdge(client, fromNodeId, optionId);
			if (edge == null)
			{
				_logger.LogInformation("Conversation: npc={Name} has no captured branch for option {Option} out of screen 0x{Node:X8}; closing.", npcName, optionId, fromNodeId);
				await SendConversationClose(client);
				return;
			}

			// #514: LOCAL TRAVEL rides this walk, and both of its steps are dialogue picks, which is why the hook is
			// here rather than in ServeScreen (a root can never be either one).
			//
			// The FARE comes off BEFORE the screen that presents the destination list - retail's DispatchUpdateCash
			// sits immediately in front of "Your coin is good enough. Where to?" - so it is charged here, on the way in.
			// A refusal (criminal, or too poor) must not then hand the player the list: close the window instead.
			// ⚠️ The corpus has no refused traveller, so closing is the conservative reading rather than a captured one.
			if (!await _travel.TryChargeFare(client, edge.ToNodeId))
			{
				await SendConversationClose(client);
				return;
			}

			await ServeScreen(client, edge.To, npcHandle, edge.GrantQuestId);

			// The destination pick: retail closes the chat window and THEN teleports (the close is the served screen's
			// own only packet), so the trip runs after the screen, in the same "text precedes state" order the quest
			// grants follow.
			await _travel.TryTravel(client, fromNodeId, optionId);
		}

		// #523: open a conversation the SERVER initiates - one with no player TalkToNpc in front of it, served because
		// some EVENT fired. Casilda's city-door farewell is the worked example: the player uses the door, the escort
		// completes, and retail opens her window unprompted.
		//
		// ⚠️ The APPROACH-triggered counterpart - walking up to the NPC is what opens it, with no object to use.
		// Saddur's beach confrontation ("{hidegoodbye}Who stands before Saddur?") is the worked example. This is
		// proximity to the NPC, NOT entry into a bounded area; only volume-entry lives in RDB 1000053, and the two are
		// different mechanisms (Gitea #513's own research says so), which is why this needs no region.
		//
		// Returns false when the NPC has no such root - or, for the object trigger, when it is not spawned for this
		// player, which is the right outcome for an escortee who is not there.
		public async Task<bool> TryOpenApproachConversation(INetworkClient client, uint npcTemplateId, uint npcHandle)
		{
			return await OpenRoot(client, npcTemplateId, ConversationTrigger.Approach, null, npcHandle) != null;
		}

		public async Task<bool> TryOpenTriggeredConversation(INetworkClient client, uint npcTemplateId, uint worldObjectId)
		{
			// #646: the escortee is found by TEMPLATE, so a same-named bystander can no longer answer for them. The
			// session already carries this map beside the name one, written on the adjacent line.
			var handle = client.Session.SpawnedNpcTemplatesByHandle
				.FirstOrDefault(kv => kv.Value == npcTemplateId)
				.Key;
			if (handle == 0)
				return false; // the escortee is not spawned for this player - the conversation simply does not open

			return await OpenRoot(client, npcTemplateId, ConversationTrigger.UseWorldObject, worldObjectId, handle) != null;
		}

		// Opens one of an NPC's dialogue ROOTS (#642) and serves its screen. The three ways retail opens a window - the
		// player clicking, the player using an object, the player walking up - differ only in the trigger they look up
		// with, so they are one method rather than three near-copies. Several roots can share a trigger and differ only
		// by quest gate (Casilda has six talk roots); the gate rule is the same one the branches use.
		//
		// Returns the root it served, or null when the NPC has none for this trigger. The ROOT rather than a bool
		// because the caller needs the NPC's display name afterwards (#646, for the talk-goal match) and the root is
		// where that label lives - reading it back off the session would be wrong, since a terminal screen ends the
		// conversation and clears it.
		private async Task<ConversationEntry?> OpenRoot(INetworkClient client, uint npcTemplateId, ConversationTrigger trigger, uint? worldObjectId, uint npcHandle)
		{
			var roots = await _database.GetConversationEntries(npcTemplateId, trigger, worldObjectId);
			if (roots.Length == 0)
				return null;

			// Remember the door/gate that opened this window, for a script event fired when it CLOSES: Casilda's
			// `RunToTortage` sends her to the object the player just used, and the handler that knew which one has
			// already returned by then (#839).
			client.Session.ConversationTriggerObjectId = worldObjectId;

			var character = client.Session.Character;
			var quests = character == null
				? []
				: await _database.GetCharacterQuests(character.Id);

			// Retail's greeting is not one line: an NPC opens with a full welcome the first time a player opens his
			// window and a terse one every time after - Elethia's "Hello.{social greet}" becomes "What now?" (#645). Only
			// four NPCs in the corpus were captured saying both, so the column is null on nearly every root and this
			// narrows nothing; where it is set, the roots for the OTHER meeting must not be candidates at all.
			var repeat = !client.Session.GreetedConversationNpcs.Add(npcTemplateId);
			var eligible = Array.FindAll(roots, r => r.RepeatGreeting == null || r.RepeatGreeting == repeat);
			if (eligible.Length == 0)
				eligible = roots; // the captures only ever caught this NPC on the other meeting - serve what we have

			var opening = PickByLatestQuest(eligible, quests);
			if (opening == null)
			{
				// ⚠️ No gate matched. For an NPC whose greetings are quest-GATED, that means the player is in a state
				// no capture ever recorded - and its unconditional root is NOT a default, it is the greeting harvested
				// for somebody else's progress. Serving it hands the player plausible, wrong dialogue: a level-80 with
				// the Destiny line finished got Tina's mid-quest opener, because hers is the only ungated root we hold.
				// Say so instead. This is the silent-fallback rule (CLAUDE.md): an uncaptured state is a gap to report,
				// not a hole to fill with the nearest thing.
				//
				// ⚠️ But only when the player is PAST the gated states, not before them. The ungated root is the
				// harvested greeting for a character with no chain progress (see
				// docs/subsystems/knubot-conversation-packets.md - "the least-informed class is the unconditional
				// fallback"), so a FRESH character matches no gate for the ordinary reason and must still be served
				// it, or they can never start the chain. It is only once they have COMPLETED a gating quest that
				// falling through means "a state after everything we captured".
				//
				// Scoped to NPCs that HAVE a gate: the 107 NPCs whose only root is unconditional are unaffected, and
				// their one greeting really is for everyone. 10 NPCs carry both shapes and are the ones this covers.
				var pastTheGates = Array.Exists(eligible,
					r => r.ConditionQuestId is { } gate && quests.Any(q => q.QuestId == gate && q.State == QuestState.Completed));

				if (pastTheGates)
				{
					_logger.LogInformation("Conversation: npc={Name} ({Template}) has quest-gated greetings but none match this player's state - no capture of it, so nothing is served.",
						roots[0].NpcName, npcTemplateId);

					if (character != null)
						await _agents.CharacterSay(npcHandle, $"({roots[0].NpcName}: no dialogue captured for this quest state yet)", [character.Id]);

					return null;
				}

				opening = Array.Find(eligible, r => r.ConditionQuestId == null);
			}

			if (opening == null)
				return null;

			if (opening.ConditionQuestId != null)
				_logger.LogInformation("Conversation: npc={Name} ({Template}) opened its {Trigger} root gated on quest {Quest} {State}.", opening.NpcName, npcTemplateId, trigger,
					opening.ConditionQuestId, opening.ConditionQuestState);

			await ServeScreen(client, opening.Node, npcHandle);
			return opening;
		}

		// Starts an NPC conversation from its greeting: routes by the clicked NPC's stable TEMPLATE id (#646 - handles
		// are session-volatile and display names are ambiguous) to the NPC's Talk root and serves the screen it opens on. Returns true if a greeting was
		// found+served. Runs for world-burst (beach) AND create-world sessions; the create-world scripted intro stays
		// the fallback for NPCs with no captured dialogue. The graph lives in the DB (ConversationEntry ->
		// ConversationNode -> ConversationEdge), imported from captures by Faolan.Importer. (Option picks after the
		// greeting are KnubotAnswers -> AdvanceConversation.)
		public async Task<bool> TryReplayConversation(INetworkClient client, uint field3, uint clickedNpcHandle)
		{
			if (client.Session is { WorldEntered: false, EntryMode: not WorldEntryMode.CreateWorld })
				return false;

			// The client sends TalkToNpc(field3=0) [greeting] then a TalkToNpc(field3!=0) the same instant (retail
			// answers that second one with nothing). Only the greeting opens the dialogue; a non-greeting click while a
			// conversation is already active is absorbed (return true, no reply) so it doesn't reset or close it.
			if (field3 != 0)
				return client.Session.ConversationNodeId != null;

			// #217: absorb the reflexive greeting the client re-fires right after we close (it keeps the NPC
			// targeted and re-talks with no new SelectDeselect). Stays suppressed until the player re-selects the
			// NPC, so a deliberate re-talk still opens. This is what makes "unknown path -> just exit" actually stay
			// exited instead of instantly re-triggering.
			if (clickedNpcHandle != 0 && client.Session.ConversationEndedNpcHandle == clickedNpcHandle)
			{
				_logger.LogInformation("Conversation: suppressed the reflexive post-close greeting for NPC 0x{NpcTemplate:X} (#217; re-select to talk again).", clickedNpcHandle);
				return true;
			}

			if (!client.Session.SpawnedNpcTemplatesByHandle.TryGetValue(clickedNpcHandle, out var npcTemplateId))
				return false; // unknown NPC (not spawned this session) -> create-world scripted intro fallback

			// Pick the greeting in the player's CURRENT (pre-completion) quest state so a talk-stage shows its OWN
			// dialogue: e.g. Casilda's "I'll protect you now" one-off is gated on HasQuest(13480), so it must be picked
			// while 13480 is still active (#264).
			if (await OpenRoot(client, npcTemplateId, ConversationTrigger.Talk, null, clickedNpcHandle) is not { } opened)
				return false;

			// #260 - SERVE FIRST, COMPLETE AFTER. Both quest-completion paths run here, after the text is on the wire,
			// because that is the order retail sends them in. Two independent captures agree, on different characters
			// and different quests:
			//
			//   compeltefirstquest  t=57.3 : KnubotOpenChatWindow -> KnubotAppendText "...{resolvegoal BJGY}{delay 1}"
			//                                -> QuestGoal Satisfied -> QuestIIR 2 -> QuestJournal -> QuestIIR 4
			//                                -> QuestFullUpdate (the next stage)
			//   ...questcomplete    t=373.3: byte-for-byte the same order
			//
			// The GRANT side is the same shape (…questcomplete t=334.5: AppendText -> MainQuestUpdate ->
			// QuestFullUpdate), so "text precedes state" is the rule for the whole dialogue, not a completion quirk.
			//
			// This ordering is also what makes the greeting PICK correct, which is the bug #260 actually reported.
			// TryTurnInQuests used to run BEFORE PickConversationScreen: it flipped the quest to Completed, so the
			// pick then matched the HasCompletedQuest variant and the player was handed the post-quest "already done"
			// line instead of the turn-in line. Retail has no separate reward node to reach for - the turn-in text IS
			// the in-progress variant, carrying an inline {resolvegoal <4CC>} directive - so serving pre-completion
			// serves exactly the right screen.
			await TryTurnInQuests(client, npcTemplateId);

			// Quest chain (#261/#264): complete the TalkToNpc goal (e.g. freeing Casilda, 13480 -> escort 12407) - so
			// the freeing dialogue shows once, then the stage completes; the next talk sees the post-freeing
			// (completed) state and gets the post-quest greeting.
			//
			// ⚠️ Matched by TEMPLATE first, name second (#521). It used to be name-only, because a TalkToNpc goal's
			// own TrailingRefId is not the NPC at all - on the 12 talk goals that carry one it is the item the player
			// HANDS OVER (quest 12371's Turach goal carries 193400, the Shipwrecked stone brick) - so the goal had no
			// id to match against. `QuestGoalDefinition.TargetTemplateId` is that id: resolved at import from the name
			// the client's own RDB data states. Both are passed because only 278 goals have resolved so far; the rest
			// name NPCs on maps that have not been imported and still match on the string.
			await _questProgression.AdvanceTalkGoals(client, opened.NpcName, npcTemplateId);
			return true;
		}

		// The branch out of `fromNodeId` that clicking option `optionId` takes, or null when the captures never walked it.
		//
		// The gate lives on the BRANCH rather than on the screen (#642): two harvested walks reached Turach's 2832 by
		// the same option with identical screen content, one granting quest 12371 and one granting nothing, and that
		// difference belongs to how you got there. Most options have a single unconditional edge - fast path.
		private async Task<ConversationEdge?> PickConversationEdge(INetworkClient client, uint fromNodeId, uint optionId)
		{
			var branches = Array.FindAll(await _database.GetConversationEdges(fromNodeId), e => e.OptionId == optionId);
			if (branches.Length == 0)
				return null;
			if (branches.Length == 1)
				return branches[0];

			var character = client.Session.Character;
			var quests = character == null
				? []
				: await _database.GetCharacterQuests(character.Id);

			var matched = PickByLatestQuest(branches, quests);
			if (matched != null)
			{
				_logger.LogInformation("Conversation: option {Option} out of screen 0x{Node:X8} took the branch gated on quest {Quest} {State}.", optionId, fromNodeId, matched.ConditionQuestId,
					matched.ConditionQuestState);
				return matched;
			}

			return Array.Find(branches, e => e.ConditionQuestId == null);
		}

		// The gated row to use when SEVERAL gates pass at once, or null when none does.
		//
		// Gates overlap by construction: the importer derives them from the quest state each screen was captured in
		// (#639), and a player deep in a chain holds every earlier quest of it too - Casilda's escort greeting is gated
		// on 12407 Active, and the two greetings after it sit on top of a 12407 that is still active. The beat the
		// player is actually ON is the quest they accepted MOST RECENTLY, and CharacterQuest is insert-ordered, so the
		// highest row id among the passing gates is that beat. Without this the pick fell to whatever order the query
		// returned, which is a database detail rather than a rule.
		internal static T? PickByLatestQuest<T>(IReadOnlyCollection<T> variants, IReadOnlyCollection<CharacterQuest> quests) where T : class, IQuestGated
		{
			T? best = null;
			var bestRank = 0u;
			foreach (var v in variants)
			{
				if (v.ConditionQuestId is not { } questId)
					continue;

				var held = quests.FirstOrDefault(q => q.QuestId == questId && q.State == v.ConditionQuestState);
				if (held == null || (best != null && held.Id <= bestRank))
					continue;

				best = v;
				bestRank = held.Id;
			}

			return best;
		}

		// Serves one dialogue SCREEN: re-addresses its captured response packets to the live player + clicked NPC and
		// sends them, then records the screen as the session's position in the graph, together with the OPTION IDS it
		// put on the player's display - which is what the next KnubotAnswer's index is resolved against (#642).
		//
		// `grantQuestId` is the quest the BRANCH that got here grants, not a property of the screen: the same screen
		// reached two ways can grant on one route and not the other (Turach's 2832).
		private async Task ServeScreen(INetworkClient client, ConversationNode node, uint clickedNpcHandle, uint? grantQuestId = null)
		{
			var liveInstance = client.Session.Character.Id;

			// The session slots are regenerated into each screen's remoteformat XML from LIVE values (the stored screen
			// has them blank, #417): the player who's actually talking, their guild (empty when guildless), and the NPC
			// guild label, which carries OUR realm name rather than the capture's retail server. Character.Guild is a
			// navigation nobody loads (lazy loading is off), so the guild is fetched by id.
			var character = client.Session.Character;
			var playerName = character?.Name ?? "";
			// #418: the grammatical gender the localized text declines the player's name against. Every captured
			// screen carries flags="m" because every capture's player was male, so carrying the stored flag declined
			// a female character as male in every line of dialogue naming her.
			var playerSex = character?.Sex ?? Sex.None;
			var guildName = character?.GuildId is { } guildId
				? (await _database.GetGuild(guildId))?.Name ?? ""
				: "";
			var npcGuildName = RemoteFormatSession.NotInGuildLabel(character != null
				? (await _database.GetRealm(character.RealmId))?.Name ?? ""
				: "");

			// A captured screen that offers options (AnswerList 0xF164A4A5) sometimes ALSO carries a trailing
			// CloseChatWindow (0xC4AF39EB) - the captured session closed the window right after. Replaying that close
			// auto-shuts the dialogue the player should be picking from (observed: Casilda's greeting opens then
			// instantly closes, can't accept). When the screen has a real AnswerList, drop the spurious close; a pure
			// terminal screen (CloseChatWindow only) is kept.
			var orderedPackets = node.Packets.OrderBy(p => p.Sequence).ToList();
			var hasAnswerList = orderedPackets.Any(p => p.SubOpcode == ConversationSubOpcode.KnubotAnswerList);

			// Remember the GameAgent instance this conversation's packets are sent from (retail varies it per
			// conversation). SendConversationClose must reuse it - a close from a different instance (e.g. the old
			// hardcoded 0x08) doesn't match the dialogue and the client re-fires the greeting (#217).
			if (orderedPackets.Count > 0)
				client.Session.ConversationAgentInstance = orderedPackets[0].SenderInstance ?? DefaultSenderInstance;

			// A grant node accepts a quest. Retail announces it INTERLEAVED inside the grant screen, in this order:
			// AppendText -> QuestLogList (0x504B7924, the J-panel list) -> QuestFullUpdate (0x63268258, the detail) ->
			// AnswerList, with NO QuestIIR and NO QuestJournal (capture randochar-login-get-casilda-quest frame 687-688).
			// The QuestLogList is what POPULATES the log; sending only the full-update (the detail) leaves the log empty.
			var grantDef = grantQuestId is > 0 and { } granted
				? await _questProgression.Grant(client, granted, $"{node.NpcName}'s dialogue")
				: null;
			var grantSent = false;

			foreach (var p in orderedPackets)
			{
				// Every conversation packet is stored STRUCTURED (#188): the importer requires a recognised SubOpcode, so
				// the columns are the SOLE construction path - no raw-Data fallback. A null SubOpcode is an import bug,
				// and all 596 shipped packets have one. Logging and skipping the line was the worst of both: it claimed
				// to surface the problem while still handing the player a conversation with a line missing from the
				// middle of it, which reads as a content bug rather than a broken import.
				var subOp = p.SubOpcode
					?? throw new InvalidOperationException($"Conversation packet #{p.Sequence} (node {node.Id}) has no SubOpcode - the importer did not columnize it, so this dialogue cannot be built.");

				if (hasAnswerList && subOp == ConversationSubOpcode.KnubotCloseChatWindow)
					continue; // skip the spurious close so the option list stays interactive

				// Deliver the quest packets immediately BEFORE the screen's AnswerList, mirroring retail's order.
				if (grantDef != null && !grantSent && subOp == ConversationSubOpcode.KnubotAnswerList)
				{
					await _questProgression.SendGrantPackets(client, grantDef);
					grantSent = true;
				}

				// The sender instance (retail varies it per conversation) is a stored column; the record body is built
				// from the typed screen columns re-addressed to the live player + clicked NPC.
				var senderInstance = p.SenderInstance ?? DefaultSenderInstance;
				if (subOp == ConversationSubOpcode.SetDialogueCamera)
					await SendDialogueCamera(client, senderInstance, liveInstance, clickedNpcHandle, CameraArgsOf(p));
				else
					await SendConstructedKnubot(client, senderInstance, liveInstance, BuildKnubotFromColumns(p, liveInstance, clickedNpcHandle, playerName, guildName, npcGuildName, playerSex));
			}

			if (grantDef != null && !grantSent) // no AnswerList on this screen - send after the (terminal) screen
				await _questProgression.SendGrantPackets(client, grantDef);

			// #241: accepting the quest changed the player's quest state, so the giver's '!' must update in-place
			// (retail clears it to 0x00 the instant you accept - capture frame 698) rather than persisting until the
			// NPC re-streams. The clicked NPC IS the giver and is in view.
			if (grantDef != null)
			{
				await _questBroadcaster.RefreshQuestGiverMarker(client, clickedNpcHandle);
				// #261: accepting a quest may reveal quest-gated NPCs (e.g. Scavenger Tuut once you hold Casilda's kill
				// quest) - re-run interest so they stream in immediately, not only after the player next moves.
				await _broadcaster.RefreshQuestVisibility(client);
			}

			// A pure terminal screen (a CloseChatWindow with NO AnswerList) ENDS the dialogue: the window is now shut
			// on the client. Recording it as the active position left the session "inside" a finished conversation,
			// so the greeting the client fires right after re-opened it (#217). End instead - this arms the post-close
			// guard (EndConversation) so that follow-up greeting is absorbed.
			var isTerminalClose = !hasAnswerList && orderedPackets.Any(p => p.SubOpcode == ConversationSubOpcode.KnubotCloseChatWindow);
			if (isTerminalClose)
			{
				client.Session.ConversationNpcHandle = clickedNpcHandle; // ensure the guard arms for THIS npc
				EndConversation(client);
				_logger.LogInformation("Conversation ended (npc={Name}, screen 0x{Node:X8}) for NPC 0x{NpcTemplate:X}.", node.NpcName, node.Id, clickedNpcHandle);
				await FireScriptEvents(client, node, clickedNpcHandle);
				return;
			}

			client.Session.ConversationNpcName = node.NpcName;
			client.Session.ConversationNodeId = node.Id;
			// The option buttons as the client now draws them, so the next KnubotAnswer's INDEX resolves to the
			// option's own LDB id. Taken from the screen we just sent rather than looked up again: the index is only
			// meaningful against what is actually on the display.
			client.Session.ConversationOptions = [.. orderedPackets
				.Where(p => p.SubOpcode == ConversationSubOpcode.KnubotAnswerList)
				.OrderBy(p => p.Sequence)
				.LastOrDefault()?.Screens.OrderBy(s => s.Sequence).Select(s => s.ScreenId) ?? []];
			client.Session.ConversationNpcHandle = clickedNpcHandle;

			_logger.LogInformation("Conversation screen (npc={Name}, screen 0x{Node:X8}) -> {Packets} packet(s), {Options} option(s) for NPC 0x{NpcTemplate:X}.", node.NpcName, node.Id,
				node.Packets.Count, client.Session.ConversationOptions.Count, clickedNpcHandle);
		}

		// #839: the SCRIPTED EVENTS this creature's dialogue fires, run once the window has CLOSED - which is where
		// retail runs them. In the capture Saddur's `Fight` action rides the second-to-last screen, and the fight
		// itself starts ~3s after `KnubotCloseChatWindow`: hate list, threat, CombatMode 4, then the first swing.
		//
		// ⭐ Dispatched on the row's ACTION, never on the event STRING. The string is retail's and names one quest's
		// fiction - `RunToTortage` fits nobody but Casilda - so matching it in C# would mean a new `const string` and a
		// new branch for every dialogue that wants the same behaviour. The action is what the strings have in common,
		// so a future `RunToKhemi` or `SpawnBandits` is wired by stamping `NpcScriptEvent.Action` and nothing here
		// changes. An UNMAPPED event (four of the seven shipped) logs rather than passing silently - an unimplemented
		// script that says nothing is indistinguishable from one that does not exist.
		private async Task FireScriptEvents(INetworkClient client, ConversationNode node, uint npcHandle)
		{
			var events = await _database.GetNpcScriptEvents(node.NpcTemplateId);
			if (events.Length == 0)
				return;

			foreach (var script in events)
			{
				var name = script.Event;
				if (script.Action == NpcScriptAction.TurnHostile)
				{
					// The flags, plus the one record only this path can send: the simulation's own aggro pass then
					// hate-lists the player, draws combat mode and swings - the same burst it produces for any other
					// aggro, and the one retail sends here. Authoring that a second time from this path would be a
					// second copy of the combat opening.
					//
					// The collision-group restate is NOT part of that burst and has to go out here, because it is the
					// only thing that distinguishes this flip from an ordinary mob's aggro: an ordinary mob spawned in
					// the mob group already, and this one spawned as a townsperson. Without it Saddur swung at the
					// player while still drawing peaceful and refusing to be hit (#839) - see BroadcastNpcTurnedHostile
					// for the corpus measurement that dates it to exactly here, ahead of the hate list.
					if (_sim.World.ZoneForEntity(_sim.PlayerWorldEntity(client)!)?.TryGet(npcHandle, out var npc) == true && npc != null)
					{
						npc.Disposition = WorldEntity.DispositionHostile;
						npc.Hostile = true;
						await _npcs.BroadcastNpcTurnedHostile(npc);
						_logger.LogInformation("Script: {Name} fired \"{Event}\" - it turns on {Player}.", node.NpcName, name, client.Session.Character?.Name);
					}
					else
					{
						_logger.LogWarning("Script: {Name} fired \"{Event}\" but handle 0x{Handle:X8} is not a live entity in the player's zone - it stays friendly.", node.NpcName, name,
							npcHandle);
					}

					continue;
				}

				if (script.Action == NpcScriptAction.LeaveWorld)
				{
					// The escort is over, so the escortee heads for the gate the player opened her window with and is
					// gone. Retail plays it out - MoveTask then n3ToClientQuit - rather than blinking her away, which
					// is why the exit point is the TRIGGER OBJECT and not a hard-coded pair of coordinates: the object
					// that opened this conversation IS the place the escort was leading to (the same reasoning
					// `TryOpenTriggeredConversation` already uses to find the escortee at all).
					if (ExitPointFor(client, npcHandle) is { } exit && _sim.World.ZoneForEntity(_sim.PlayerWorldEntity(client)!)?.TryGet(npcHandle, out var leaving) == true && leaving != null)
					{
						_npcs.ScheduleScriptedExit(leaving, exit);
						_logger.LogInformation("Script: {Name} fired \"{Event}\" - she heads for the gate and leaves.", node.NpcName, name);
					}
					else
					{
						_logger.LogWarning("Script: {Name} fired \"{Event}\" but there is no trigger object to walk to - she stays put.", node.NpcName, name);
					}

					continue;
				}

				if (script.Action == NpcScriptAction.RevealQuestTargets)
				{
					// The creatures the player's quests are waiting on enter the world. Expressed as "re-evaluate what
					// the quests ask for" rather than "spawn creature N", because the event names no creature and
					// retail's own second path agrees: on a relog Scavenger Tuut arrives in the ordinary world-entry
					// stream with NO scry event, gated on the quest being held. So the quest says WHO and this says
					// WHEN - which is also why a future spawn script needs no code here, only a gated goal and this
					// action stamped on its event.
					await _broadcaster.RefreshQuestVisibility(client);
					_logger.LogInformation("Script: {Name} fired \"{Event}\" - its quests' creatures are now in the world.", node.NpcName, name);
					continue;
				}

				_logger.LogInformation("Script: {Name} fired \"{Event}\", which is harvested but carries no mapped action yet (#839).", node.NpcName, name);
			}
		}

		// Where a leaving NPC walks to: the world object that opened this conversation. Null when the player opened it
		// themselves, which is the honest answer - a farewell with no door behind it has no gate to head for.
		private Vector3? ExitPointFor(INetworkClient client, uint npcHandle)
		{
			if (client.Session.ConversationTriggerObjectId is not { } objectId)
				return null;

			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			foreach (var candidate in _sim.World.ZoneForEntity(player).WorldObjects)
			{
				if (candidate.Row.Id == objectId)
					return candidate.Position;
			}

			return null;
		}

		// Completes any quest GIVEN by the NPC TEMPLATE whose objective is done (Active + GoalProgress >= GoalRequired): marks
		// it Completed and sends QuestIIR state 2 -> 4 so the client journal moves it to the completed tab (#47). The
		// kill only advances the objective; the quest is marked Completed here, on TURN-IN at the giver. Called AFTER
		// the greeting has been served (#260) - the state change follows the text on the wire, as in retail, and the
		// greeting is therefore picked while the quest is still Active so the player sees the turn-in line rather than
		// the post-quest "already done" one.
		private async Task TryTurnInQuests(INetworkClient client, uint npcTemplateId)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var givers = await _database.GetQuestGiverMap(); // NpcTemplateId -> EVERY quest id it grants
			if (!givers.TryGetValue(npcTemplateId, out var givenQuestIds))
				return;

			// ⚠️ Every quest the NPC grants, not just one: the map used to collapse to Min(GrantQuestId), so a giver who
			// hands out two - Turach's "The Quarry" and "The Guard", Laranga's "The Blacksmith" and "The Barmaid" - could
			// only ever turn the lower-numbered one in, and the other stayed Active forever with its objective done.
			var quests = await _database.GetCharacterQuests(character.Id);
			foreach (var givenQuestId in givenQuestIds)
			{
				var held = Array.Find(quests, q => q.QuestId == givenQuestId);
				if (held == null || held.State != QuestState.Active || held.GoalRequired == 0 || held.GoalProgress < held.GoalRequired)
					continue;

				await _database.SetCharacterQuest(character.Id, held.QuestId, QuestState.Completed, held.GoalProgress, held.GoalRequired, held.GoalTargetName);
				await _questBroadcaster.SendQuestIir(client, held.QuestId, QuestCodec.StateActive);
				await _questBroadcaster.SendQuestIir(client, held.QuestId, QuestCodec.StateCompleted);

				// #820: THIS is where a turn-in quest completes, so this is where it pays. The guard above only lets a
				// quest through while it is still Active, so the flip - and the payment - happen exactly once.
				await _rewards.GrantCompletionRewards(client, held.QuestId);
				_logger.LogInformation("Quest {Quest} turned in to template {NpcTemplate} by {Char} (objective {Progress}/{Required}).", held.QuestId, npcTemplateId, character.Name,
					held.GoalProgress, held.GoalRequired);
			}
		}

		// Closes the NPC chat window gracefully (KnubotCloseChatWindow with an empty answer list), re-addressed to the
		// live player. Sent when a TalkToNpc has NO matching conversation node, so the client never hangs mid-dialogue
		// waiting for a reply that won't come - the "advance a step with no captured branch -> chat locks, can't ESC"
		// bug. (Conversation replay is coverage-bound: (Mode, Field3) is quest-state-dependent and the captures cover
		// only some states per NPC, so an un-captured branch is expected; closing the window is the right answer.)
		// Clears the session's conversation state - the player is no longer in a dialogue. Sends nothing; use this
		// when the CLIENT ended the conversation (it sent KnubotCloseChatWindow), so we just tear down our state and
		// don't echo a close back. Leaving state set is what let a stale conversation be re-entered (#217).
		public static void EndConversation(INetworkClient client)
		{
			// #217: remember which NPC we just closed so the reflexive post-close greeting (the client re-fires one
			// with no new SelectDeselect) is absorbed rather than re-opening the dialogue. Cleared when the player
			// re-selects an NPC (SelectDeselect) = genuine intent to talk again.
			if (client.Session.ConversationNpcHandle is not null)
				client.Session.ConversationEndedNpcHandle = client.Session.ConversationNpcHandle;

			client.Session.ConversationNodeId = null;
			client.Session.ConversationOptions = [];
			client.Session.ConversationNpcName = null!;
			client.Session.ConversationNpcHandle = null;
			client.Session.ConversationAgentInstance = null;
		}

		public static Task SendConversationClose(INetworkClient client, uint? npcHandle = null)
		{
			// #217: the close's KnubotBase header carries the NPC handle the client uses to tear down THAT dialogue
			// and RELEASE its dialogue camera. We used to re-address only the player instance and keep the captured
			// (stale) NPC handle, so the client never matched the live dialogue: the camera stayed locked (soft-lock,
			// relog to recover) and the client re-fired TalkToNpc (the conversation re-opened). Retail's close carries
			// the LIVE NPC handle (e.g. 0x6A0A Kalanthes). Use the active dialogue's NPC (captured BEFORE EndConversation
			// clears it), or the caller's clicked NPC for the no-node-on-click close.
			// Which window to close: the caller's NPC, else the ACTIVE dialogue's (captured before EndConversation
			// clears it), else the captured handle as a last resort - one coalescing chain rather than two 0-checks.
			var target = npcHandle ?? client.Session.ConversationNpcHandle;
			// The close must ride the SAME GameAgent instance the conversation's screens used (#217); 0x08 is the
			// fallback when there was no active conversation. Captured BEFORE EndConversation clears it.
			// 0x08 is the fallback when there was no active conversation.
			var agentInstance = client.Session.ConversationAgentInstance ?? 0x08;
			EndConversation(client); // end any active conversation
			var id = client.Session.Character.Id;

			// CONSTRUCT the close via KnubotCodec from the captured empty-remoteformat reply, re-addressed to the live
			// player AND the live NPC (the captured string is reused verbatim), on the same sender (GameAgent:0x08).
			// AsSpan(4): the captured blob is the whole carrier payload, and its leading [u32 len] belongs to the
			// sender now (#530 rule 2) - SendConstructedKnubot writes it back on.
			var captured = (KnubotCloseChatWindowIIR)KnubotCodec.Parse(KnubotCloseChatWindowBody.AsSpan(4));
			var record = KnubotCodec.Build(captured with
			{
				PlayerInstance = id,
				NpcHandle = target ?? captured.NpcHandle
			});

			return SendConstructedKnubot(client, agentInstance, id, record);
		}

		// Construct a Knubot record from the STRUCTURED columns (#188) - the screen is rebuilt from its typed
		// ConversationScreen rows (RemoteFormatScreenCodec.Build) with the live player name/guild regenerated in. The
		// rows are gated byte-exact at import (ConversationScreenCodec.Parse), so this build is authoritative - no
		// Data-path fallback. Byte-identical to the raw-capture construct for the same inputs
		// (ConversationConstructTests). The record carries NO length prefix - the sender frames it (#530 rule 2).
		internal static byte[] BuildKnubotFromColumns(ConversationPacket p, uint liveInstance, uint npcHandle, string playerName, string guildName, string npcGuildName = "",
			Sex playerSex = Sex.None)
		{
			var env = p.Env ?? 0;
			var screens = ConversationScreenColumns.ToScreens(p.Screens); // ordered RemoteFormatScreen list

			if (p.SubOpcode == ConversationSubOpcode.KnubotOpenChatWindow)
				return KnubotCodec.Build(new KnubotOpenChatWindowIIR(liveInstance, npcHandle, env, p.Flag ?? 0));
			if (p.SubOpcode == ConversationSubOpcode.KnubotAppendText)
				return KnubotCodec.Build(new KnubotAppendTextIIR(liveInstance, npcHandle, env, p.Field ?? 1, Screen(0), p.Flag ?? 1));

			// CloseChatWindow carries ONE screen behind a TextType word, not a string list (#809) - the stored Field
			// is that word. Every captured close holds exactly one screen, so this picks the same bytes it always did.
			if (p.SubOpcode == ConversationSubOpcode.KnubotCloseChatWindow)
				return KnubotCodec.Build(new KnubotCloseChatWindowIIR(liveInstance, npcHandle, env, p.Field ?? 1, screens.Count == 0 ? [] : Screen(0)));

			return KnubotCodec.Build(new KnubotAnswerListIIR(liveInstance, npcHandle, env, screens.Select((_, i) => Screen(i)).ToList()));

			byte[] Screen(int i)
			{
				// With no live player (empty name) keep the screen's stored content as-is - the construct-vs-capture gate
				// feeds it capture-parsed rows and expects them back verbatim; otherwise regenerate the session slots
				// (player name, their guild, the NPC guild label) from the live values.
				var screen = string.IsNullOrEmpty(playerName)
					? screens[i]
					: RemoteFormatSession.WithLivePlayer(screens[i], playerName, guildName ?? "", npcGuildName ?? "", playerSex);

				return Encoding.Latin1.GetBytes(RemoteFormatScreenCodec.Build(screen));
			}
		}

		// A conditioned variant applies when the player holds ConditionQuestId in state ConditionQuestState - the two
		// shapes retail's dialogue Lua actually used: HasCompletedQuest(X) == (X, Completed), and HasQuest(X) and not
		// HasCompletedQuest(X) == (X, Active). Kept internal-static so ConversationConditionTests can pin the gate.
		internal static bool QuestConditionMet(IQuestGated gated, IReadOnlyCollection<CharacterQuest> quests)
		{
			return gated.ConditionQuestId is { } questId && quests.Any(q => q.QuestId == questId && q.State == gated.ConditionQuestState);
		}

		// Frames a constructed Knubot record exactly as retail does (verified against the captures): the conversation
		// runs sender `GameAgent:<conversationAgentInstance>` -> receiver `GameInterface:<playerHandle>` on opcode
		// 0x2000. Retail VARIES the GameAgent instance per conversation (0x07..0x5a observed; 0x08 the most common),
		// and the client addresses its KnubotAnswer back to whatever instance sent the screen - so we PRESERVE the
		// captured packet's sender instance rather than hardcoding one, matching the replay path byte-for-byte.
		// The carrier's [u32 len] is written HERE (#530 rule 2) - KnubotCodec emits the record alone.
		private static Task SendConstructedKnubot(INetworkClient client, uint senderInstance, uint liveInstance, byte[] record)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, senderInstance), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, liveInstance),
				GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
		}

		// Constructs the dialogue camera (DispatchSetDialogueCamera 0x2015) from the structured columns (#188):
		// GameCharAgent:<agentInstance> -> GameCharInterface:<player>, body [0xC350][npc][0xC350][player][params].
		// Byte-identical to the captured packet re-addressed (gated by the construct test).
		internal static Rpc BuildDialogueCameraPacket(uint agentInstance, uint liveInstance, uint npcHandle, CameraArgs camera)
		{
			return new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, agentInstance), Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceType, liveInstance),
				SetDialogueCameraCodec.Method, SetDialogueCameraCodec.Build(npcHandle, liveInstance, camera));
		}

		private static Task SendDialogueCamera(INetworkClient client, uint agentInstance, uint liveInstance, uint npcHandle, CameraArgs camera)
		{
			return client.SendAsync(BuildDialogueCameraPacket(agentInstance, liveInstance, npcHandle, camera));
		}

		// The camera arguments as they sit on the wire, rebuilt from the packet row's typed columns (#410 - these
		// were one opaque 10-byte CameraParams blob).
		internal static CameraArgs CameraArgsOf(ConversationPacket row)
		{
			ArgumentNullException.ThrowIfNull(row);

			return new CameraArgs(row.ShotSize ?? 0, row.ShotType ?? 0, row.ShotEmphasis ?? 0, row.ShotFov ?? 0, row.ShotTilt ?? 0, row.Autoface == true, row.HeadPaddingY ?? 0f);
		}
	}
}
