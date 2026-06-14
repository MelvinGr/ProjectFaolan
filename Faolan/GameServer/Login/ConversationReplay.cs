using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The instance baked into the captured KnubotCloseChatWindow body (hoxxling's clientInstance), re-addressed
		// to the live player on send.
		private const uint CloseChatWindowCapturedInstance = 0x01316E71;

		// The Knubot packets that END a conversation stage: an answer list (the next options) or a close-window.
		private const uint KnubotAnswerListSubOpcode = 0xF164A4A5;
		private const uint KnubotCloseChatWindowSubOpcode = 0xC4AF39EB;

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

		// Closes the NPC chat window gracefully (KnubotCloseChatWindow with an empty answer list), re-addressed to the
		// live player. Sent when a TalkToNpc has NO matching conversation node, so the client never hangs mid-dialogue
		// waiting for a reply that won't come - the "advance a step with no captured branch -> chat locks, can't ESC"
		// bug. (Conversation replay is coverage-bound: (Mode, Field3) is quest-state-dependent and the captures cover
		// only some states per NPC, so an un-captured branch is expected; closing the window is the right answer.)
		internal Task SendConversationClose(INetworkClient client)
		{
			client.Session.ConversationAnswerPath = null; // end any active conversation
			client.Session.ConversationNpcName = null;
			var id = client.Session.Account.ClientInstance;
			var body = (byte[])KnubotCloseChatWindowBody.Clone();
			SubstituteUInt32(body, CloseChatWindowCapturedInstance, id);

			return new PacketStream()
				.WriteHeader([0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x08], AppendVarint([0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10], id), 0x2000u)
				.WriteArray(body)
				.SendAsync(client);
		}

		// Serves one dialogue SCREEN: re-addresses its captured response packets to the live player + clicked NPC and
		// sends them, then records the screen as the session's position in the dialogue tree. Each ConversationNode IS
		// one screen (the greeting, or an option's reply); the player's next KnubotAnswer picks the child.
		private async Task ServeScreen(INetworkClient client, ConversationNode node, uint clickedNpcHandle)
		{
			var liveInstance = client.Session.Account.ClientInstance;
			var oldInstanceVarint = EncodeVarint(node.CapturedInstance);
			var newInstanceVarint = EncodeVarint(liveInstance);

			// A captured screen that offers options (AnswerList 0xF164A4A5) sometimes ALSO carries a trailing
			// CloseChatWindow (0xC4AF39EB) - the captured session closed the window right after. Replaying that close
			// auto-shuts the dialogue the player should be picking from (observed: Casilda's greeting opens then
			// instantly closes, can't accept). When the screen has a real AnswerList, drop the spurious close; a pure
			// terminal screen (CloseChatWindow only) is kept.
			var orderedPackets = node.Packets.OrderBy(p => p.Sequence).ToList();
			var hasAnswerList = orderedPackets.Any(p => ConversationSubOpcode(p.Data) == KnubotAnswerListSubOpcode);

			// A grant node accepts a quest. Retail announces it INTERLEAVED inside the grant screen, in this order:
			// AppendText -> QuestLogList (0x504B7924, the J-panel list) -> QuestFullUpdate (0x63268258, the detail) ->
			// AnswerList, with NO QuestIIR and NO QuestJournal (capture randochar-login-get-casilda-quest frame 687-688).
			// The QuestLogList is what POPULATES the log; sending only the full-update (the detail) leaves the log empty.
			var grantDef = node.GrantQuestId != 0 ? await PrepareQuestGrant(client, node) : null;
			var grantSent = false;

			foreach (var p in orderedPackets)
			{
				var subOp = ConversationSubOpcode(p.Data);
				if (hasAnswerList && subOp == KnubotCloseChatWindowSubOpcode)
					continue; // skip the spurious close so the option list stays interactive

				// Deliver the quest packets immediately BEFORE the screen's AnswerList, mirroring retail's order.
				if (grantDef != null && !grantSent && subOp == KnubotAnswerListSubOpcode)
				{
					await SendQuestGrantPackets(client, grantDef);
					grantSent = true;
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

			client.Session.ConversationNpcName = node.NpcName;
			client.Session.ConversationTypeId = node.TypeId;
			client.Session.ConversationAnswerPath = node.AnswerPath;
			client.Session.ConversationNpcHandle = clickedNpcHandle;

			Logger.LogInformation("Conversation screen (npc={Name}, typeId={TypeId}, path='{Path}') -> {Packets} packet(s) for NPC 0x{Npc:X}.", node.NpcName, node.TypeId, node.AnswerPath, node.Packets.Count, clickedNpcHandle);
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

			var existing = await Database.GetCharacterQuests(character.Id);
			if (existing.Any(q => q.QuestId == node.GrantQuestId))
				return null; // already accepted (active) or completed - don't re-grant

			await Database.SetCharacterQuest(character.Id, node.GrantQuestId, QuestState.Active);
			Logger.LogInformation("Quest {QuestId} granted to {Name} from {Npc}'s dialogue.", node.GrantQuestId, character.Name, node.NpcName);
			return await Database.GetQuestDefinition(node.GrantQuestId);
		}

		// Sends the two retail grant packets in order: the QuestLogList (populates the J-panel - all active quests,
		// just-granted tracked first) then the QuestFullUpdate (the quest's detail). Order + presence both matter: the
		// list is what makes the quest appear in the log (decoded from the grant capture).
		private async Task SendQuestGrantPackets(INetworkClient client, QuestDefinition grantDef)
		{
			var character = client.Session.Character;
			var active = await Database.GetCharacterQuests(character.Id);
			var ids = new List<uint> { grantDef.QuestId };
			ids.AddRange(active
				.Where(q => q.State == QuestState.Active && q.QuestId != grantDef.QuestId)
				.Select(q => q.QuestId));

			await _broadcaster.SendQuestLogList(client, grantDef, ids);
			await _broadcaster.SendQuestFullUpdate(client, grantDef);
		}

		// The player picked option `answerIndex` in the active conversation: navigate to the child screen at the
		// extended answer-path and serve it. An un-walked branch (no captured screen) closes the window gracefully so
		// the client never hangs. No-op when no conversation is active.
		internal async Task AdvanceConversation(INetworkClient client, uint answerIndex)
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
				Logger.LogInformation("Conversation: no captured screen for npc={Name} path='{Path}' (un-walked branch); closing.", client.Session.ConversationNpcName, childPath);
				await SendConversationClose(client);
				return;
			}

			await ServeScreen(client, screen, client.Session.ConversationNpcHandle);
		}

		// Starts an NPC conversation from its greeting: routes by the clicked NPC's stable NAME (handles are
		// session-volatile) to the dialogue's "" screen and serves it. Returns true if a greeting was found+served.
		// Runs for world-burst (beach) AND create-world sessions; the create-world scripted intro stays the fallback
		// for NPCs with no captured dialogue. The nodes live in the DB (ConversationNode/ConversationPacket), imported
		// from captures by Faolan.Importer. (Option picks after the greeting are KnubotAnswers -> AdvanceConversation.)
		private async Task<bool> TryReplayConversation(INetworkClient client, uint typeId, uint field3, uint clickedNpcHandle)
		{
			if (!client.Session.UsedWorldBurstReplay && !client.Session.IsCreateWorldSession)
				return false;

			// The client sends TalkToNpc(field3=0) [greeting] then a TalkToNpc(field3!=0) the same instant (retail
			// answers that second one with nothing). Only the greeting opens the dialogue; a non-greeting click while a
			// conversation is already active is absorbed (return true, no reply) so it doesn't reset or close it.
			if (field3 != 0)
				return client.Session.ConversationAnswerPath != null;

			if (!client.Session.SpawnedNpcNamesByHandle.TryGetValue(clickedNpcHandle, out var npcName))
				return false; // unknown NPC (not spawned this session) -> create-world scripted intro fallback

			var greeting = await PickConversationScreen(client, npcName, typeId, "");
			if (greeting == null)
				return false;

			await ServeScreen(client, greeting, clickedNpcHandle);
			return true;
		}

		// Picks the conversation-screen variant whose Lua CONDITION passes for the live player (quest/tag-gated
		// dialogue, issue #37). Most screens have a single unconditional variant - fast path. When conditioned
		// variants exist, evaluate each (conditioned-first) against the character's tags + completed quests via the
		// LuaScriptVm, and serve the first that passes; the unconditional "" variant is the fallback. A condition
		// script error is treated as "doesn't match" (permissive=false) so a bad gate can't hijack every player.
		private async Task<ConversationNode> PickConversationScreen(INetworkClient client, string npcName, uint typeId, string answerPath)
		{
			var variants = await Database.GetConversationScreens(npcName, typeId, answerPath);
			if (variants.Count == 0)
				return null;
			if (variants.Count == 1 || variants.All(v => string.IsNullOrEmpty(v.ConditionScript)))
				return variants[0]; // GetConversationScreens orders the unconditional fallback last

			var character = client.Session.Character;
			var tags = character != null ? await Database.GetCharacterTags(character.Id) : System.Array.Empty<uint>();
			var quests = character != null ? await Database.GetCharacterQuests(character.Id) : System.Array.Empty<CharacterQuest>();
			var completed = quests.Where(q => q.State == QuestState.Completed).Select(q => q.QuestId);
			var active = quests.Where(q => q.State == QuestState.Active).Select(q => q.QuestId);
			var player = new Scripting.GameLuaPlayer(tags, completed, active);

			ConversationNode fallback = null;
			foreach (var v in variants)
			{
				if (string.IsNullOrEmpty(v.ConditionScript))
				{
					fallback = v;
					continue;
				}

				if (_luaVm.RunCondition(v.ConditionScript, player, permissive: false))
				{
					Logger.LogInformation("Conversation: npc={Name} path='{Path}' matched conditioned variant.", npcName, answerPath);
					return v;
				}
			}

			return fallback;
		}

		// The conversation IIR sub-opcode of a stored packet: full Conan packet is [len4][crc4][headerLen4][header]
		// [body], and the body is [u32 len][u32 subOpcode]... so the sub-op is the u32 at 12 + headerLen + 4.
		private static uint ConversationSubOpcode(byte[] packet)
		{
			if (packet == null || packet.Length < 12)
				return 0;
			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var off = 12 + headerLen + 4;
			return off + 4 <= packet.Length ? BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(off, 4)) : 0;
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
	}
}
