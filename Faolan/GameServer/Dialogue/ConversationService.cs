using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Dialogue
{
	// The NPC dialogue runtime: routes a TalkToNpc / KnubotAnswer to the captured conversation node for the
	// requested (NPC, typeId, answer-path), re-addresses its packets to the live player + clicked NPC, advances
	// the tree, and grants quests reached in a branch. Extracted from GameServerListener as a composed
	// collaborator; quest-journal pushes go through the WorldBroadcaster, branch conditions through the Lua VM.
	public sealed class ConversationService
	{
		// The Knubot packets that END a conversation stage: an answer list (the next options) or a close-window.
		private const uint KnubotAnswerListSubOpcode = (uint)GameServer0X2000RespondsOpcodes.KnubotAnswerListIIR;

		// The Knubot packets we CONSTRUCT from the typed codec (KnubotCodec) instead of replaying+substituting (#188):
		// ALL FOUR record types - the spoken line (AppendText), the window-open, the answer list and the close. Their
		// content (XML / options / value) is lifted from the captured record and rebuilt with the LIVE player/NPC
		// endpoints - byte-identical to the substitution path (ConversationConstruct tests) but routed through the
		// typed codec, the step toward sourcing the line content from RDB/Lua instead of the captured packet.
		private const uint KnubotAppendTextSubOpcode = (uint)GameServer0X2000RespondsOpcodes.KnubotAppendTextIIR;
		private const uint KnubotCloseChatWindowSubOpcode = (uint)GameServer0X2000RespondsOpcodes.KnubotCloseChatWindowIIR;
		private const uint KnubotOpenChatWindowSubOpcode = (uint)GameServer0X2000RespondsOpcodes.KnubotOpenChatWindowIIR;

		// A KnubotCloseChatWindow (0xC4AF39EB) body, captured verbatim. It closes the NPC chat window with an EMPTY
		// answer list (the remoteformat is id="0" with no options) - retail's "this NPC has nothing (more) to say"
		// reply. The remoteformat (incl. its key) is byte-identical across every capture, so it is a reusable
		// constant; only the player instance @offset 12 is re-addressed. See SendConversationClose.
		private static readonly byte[] KnubotCloseChatWindowBody =
		[
			0x00, 0x00, 0x00, 0x7C, 0xC4, 0xAF, 0x39, 0xEB, 0x00, 0x00, 0xC3, 0x50, 0x01, 0x31, 0x6E, 0x71, 0x00, 0x00,
			0x01, 0x00, 0x00, 0xC3, 0x50, 0x00, 0x01, 0x7E, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x5F, 0x3C, 0x72, 0x65,
			0x6D, 0x6F, 0x74, 0x65, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3D, 0x22, 0x30, 0x22, 0x20,
			0x63, 0x61, 0x74, 0x65, 0x67, 0x6F, 0x72, 0x79, 0x3D, 0x22, 0x31, 0x30, 0x30, 0x22, 0x20, 0x6B, 0x65, 0x79,
			0x3D, 0x22, 0x2E, 0x66, 0x7A, 0x54, 0x75, 0x45, 0x79, 0x69, 0x75, 0x37, 0x42, 0x26, 0x23, 0x39, 0x32, 0x3B,
			0x5A, 0x59, 0x5A, 0x4D, 0x6F, 0x71, 0x31, 0x2D, 0x22, 0x20, 0x6B, 0x6E, 0x75, 0x62, 0x6F, 0x74, 0x3D, 0x22,
			0x30, 0x22, 0x20, 0x20, 0x3E, 0x3C, 0x2F, 0x72, 0x65, 0x6D, 0x6F, 0x74, 0x65, 0x66, 0x6F, 0x72, 0x6D, 0x61,
			0x74, 0x3E
		];

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly LuaScriptVm _luaVm;
		private readonly QuestProgressionService _questProgression;
		private readonly WorldBroadcaster _broadcaster;

		public ConversationService(IDatabaseRepository database, ILogger logger, WorldBroadcaster broadcaster, QuestProgressionService questProgression, LuaScriptVm luaVm)
		{
			_database = database;
			_logger = logger;
			_broadcaster = broadcaster;
			_questProgression = questProgression;
			_luaVm = luaVm;
		}

		// The player picked option `answerIndex` in the active conversation: navigate to the child screen at the
		// extended answer-path and serve it. An un-walked branch (no captured screen) closes the window gracefully so
		// the client never hangs. No-op when no conversation is active.
		public async Task AdvanceConversation(INetworkClient client, uint answerIndex)
		{
			var path = client.Session.ConversationAnswerPath;
			if (path == null)
				return;

			var childPath = path.Length == 0
				? answerIndex.ToString()
				: $"{path}>{answerIndex}";
			var screen = await PickConversationScreen(client, client.Session.ConversationNpcName, client.Session.ConversationTypeId, childPath);
			if (screen == null)
			{
				_logger.LogInformation("Conversation: no captured screen for npc={Name} path='{Path}' (un-walked branch); closing.", client.Session.ConversationNpcName, childPath);
				await SendConversationClose(client);
				return;
			}

			await ServeScreen(client, screen, client.Session.ConversationNpcHandle);
		}

		public Task SendConversationClose(INetworkClient client, uint npcHandle = 0)
		{
			// #217: the close's KnubotBase header carries the NPC handle the client uses to tear down THAT dialogue
			// and RELEASE its dialogue camera. We used to re-address only the player instance and keep the captured
			// (stale) NPC handle, so the client never matched the live dialogue: the camera stayed locked (soft-lock,
			// relog to recover) and the client re-fired TalkToNpc (the conversation re-opened). Retail's close carries
			// the LIVE NPC handle (e.g. 0x6A0A Kalanthes). Use the active dialogue's NPC (captured BEFORE EndConversation
			// clears it), or the caller's clicked NPC for the no-node-on-click close.
			if (npcHandle == 0)
				npcHandle = client.Session.ConversationNpcHandle;
			// The close must ride the SAME GameAgent instance the conversation's screens used (#217); 0x08 is the
			// fallback when there was no active conversation. Captured BEFORE EndConversation clears it.
			var agentInstance = client.Session.ConversationAgentInstance != 0
				? client.Session.ConversationAgentInstance
				: 0x08;
			EndConversation(client); // end any active conversation
			var id = client.Session.Account.ClientInstance;

			// CONSTRUCT the close via KnubotCodec from the captured empty-remoteformat reply, re-addressed to the live
			// player AND the live NPC (the captured string is reused verbatim), on the same sender (GameAgent:0x08).
			var captured = KnubotCodec.ParseStringList(KnubotCloseChatWindowBody);
			var record = KnubotCodec.BuildCloseChatWindow(id,
				npcHandle != 0
					? npcHandle
					: captured.NpcHandle,
				captured.Strings,
				captured.Env);
			return SendConstructedKnubot(client, agentInstance, id, record);
		}

		// Serves `npcName`'s greeting screen through the live CONSTRUCT path (PickConversationScreen -> ServeScreen,
		// rebuilt via KnubotCodec re-addressed to `npcHandle`) - the #188 dialogue-VM entry the .npcsay GM command reuses
		// instead of replaying a baked captured line. Returns false when the NPC has no matching screen for `typeId`.
		public async Task<bool> ServeGreeting(INetworkClient client, string npcName, uint typeId, uint npcHandle)
		{
			var greeting = await PickConversationScreen(client, npcName, typeId, "");
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
			if (!client.Session.WorldEntered && !client.Session.IsCreateWorldSession)
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
			if (clickedNpcHandle != 0 && clickedNpcHandle == client.Session.ConversationEndedNpcHandle)
			{
				_logger.LogInformation("Conversation: suppressed the reflexive post-close greeting for NPC 0x{Npc:X} (#217; re-select to talk again).", clickedNpcHandle);
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
			var greeting = await PickConversationScreen(client, npcName, typeId, "");
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

		// Picks the conversation-screen variant whose Lua CONDITION passes for the live player (quest/tag-gated
		// dialogue, issue #37). Most screens have a single unconditional variant - fast path. When conditioned
		// variants exist, evaluate each (conditioned-first) against the character's tags + completed quests via the
		// LuaScriptVm, and serve the first that passes; the unconditional "" variant is the fallback. A condition
		// script error is treated as "doesn't match" (permissive=false) so a bad gate can't hijack every player.
		private async Task<ConversationNode> PickConversationScreen(INetworkClient client, string npcName, uint typeId, string answerPath)
		{
			var variants = await _database.GetConversationScreens(npcName, typeId, answerPath);
			if (variants.Count == 0)
				return null;
			if (variants.Count == 1 || variants.All(v => string.IsNullOrEmpty(v.ConditionScript)))
				return variants[0]; // GetConversationScreens orders the unconditional fallback last

			var character = client.Session.Character;
			var tags = character != null
				? await _database.GetCharacterTags(character.Id)
				: [];
			var quests = character != null
				? await _database.GetCharacterQuests(character.Id)
				: [];
			var completed = quests.Where(q => q.State == QuestState.Completed).Select(q => q.QuestId);
			var active = quests.Where(q => q.State == QuestState.Active).Select(q => q.QuestId);
			var player = new GameLuaPlayer(tags, completed, active);

			ConversationNode fallback = null;
			foreach (var v in variants)
			{
				if (string.IsNullOrEmpty(v.ConditionScript))
				{
					fallback = v;
					continue;
				}

				if (_luaVm.RunCondition(v.ConditionScript, player, false))
				{
					_logger.LogInformation("Conversation: npc={Name} path='{Path}' matched conditioned variant.", npcName, answerPath);
					return v;
				}
			}

			return fallback;
		}

		// Reaching a conversation node that GRANTS a quest accepts it for the player: persist it ACTIVE and return its
		// definition so ServeScreen can deliver the single QuestFullUpdate inline (the retail grant shape - see above).
		// Returns null when the player already has the quest (re-serving the node must not re-grant) or it has no
		// definition (state is still persisted; there is just nothing to render).
		private async Task<QuestDefinition> PrepareQuestGrant(INetworkClient client, ConversationNode node)
		{
			var character = client.Session?.Character;
			if (character == null)
				return null;

			var existing = await _database.GetCharacterQuests(character.Id);
			if (existing.Any(q => q.QuestId == node.GrantQuestId))
				return null; // already accepted (active) or completed - don't re-grant

			var def = await _database.GetQuestDefinition(node.GrantQuestId);

			// Seed the quest's first objective so kills can advance it (#47): GoalRequired = the goal record's decoded
			// count (QuestGoalDefinition.RequiredCount), GoalTargetName = the seeded kill target (QuestGoalDefinition.
			// TargetName - server-side, sourced per quest since it's not on the wire). With both set,
			// CombatService.AdvanceKillQuests matches a kill of that NPC and advances/completes the quest (e.g. Casilda's
			// 13479 = kill 1 "Scavenger Tuut"). A talk/collect goal leaves TargetName null - no kill matching, count 0.
			var firstGoal = def?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
			var goalRequired = firstGoal is { RequiredCount: > 0 }
				? firstGoal.RequiredCount
				: 0u;
			var goalTargetName = firstGoal?.TargetName;

			await _database.SetCharacterQuest(character.Id, node.GrantQuestId, QuestState.Active, goalRequired: goalRequired, goalTargetName: goalTargetName);
			_logger.LogInformation("Quest {QuestId} granted to {Name} from {Npc}'s dialogue (goalRequired={Req}, target={Target}).", node.GrantQuestId, character.Name, node.NpcName, goalRequired, goalTargetName ?? "-");
			return def;
		}

		// Sends the two retail grant packets in order: the QuestLogList (populates the J-panel - all active quests,
		// just-granted tracked first) then the QuestFullUpdate (the quest's detail). Order + presence both matter: the
		// list is what makes the quest appear in the log (decoded from the grant capture).
		private async Task SendQuestGrantPackets(INetworkClient client, QuestDefinition grantDef)
		{
			var character = client.Session.Character;
			var active = await _database.GetCharacterQuests(character.Id);
			var ids = new List<uint> { grantDef.QuestId };
			ids.AddRange(active.Where(q => q.State == QuestState.Active && q.QuestId != grantDef.QuestId).Select(q => q.QuestId));

			await _broadcaster.SendQuestLogList(client, grantDef, ids);
			await _broadcaster.SendQuestFullUpdate(client, grantDef);
		}

		// Serves one dialogue SCREEN: re-addresses its captured response packets to the live player + clicked NPC and
		// sends them, then records the screen as the session's position in the dialogue tree. Each ConversationNode IS
		// one screen (the greeting, or an option's reply); the player's next KnubotAnswer picks the child.
		private async Task ServeScreen(INetworkClient client, ConversationNode node, uint clickedNpcHandle)
		{
			var liveInstance = client.Session.Account.ClientInstance;
			var oldInstanceVarint = Varint.Encode(node.CapturedInstance);
			var newInstanceVarint = Varint.Encode(liveInstance);

			// The live player's name/guild are regenerated into each screen's remoteformat XML (replacing the captured
			// player's), so a dialogue addresses the player who's actually talking. Guild is empty when guildless.
			var playerName = client.Session.Character?.Name ?? "";
			var guildName = client.Session.Character?.Guild?.Name ?? "";

			// A captured screen that offers options (AnswerList 0xF164A4A5) sometimes ALSO carries a trailing
			// CloseChatWindow (0xC4AF39EB) - the captured session closed the window right after. Replaying that close
			// auto-shuts the dialogue the player should be picking from (observed: Casilda's greeting opens then
			// instantly closes, can't accept). When the screen has a real AnswerList, drop the spurious close; a pure
			// terminal screen (CloseChatWindow only) is kept.
			var orderedPackets = node.Packets.OrderBy(p => p.Sequence).ToList();
			var hasAnswerList = orderedPackets.Any(p => SubOpcodeOf(p) == KnubotAnswerListSubOpcode);

			// Remember the GameAgent instance this conversation's packets are sent from (retail varies it per
			// conversation). SendConversationClose must reuse it - a close from a different instance (e.g. the old
			// hardcoded 0x08) doesn't match the dialogue and the client re-fires the greeting (#217).
			if (orderedPackets.Count > 0)
				client.Session.ConversationAgentInstance = ReadSenderInstance(orderedPackets[0].Data);

			// A grant node accepts a quest. Retail announces it INTERLEAVED inside the grant screen, in this order:
			// AppendText -> QuestLogList (0x504B7924, the J-panel list) -> QuestFullUpdate (0x63268258, the detail) ->
			// AnswerList, with NO QuestIIR and NO QuestJournal (capture randochar-login-get-casilda-quest frame 687-688).
			// The QuestLogList is what POPULATES the log; sending only the full-update (the detail) leaves the log empty.
			var grantDef = node.GrantQuestId != 0
				? await PrepareQuestGrant(client, node)
				: null;
			var grantSent = false;

			foreach (var p in orderedPackets)
			{
				var subOp = SubOpcodeOf(p);
				if (hasAnswerList && subOp == KnubotCloseChatWindowSubOpcode)
					continue; // skip the spurious close so the option list stays interactive

				// Deliver the quest packets immediately BEFORE the screen's AnswerList, mirroring retail's order.
				if (grantDef != null && !grantSent && subOp == KnubotAnswerListSubOpcode)
				{
					await SendQuestGrantPackets(client, grantDef);
					grantSent = true;
				}

				// Preferred path: the packet is stored STRUCTURED (#188) - build it from the columns. Falls back to
				// decoding/patching the raw Data when the columns aren't populated (re-import gap) or unrecognised.
				if (p.SubOpcode is { } structuredSub)
				{
					if (structuredSub == ConversationScreenDecoder.SetDialogueCamera)
						await SendDialogueCamera(client, p.CameraAgentInstance ?? 0x08, liveInstance, clickedNpcHandle, p.CameraParams);
					else
					{
						await SendConstructedKnubot(client, ReadSenderInstance(p.Data), liveInstance, BuildKnubotFromColumns(p, liveInstance, clickedNpcHandle, playerName, guildName));
					}

					continue;
				}

				// CONSTRUCT every Knubot record through the typed codec (#188): AppendText (line) + OpenChatWindow +
				// AnswerList + CloseChatWindow all rebuild via KnubotCodec re-addressed to the live endpoints. Only
				// NON-Knubot records (e.g. interleaved quest packets) fall through to the substitution-replay below.
				if (TryConstructKnubotRecord(p.Data, liveInstance, clickedNpcHandle, playerName, guildName, out var constructed))
				{
					await SendConstructedKnubot(client, ReadSenderInstance(p.Data), liveInstance, constructed);
					continue;
				}

				var packet = (byte[])p.Data.Clone();
				SubstituteUInt32(packet, node.CapturedNpcHandle, clickedNpcHandle);
				SubstituteUInt32(packet, node.CapturedInstance, liveInstance);
				if (oldInstanceVarint.Length == newInstanceVarint.Length)
					ReplaceAll(packet, oldInstanceVarint, newInstanceVarint);
				await new PacketStream(packet).SendAsync(client);
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
			var isTerminalClose = !hasAnswerList && orderedPackets.Any(p => ConversationSubOpcode(p.Data) == KnubotCloseChatWindowSubOpcode);
			if (isTerminalClose)
			{
				client.Session.ConversationNpcHandle = clickedNpcHandle; // ensure the guard arms for THIS npc
				EndConversation(client);
				_logger.LogInformation("Conversation ended (npc={Name}, typeId={TypeId}, path='{Path}') for NPC 0x{Npc:X}.", node.NpcName, node.TypeId, node.AnswerPath, clickedNpcHandle);
				return;
			}

			client.Session.ConversationNpcName = node.NpcName;
			client.Session.ConversationTypeId = node.TypeId;
			client.Session.ConversationAnswerPath = node.AnswerPath;
			client.Session.ConversationNpcHandle = clickedNpcHandle;

			_logger.LogInformation("Conversation screen (npc={Name}, typeId={TypeId}, path='{Path}') -> {Packets} packet(s) for NPC 0x{Npc:X}.", node.NpcName, node.TypeId, node.AnswerPath, node.Packets.Count, clickedNpcHandle);
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
			_logger.LogInformation("Quest {Quest} turned in to {Npc} by {Char} (objective {Progress}/{Required}).", held.QuestId, npcName, character.Name, held.GoalProgress, held.GoalRequired);
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
			if (client.Session.ConversationNpcHandle != 0)
				client.Session.ConversationEndedNpcHandle = client.Session.ConversationNpcHandle;

			client.Session.ConversationAnswerPath = null;
			client.Session.ConversationNpcName = null;
			client.Session.ConversationTypeId = 0;
			client.Session.ConversationNpcHandle = 0;
			client.Session.ConversationAgentInstance = 0;
		}

		internal static byte[] BuildDialogueCameraPacket(uint agentInstance, uint liveInstance, uint npcHandle, byte[] cameraParams)
		{
			var body = new FaolanStream().WriteUInt32(EntityClass.Character).WriteUInt32(npcHandle).WriteUInt32(EntityClass.Character).WriteUInt32(liveInstance).WriteArray(cameraParams ?? []).ToArray();

			var ps = new PacketStream().WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, agentInstance),
				ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, liveInstance),
				(ushort)ConversationScreenDecoder.SetDialogueCamera);
			ps.WriteArray(body);
			ps.WriteLengthHash();
			return ps.ToArray();
		}

		// Construct a Knubot record from the STRUCTURED columns (#188) - the screen text comes from ScreensJson, the
		// live player name/guild are regenerated in. Byte-identical to the Data-path construct for the same inputs
		// (gated by ConversationStructuredScreenTests), so swapping the source doesn't change the wire output.
		internal static byte[] BuildKnubotFromColumns(ConversationPacket p, uint liveInstance, uint npcHandle, string playerName, string guildName)
		{
			var env = p.Env ?? 0;
			var screens = string.IsNullOrEmpty(p.ScreensJson)
				? []
				: JsonSerializer.Deserialize<string[]>(p.ScreensJson) ?? [];

			byte[] Screen(int i)
			{
				return WithLivePlayer(Encoding.Latin1.GetBytes(screens[i]), playerName, guildName);
			}

			if (p.SubOpcode == KnubotOpenChatWindowSubOpcode)
				return KnubotCodec.BuildOpenChatWindow(liveInstance, npcHandle, p.Flag ?? 0, env);
			if (p.SubOpcode == KnubotAppendTextSubOpcode)
				return KnubotCodec.BuildAppendText(liveInstance, npcHandle, Screen(0), p.Field ?? 1, p.Flag ?? 1, env);

			// AnswerList + CloseChatWindow: the string-list grammar.
			var strings = screens.Select((_, i) => Screen(i)).ToList();
			return KnubotCodec.BuildStringList(p.SubOpcode!.Value, liveInstance, npcHandle, strings, env);
		}

		// Reads the GameAgent sender instance out of a captured packet's RPC header. Header layout (PacketStream):
		// [u32 len][u32 crc][u32 headerLen][0x0a][byteLen][sender endpoint][0x12]... where a ProtoEndpoint is
		// [0x0D <type LE32> 0x10 <instance varint>]. Falls back to 0x08 (the common value / SendConversationClose's)
		// if the header is shaped unexpectedly.
		internal static uint ReadSenderInstance(byte[] packet)
		{
			const uint Fallback = 0x08;
			if (packet == null || packet.Length < 14 || packet[12] != 0x0a)
				return Fallback;

			var senderLen = packet[13];
			var i = 14;
			var end = i + senderLen;
			if (end > packet.Length || packet[i] != 0x0d)
				return Fallback;

			i += 1 + 4; // skip 0x0D + the fixed32 type
			if (i >= end || packet[i] != 0x10)
				return Fallback;

			i++; // skip the 0x10 instance tag

			uint value = 0;
			var shift = 0;
			while (i < end)
			{
				var bb = packet[i++];
				value |= (uint)(bb & 0x7f) << shift;
				if ((bb & 0x80) == 0)
					return value;

				shift += 7;
			}

			return Fallback;
		}

		// Lifts the IIR carrier payload (the [u32 len][record]) out of a captured full conversation packet and, if it
		// is an AppendText or OpenChatWindow, rebuilds it through KnubotCodec re-addressed to the live player + clicked
		// NPC. Returns false for every other sub-opcode (those stay on the substitution-replay path).
		internal static bool TryConstructKnubotRecord(byte[] capturedPacket, uint liveInstance, uint clickedNpcHandle, string playerName, string guildName, out byte[] record)
		{
			record = null;
			if (capturedPacket == null || capturedPacket.Length < 12)
				return false;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(capturedPacket.AsSpan(8, 4));
			var start = 12 + headerLen;
			if (start + 8 > capturedPacket.Length)
				return false;

			var carrier = capturedPacket.AsSpan(start).ToArray(); // [u32 len][sub][...]
			var sub = BinaryPrimitives.ReadUInt32BigEndian(carrier.AsSpan(4, 4));

			if (sub == KnubotAppendTextSubOpcode)
			{
				var p = KnubotCodec.ParseAppendText(carrier);
				var xml = WithLivePlayer(p.Xml, playerName, guildName);
				record = KnubotCodec.BuildAppendText(liveInstance, clickedNpcHandle, xml, p.Field, p.Trailing, p.Env);
				return true;
			}

			if (sub == KnubotOpenChatWindowSubOpcode)
			{
				var p = KnubotCodec.ParseOpenChatWindow(carrier);
				record = KnubotCodec.BuildOpenChatWindow(liveInstance, clickedNpcHandle, p.Value, p.Env);
				return true;
			}

			// AnswerList + CloseChatWindow share the string-list grammar; rebuild re-addressed to the live endpoints.
			if (sub == KnubotAnswerListSubOpcode || sub == KnubotCloseChatWindowSubOpcode)
			{
				var p = KnubotCodec.ParseStringList(carrier);
				var strings = p.Strings.Select(x => WithLivePlayer(x, playerName, guildName)).ToList();
				record = KnubotCodec.BuildStringList(sub, liveInstance, clickedNpcHandle, strings, p.Env);
				return true;
			}

			return false;
		}

		// The conversation IIR sub-opcode of a stored packet: full Conan packet is [len4][crc4][headerLen4][header]
		// [body], and the body is [u32 len][u32 subOpcode]... so the sub-op is the u32 at 12 + headerLen + 4.
		private static uint ConversationSubOpcode(byte[] packet)
		{
			if (packet == null || packet.Length < 12)
				return 0;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var off = 12 + headerLen + 4;
			return off + 4 <= packet.Length
				? BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(off, 4))
				: 0;
		}

		// Byte-array find/replace from offset 8 (skips the length+crc prefix). Copied from RetailLoginReplay's
		// private helper - a small self-contained util; duplicating it keeps this service free of the listener.
		private static void ReplaceAll(byte[] buffer, byte[] find, byte[] repl)
		{
			for (var i = 8; i + find.Length <= buffer.Length; i++)
			{
				var match = true;
				for (var j = 0; j < find.Length; j++)
				{
					if (buffer[i + j] != find[j])
					{
						match = false;
						break;
					}
				}

				if (match)
				{
					repl.CopyTo(buffer, i);
					i += find.Length - 1;
				}
			}
		}

		// Frames a constructed Knubot record exactly as retail does (verified against the captures): the conversation
		// runs sender `GameAgent:<conversationAgentInstance>` -> receiver `GameInterface:<playerHandle>` on opcode
		// 0x2000. Retail VARIES the GameAgent instance per conversation (0x07..0x5a observed; 0x08 the most common),
		// and the client addresses its KnubotAnswer back to whatever instance sent the screen - so we PRESERVE the
		// captured packet's sender instance rather than hardcoding one, matching the replay path byte-for-byte.
		private static Task SendConstructedKnubot(INetworkClient client, uint senderInstance, uint liveInstance, byte[] record)
		{
			return new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, senderInstance), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, liveInstance), GameServerCarrierOpcodes.IirCarrier)
				.WriteArray(record)
				.SendAsync(client);
		}

		// Constructs the dialogue camera (DispatchSetDialogueCamera 0x2015) from the structured columns (#188):
		// GameCharAgent:<agentInstance> -> GameCharInterface:<player>, body [0xC350][npc][0xC350][player][params].
		// Byte-identical to the captured packet re-addressed (gated by the construct test).
		private static Task SendDialogueCamera(INetworkClient client, uint agentInstance, uint liveInstance, uint npcHandle, byte[] cameraParams)
		{
			return client.SendAsync(BuildDialogueCameraPacket(agentInstance, liveInstance, npcHandle, cameraParams));
		}

		// The packet's Knubot sub-opcode: the stored column when structured, else read from the raw Data.
		private static uint SubOpcodeOf(ConversationPacket p)
		{
			return p.SubOpcode ?? ConversationSubOpcode(p.Data);
		}

		// Replaces every big-endian uint32 occurrence of oldVal with newVal after the 8-byte length+crc prefix.
		// The handle/instance are 4 bytes both ways, so every nested length prefix stays valid.
		private static void SubstituteUInt32(byte[] packet, uint oldVal, uint newVal)
		{
			if (oldVal == newVal)
				return;

			Span<byte> needle = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(needle, oldVal);

			for (var i = 8; i + 4 <= packet.Length; i++)
			{
				if (packet[i] == needle[0] && packet[i + 1] == needle[1] && packet[i + 2] == needle[2] && packet[i + 3] == needle[3])
				{
					BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(i, 4), newVal);
					i += 3;
				}
			}
		}

		// Regenerate the live player's name/guild into a captured screen's remoteformat XML via the structured
		// builder (so the captured player's name no longer shows to everyone). SAFETY: only rebuilds when the parser
		// reproduces the captured XML byte-for-byte first; otherwise the original bytes are returned untouched, so an
		// unrecognised screen variant can never be corrupted.
		private static byte[] WithLivePlayer(byte[] screenXml, string playerName, string guildName)
		{
			if (screenXml == null || screenXml.Length == 0 || string.IsNullOrEmpty(playerName))
				return screenXml;

			try
			{
				var xml = Encoding.Latin1.GetString(screenXml);
				var screen = RemoteFormatXml.Parse(xml);
				if (RemoteFormatXml.Build(screen) != xml)
					return screenXml; // not byte-exact roundtrippable -> leave it alone

				var rebuilt = RemoteFormatXml.Build(RemoteFormatXml.WithLivePlayer(screen, playerName, guildName ?? ""));
				return Encoding.Latin1.GetBytes(rebuilt);
			}
			catch
			{
				return screenXml;
			}
		}
	}
}
