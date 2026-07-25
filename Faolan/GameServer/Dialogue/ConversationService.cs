using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
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

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;
		private readonly WorldBroadcaster _broadcaster;

		public ConversationService(IDatabaseRepository database, ILogger logger, WorldBroadcaster broadcaster, QuestProgressionService questProgression)
		{
			_database = database;
			_logger = logger;
			_broadcaster = broadcaster;
			_questProgression = questProgression;
		}

		// The player picked option `answerIndex` in the active conversation: navigate to the child screen at the
		// extended answer-path and serve it. An un-walked branch (no captured screen) closes the window gracefully so
		// the client never hangs. No-op when no conversation is active.
		public async Task AdvanceConversation(INetworkClient client, uint answerIndex)
		{
			// An active conversation means all three slots are set; guarding them here states that invariant once instead
			// of defaulting each to 0 further down (a 0 handle/type would silently look up the wrong screen).
			var path = client.Session.ConversationAnswerPath;
			if (path == null || client.Session.ConversationNpcHandle is not { } npcHandle || client.Session.ConversationTypeId is not { } typeId)
				return;

			var childPath = path.Length == 0
				? answerIndex.ToString()
				: $"{path}>{answerIndex}";
			var screen = await PickConversationScreen(client, client.Session.ConversationNpcName, typeId, childPath,
				client.Session.SpawnedNpcTemplatesByHandle.GetValueOrDefault(npcHandle));
			if (screen == null)
			{
				_logger.LogInformation("Conversation: no captured screen for npc={Name} path='{Path}' (un-walked branch); closing.", client.Session.ConversationNpcName, childPath);
				await SendConversationClose(client);
				return;
			}

			await ServeScreen(client, screen, npcHandle);
		}

		// Serves `npcName`'s greeting screen through the live CONSTRUCT path (PickConversationScreen -> ServeScreen,
		// rebuilt via KnubotCodec re-addressed to `npcHandle`) - the #188 dialogue-VM entry the .npcsay GM command reuses
		// instead of replaying a baked captured line. Returns false when the NPC has no matching screen for `typeId`.
		public async Task<bool> ServeGreeting(INetworkClient client, string npcName, uint typeId, uint npcHandle)
		{
			var greeting = await PickConversationScreen(client, npcName, typeId, "", client.Session.SpawnedNpcTemplatesByHandle.GetValueOrDefault(npcHandle));
			if (greeting == null)
				return false;

			await ServeScreen(client, greeting, npcHandle);
			return true;
		}

		// Starts an NPC conversation from its greeting: routes by the clicked NPC's stable NAME (handles are
		// session-volatile) to the dialogue's "" screen and serves it. Returns true if a greeting was found+served.
		// Runs for world-burst (beach) AND create-world sessions; the create-world scripted intro stays the fallback
		// for NPCs with no captured dialogue. The nodes live in the DB (ConversationNode/ConversationPacket), imported
		// from captures by Faolan.Importer. (Option picks after the greeting are KnubotAnswers -> AdvanceConversation.)
		public async Task<bool> TryReplayConversation(INetworkClient client, uint typeId, uint field3, uint clickedNpcHandle)
		{
			if (client.Session is { WorldEntered: false, EntryMode: not WorldEntryMode.CreateWorld })
				return false;

			// The client sends TalkToNpc(field3=0) [greeting] then a TalkToNpc(field3!=0) the same instant (retail
			// answers that second one with nothing). Only the greeting opens the dialogue; a non-greeting click while a
			// conversation is already active is absorbed (return true, no reply) so it doesn't reset or close it.
			if (field3 != 0)
				return client.Session.ConversationAnswerPath != null;

			// #217: absorb the reflexive greeting the client re-fires right after we close (it keeps the NPC
			// targeted and re-talks with no new SelectDeselect). Stays suppressed until the player re-selects the
			// NPC, so a deliberate re-talk still opens. This is what makes "unknown path -> just exit" actually stay
			// exited instead of instantly re-triggering.
			if (clickedNpcHandle != 0 && client.Session.ConversationEndedNpcHandle == clickedNpcHandle)
			{
				_logger.LogInformation("Conversation: suppressed the reflexive post-close greeting for NPC 0x{NpcTemplate:X} (#217; re-select to talk again).", clickedNpcHandle);
				return true;
			}

			if (!client.Session.SpawnedNpcNamesByHandle.TryGetValue(clickedNpcHandle, out var npcName))
				return false; // unknown NPC (not spawned this session) -> create-world scripted intro fallback

			// Turn-in (#47): a CHAINLESS quest whose objective is already done completes when talking to its giver - the
			// reply is the reward greeting and the journal moves to Completed. (Chained stages supersede below instead.)
			await TryTurnInQuests(client, npcName);

			// Pick the greeting in the player's CURRENT (pre-completion) quest state so a talk-stage shows its OWN
			// dialogue: e.g. Casilda's "I'll protect you now" one-off is gated on HasQuest(13480), so it must be picked
			// while 13480 is still active (#264).
			var greeting = await PickConversationScreen(client, npcName, typeId, "", client.Session.SpawnedNpcTemplatesByHandle.GetValueOrDefault(clickedNpcHandle));
			if (greeting == null)
				return false;

			await ServeScreen(client, greeting, clickedNpcHandle);

			// Quest chain (#261/#264): NOW complete the TalkToNpc goal (e.g. freeing Casilda, 13480 -> escort 12407),
			// AFTER serving the greeting - so the freeing dialogue shows once, then the stage completes; the next talk
			// sees the post-freeing (completed) state and gets the post-quest greeting. Matches retail (capture: open ->
			// supersede -> options).
			await _questProgression.AdvanceTalkGoals(client, npcName);
			return true;
		}

		// Picks the conversation-screen variant whose quest-state CONDITION matches the live player (quest-gated
		// dialogue, issue #37). Most screens have a single unconditional variant - fast path. When conditioned variants
		// exist, evaluate each (conditioned-first) against the character's quest states and serve the first that
		// matches; the unconditional (null-gate) variant is the fallback. A variant with no matching quest is skipped,
		// so a stale gate can't hijack a player who isn't in that state.
		private async Task<ConversationNode?> PickConversationScreen(INetworkClient client, string npcName, uint typeId, string answerPath, uint clickedTemplateId)
		{
			var variants = await _database.GetConversationScreens(npcName, typeId, answerPath, clickedTemplateId);
			if (variants.Length == 0)
				return null;
			if (variants.Length == 1 || variants.All(v => v.ConditionQuestId == null))
				return variants[0]; // GetConversationScreens orders the unconditional fallback last

			var character = client.Session.Character;
			var quests = character == null
				? []
				: await _database.GetCharacterQuests(character.Id);

			ConversationNode? fallback = null;
			foreach (var v in variants)
			{
				if (v.ConditionQuestId == null)
				{
					fallback = v;
					continue;
				}

				if (QuestConditionMet(v, quests))
				{
					_logger.LogInformation("Conversation: npc={Name} path='{Path}' matched conditioned variant (quest {Quest} {State}).", npcName, answerPath, v.ConditionQuestId,
						v.ConditionQuestState);
					return v;
				}
			}

			return fallback;
		}

		// Reaching a conversation node that GRANTS a quest accepts it for the player: persist it ACTIVE and return its
		// definition so ServeScreen can deliver the single QuestFullUpdate inline (the retail grant shape - see above).
		// Returns null when the player already has the quest (re-serving the node must not re-grant) or it has no
		// definition (state is still persisted; there is just nothing to render).
		private async Task<QuestDefinition?> PrepareQuestGrant(INetworkClient client, ConversationNode node)
		{
			var character = client.Session.Character;

			if (node.GrantQuestId is not { } questId)
				return null; // this screen grants no quest

			var existing = await _database.GetCharacterQuests(character.Id);
			if (existing.Any(q => q.QuestId == questId))
				return null; // already accepted (active) or completed - don't re-grant

			var def = await _database.GetQuestDefinition(questId);

			// Seed the quest's first objective so kills can advance it (#47): GoalRequired = the goal record's decoded
			// count (QuestGoalDefinition.RequiredCount), GoalTargetName = the seeded kill target (QuestGoalDefinition.
			// TargetName - server-side, sourced per quest since it's not on the wire). With both set,
			// QuestProgressionService.AdvanceKillGoals matches a kill of that NPC and advances/completes the quest (e.g. Casilda's
			// 13479 = kill 1 "Scavenger Tuut"). A talk/collect goal leaves TargetName null - no kill matching, count 0.
			var firstGoal = def?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
			var goalRequired = firstGoal is { RequiredCount: > 0 }
				? firstGoal.RequiredCount
				: 0u;
			var goalTargetName = firstGoal?.TargetName;

			await _database.SetCharacterQuest(character.Id, questId, QuestState.Active, goalRequired: goalRequired, goalTargetName: goalTargetName);
			_logger.LogInformation("Quest {QuestId} granted to {Name} from {NpcTemplate}'s dialogue (goalRequired={Req}, target={Target}).", questId, character.Name, node.NpcName, goalRequired,
				goalTargetName ?? "-");
			return def;
		}

		// Sends the two retail grant packets in order: the QuestLogList (populates the J-panel - all active quests,
		// just-granted tracked first) then the QuestFullUpdate (the quest's detail). Order + presence both matter: the
		// list is what makes the quest appear in the log (decoded from the grant capture).
		private async Task SendQuestGrantPackets(INetworkClient client, QuestDefinition grantDef)
		{
			var character = client.Session.Character;
			var active = await _database.GetCharacterQuests(character.Id);
			var ids = new List<uint>
			{
				grantDef.QuestId
			};
			ids.AddRange(active.Where(q => q.State == QuestState.Active && q.QuestId != grantDef.QuestId).Select(q => q.QuestId));

			await WorldBroadcaster.SendQuestLogList(client, grantDef, ids);
			await _broadcaster.SendQuestFullUpdate(client, grantDef);
		}

		// Serves one dialogue SCREEN: re-addresses its captured response packets to the live player + clicked NPC and
		// sends them, then records the screen as the session's position in the dialogue tree. Each ConversationNode IS
		// one screen (the greeting, or an option's reply); the player's next KnubotAnswer picks the child.
		private async Task ServeScreen(INetworkClient client, ConversationNode node, uint clickedNpcHandle)
		{
			var liveInstance = client.Session.Character.Id;

			// The session slots are regenerated into each screen's remoteformat XML from LIVE values (the stored screen
			// has them blank, #417): the player who's actually talking, their guild (empty when guildless), and the NPC
			// guild label, which carries OUR realm name rather than the capture's retail server. Character.Guild is a
			// navigation nobody loads (lazy loading is off), so the guild is fetched by id.
			var character = client.Session.Character;
			var playerName = character?.Name ?? "";
			var guildName = character?.GuildId is { } guildId
				? (await _database.GetGuild(guildId))?.Name ?? ""
				: "";
			var npcGuildName = RemoteFormatXml.NotInGuildLabel(character != null
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
			var grantDef = node.GrantQuestId is > 0
				? await PrepareQuestGrant(client, node)
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
					await SendQuestGrantPackets(client, grantDef);
					grantSent = true;
				}

				// The sender instance (retail varies it per conversation) is a stored column; the record body is built
				// from the typed screen columns re-addressed to the live player + clicked NPC.
				var senderInstance = p.SenderInstance ?? DefaultSenderInstance;
				if (subOp == ConversationSubOpcode.SetDialogueCamera)
					await SendDialogueCamera(client, senderInstance, liveInstance, clickedNpcHandle, DialogueCameraBody.BuildParams(p));
				else
					await SendConstructedKnubot(client, senderInstance, liveInstance, BuildKnubotFromColumns(p, liveInstance, clickedNpcHandle, playerName, guildName, npcGuildName));
			}

			if (grantDef != null && !grantSent) // no AnswerList on this screen - send after the (terminal) screen
				await SendQuestGrantPackets(client, grantDef);

			// #241: accepting the quest changed the player's quest state, so the giver's '!' must update in-place
			// (retail clears it to 0x00 the instant you accept - capture frame 698) rather than persisting until the
			// NPC re-streams. The clicked NPC IS the giver and is in view.
			if (grantDef != null)
			{
				await _broadcaster.RefreshQuestGiverMarker(client, clickedNpcHandle);
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
				_logger.LogInformation("Conversation ended (npc={Name}, typeId={TypeId}, path='{Path}') for NPC 0x{NpcTemplate:X}.", node.NpcName, node.TypeId, node.AnswerPath, clickedNpcHandle);
				return;
			}

			client.Session.ConversationNpcName = node.NpcName;
			client.Session.ConversationTypeId = node.TypeId;
			client.Session.ConversationAnswerPath = node.AnswerPath;
			client.Session.ConversationNpcHandle = clickedNpcHandle;

			_logger.LogInformation("Conversation screen (npc={Name}, typeId={TypeId}, path='{Path}') -> {Packets} packet(s) for NPC 0x{NpcTemplate:X}.", node.NpcName, node.TypeId, node.AnswerPath,
				node.Packets.Count, clickedNpcHandle);
		}

		// Completes any quest GIVEN by `npcName` whose objective is done (Active + GoalProgress >= GoalRequired): marks
		// it Completed and sends QuestIIR state 2 -> 4 so the client journal moves it to the completed tab (#47). The
		// kill only advances the objective; the quest is marked Completed here, on TURN-IN at the giver. Called as the
		// player opens the giver's greeting, so the greeting condition (HasCompletedQuest) then serves the reward line.
		private async Task TryTurnInQuests(INetworkClient client, string npcName)
		{
			var character = client.Session.Character;
			if (character == null || npcName == null)
				return;

			var givers = await _database.GetQuestGiverMap(); // NpcName -> the quest id it grants
			if (!givers.TryGetValue(npcName, out var givenQuestId))
				return;

			var held = (await _database.GetCharacterQuests(character.Id)).FirstOrDefault(q => q.QuestId == givenQuestId);
			if (held == null || held.State != QuestState.Active || held.GoalRequired == 0 || held.GoalProgress < held.GoalRequired)
				return;

			await _database.SetCharacterQuest(character.Id, held.QuestId, QuestState.Completed, held.GoalProgress, held.GoalRequired, held.GoalTargetName);
			await _broadcaster.SendQuestIir(client, held.QuestId, WorldQuest.StateActive);
			await _broadcaster.SendQuestIir(client, held.QuestId, WorldQuest.StateCompleted);
			_logger.LogInformation("Quest {Quest} turned in to {NpcTemplate} by {Char} (objective {Progress}/{Required}).", held.QuestId, npcName, character.Name, held.GoalProgress,
				held.GoalRequired);
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

			client.Session.ConversationAnswerPath = null!;
			client.Session.ConversationNpcName = null!;
			client.Session.ConversationTypeId = null;
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
			var captured = KnubotCodec.ParseStringList(KnubotCloseChatWindowBody);
			var record = KnubotCodec.BuildCloseChatWindow(id, target ?? captured.NpcHandle, captured.Strings, captured.Env);
			return SendConstructedKnubot(client, agentInstance, id, record);
		}

		// Construct a Knubot record from the STRUCTURED columns (#188) - the screen is rebuilt from its typed
		// ConversationScreen rows (RemoteFormatXml.Build) with the live player name/guild regenerated in. The rows are
		// gated byte-exact at import (ConversationScreenColumns.ToRow), so this build is authoritative - no Data-path
		// fallback. Byte-identical to the raw-capture construct for the same inputs (ConversationConstructTests).
		internal static byte[] BuildKnubotFromColumns(ConversationPacket p, uint liveInstance, uint npcHandle, string playerName, string guildName, string npcGuildName = "")
		{
			var env = p.Env ?? 0;
			var screens = ConversationScreenColumns.ToScreens(p.Screens); // ordered RemoteFormatXml.Screen list

			if (p.SubOpcode == ConversationSubOpcode.KnubotOpenChatWindow)
				return KnubotCodec.BuildOpenChatWindow(liveInstance, npcHandle, p.Flag ?? 0, env);
			if (p.SubOpcode == ConversationSubOpcode.KnubotAppendText)
				return KnubotCodec.BuildAppendText(liveInstance, npcHandle, Screen(0), p.Field ?? 1, p.Flag ?? 1, env);

			// AnswerList + CloseChatWindow: the string-list grammar.
			var strings = screens.Select((_, i) => Screen(i)).ToList();
			return KnubotCodec.BuildStringList((uint)p.SubOpcode!.Value, liveInstance, npcHandle, strings, env);

			byte[] Screen(int i)
			{
				// With no live player (empty name) keep the screen's stored content as-is - the construct-vs-capture gate
				// feeds it capture-parsed rows and expects them back verbatim; otherwise regenerate the session slots
				// (player name, their guild, the NPC guild label) from the live values.
				var screen = string.IsNullOrEmpty(playerName)
					? screens[i]
					: RemoteFormatXml.WithLivePlayer(screens[i], playerName, guildName ?? "", npcGuildName ?? "");
				return Encoding.Latin1.GetBytes(RemoteFormatXml.Build(screen));
			}
		}

		// A conditioned variant applies when the player holds ConditionQuestId in state ConditionQuestState - the two
		// shapes retail's dialogue Lua actually used: HasCompletedQuest(X) == (X, Completed), and HasQuest(X) and not
		// HasCompletedQuest(X) == (X, Active). Kept internal-static so ConversationConditionTests can pin the gate.
		internal static bool QuestConditionMet(ConversationNode node, IReadOnlyCollection<CharacterQuest> quests)
		{
			return node.ConditionQuestId is { } questId && quests.Any(q => q.QuestId == questId && q.State == node.ConditionQuestState);
		}

		// Frames a constructed Knubot record exactly as retail does (verified against the captures): the conversation
		// runs sender `GameAgent:<conversationAgentInstance>` -> receiver `GameInterface:<playerHandle>` on opcode
		// 0x2000. Retail VARIES the GameAgent instance per conversation (0x07..0x5a observed; 0x08 the most common),
		// and the client addresses its KnubotAnswer back to whatever instance sent the screen - so we PRESERVE the
		// captured packet's sender instance rather than hardcoding one, matching the replay path byte-for-byte.
		private static Task SendConstructedKnubot(INetworkClient client, uint senderInstance, uint liveInstance, byte[] record)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, senderInstance), Endpoints.Endpoint(Endpoints.GameInterfaceType, liveInstance),
				GameInterfaceOpcodes.DispatchPassBlob, record));
		}

		// Constructs the dialogue camera (DispatchSetDialogueCamera 0x2015) from the structured columns (#188):
		// GameCharAgent:<agentInstance> -> GameCharInterface:<player>, body [0xC350][npc][0xC350][player][params].
		// Byte-identical to the captured packet re-addressed (gated by the construct test).
		private static Task SendDialogueCamera(INetworkClient client, uint agentInstance, uint liveInstance, uint npcHandle, byte[] cameraParams)
		{
			return client.SendAsync(DialogueCameraBody.BuildDialogueCameraPacket(agentInstance, liveInstance, npcHandle, cameraParams));
		}
	}
}
