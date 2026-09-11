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
	/// <summary>
	///     The NPC dialogue runtime: routes a TalkToNpc or KnubotAnswer to the captured conversation node for the
	///     requested (NPC, typeId, answer-path), re-addresses its packets to the live player and clicked NPC, advances
	///     the tree, and grants quests reached in a branch.
	/// </summary>
	/// <remarks>
	///     Quest-journal pushes go through the WorldBroadcaster; branch conditions match the player's quest state.
	/// </remarks>
	public sealed class ConversationService
	{
		// The TypeId every server-initiated conversation node carries: a server-opened window has no BCC action to take
		// an action id from. It doubles as the guard that keeps these out of the talk path.
		public const uint ServerInitiatedTypeId = 0;

		// The GameAgent sender instance to use when a packet has none stored (retail's most common value); the real
		// per-conversation instance comes from ConversationPacket.SenderInstance.
		private const uint DefaultSenderInstance = 0x08;
		// The Knubot packets that end a conversation stage: an answer list (the next options) or a close-window.

		// The Knubot packets constructed from the typed codec (KnubotCodec) rather than replayed and substituted: all
		// four record types - the spoken line (AppendText), the window-open, the answer list and the close. Their
		// content - XML, options, value - is lifted from the captured record and rebuilt with the live player and NPC
		// endpoints, byte-identical to a substitution (ConversationConstruct tests) but routed through the typed codec.

		// A KnubotCloseChatWindow (0xC4AF39EB) body, captured verbatim. It closes the NPC chat window with an empty
		// answer list - the remoteformat is id="0" with no options - which is retail's "this NPC has nothing more to
		// say" reply. The remoteformat, including its key, is byte-identical across every capture, so it is a reusable
		// constant; only the player instance at offset 12 is re-addressed. See SendConversationClose.
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

		// A turn-in is a quest completion, so it pays the quest's stated reward the same way the objective path does.
		// Reached directly rather than through the progression service, which would only be forwarding.
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

		// How close the player must come to an NPC for its approach-triggered conversation to open. Measured at 12.9,
		// 13.8 and 10.9 units across three independent captures of Saddur's beach confrontation.
		private const float ApproachRadius = 15f;

		// The NPC templates that have an approach-triggered conversation, cached on first use. A small static set, so
		// the per-move check costs one hash lookup for the overwhelming majority of players, who are near nobody
		// special.
		private HashSet<uint>? _approachNpcs;

		/// <summary>
		///     Opens a conversation the player has just walked up to an NPC for - the approach trigger. Saddur's beach
		///     confrontation is the worked example: "{hidegoodbye}Who stands before Saddur?" fires on approach, with no
		///     click and no object.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The radius is measured, not chosen: across three independent captures the window opens at 12.9, 13.8 and
		///         10.9 units from Saddur's spawn, so 15 covers all three with a little slack and stays far short of the
		///         next NPC (Casilda at 153 and beyond). It is deliberately small - a generous radius would open the
		///         conversation across a room.
		///     </para>
		///     <para>
		///         This is proximity to the NPC, not a bounded-area volume. Only volume entry lives in RDB 1000053 and the
		///         two are different mechanisms, so nothing here needs region geometry.
		///     </para>
		///     <para>
		///         It fires once per NPC per session (<c>ApproachedConversationNpcs</c>): Saddur's nodes carry no quest
		///         gate, so without a latch the window would re-open on every step taken near him. The templates that have
		///         one at all are cached here - matching on a display name would open Saddur's confrontation on any NPC
		///         sharing it - so a player walking past ordinary NPCs pays one hash lookup and no database hit.
		///     </para>
		/// </remarks>
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

		/// <summary>
		///     The player picked option <paramref name="answerIndex" /> in the active conversation: resolves it to the
		///     option's own LDB id on the screen last sent, takes the edge that option opens, and serves where it lands. A
		///     branch the captures never walked closes the window gracefully so the client never hangs. A no-op when no
		///     conversation is active.
		/// </summary>
		/// <remarks>
		///     The index is resolved against the screen actually on the player's display and is never persisted. The same
		///     screen offers different option lists in different quest states - Casilda's greeting has six - so an index
		///     means six different things while the option's string id means one.
		/// </remarks>
		public async Task AdvanceConversation(INetworkClient client, uint answerIndex)
		{
			// An active conversation means both slots are set; guarding them here states that invariant once rather than
			// defaulting each further down, where a 0 handle would silently address the wrong NPC.
			if (client.Session.ConversationNodeId is not { } fromNodeId || client.Session.ConversationNpcHandle is not { } npcHandle)
				return;

			var npcName = client.Session.ConversationNpcName;
			if (client.Session.ConversationOptions.Count <= answerIndex)
			{
				// The client offered an option the screen just sent does not have. That is not a coverage gap but a
				// disagreement about what is on screen, so it is reported rather than closed as an un-walked branch.
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

			// Local travel rides this walk, and both of its steps are dialogue picks, which is why the hook is here
			// rather than in ServeScreen - a root can never be either one.
			//
			// The fare comes off before the screen that presents the destination list: retail's DispatchUpdateCash sits
			// immediately in front of "Your coin is good enough. Where to?", so it is charged here, on the way in. A
			// refusal - criminal, or too poor - must not then hand the player the list, so the window is closed instead.
			// The corpus has no refused traveller, so closing is the conservative reading rather than a captured one.
			if (!await _travel.TryChargeFare(client, edge.ToNodeId))
			{
				await SendConversationClose(client);
				return;
			}

			await ServeScreen(client, edge.To, npcHandle, edge.GrantQuestId);

			// The destination pick: retail closes the chat window and then teleports - the close is the served screen's
			// only packet - so the trip runs after the screen, in the same text-precedes-state order the quest grants
			// follow.
			await _travel.TryTravel(client, fromNodeId, optionId);
		}

		/// <summary>
		///     Opens a conversation the server initiates - one with no player TalkToNpc in front of it, served because an
		///     event fired. Casilda's city-door farewell is the worked example: the player uses the door, the escort
		///     completes, and retail opens her window unprompted.
		/// </summary>
		/// <returns>
		///     False when the NPC has no such root, or - for the object trigger - when it is not spawned for this player,
		///     which is the right outcome for an escortee who is not there.
		/// </returns>
		public async Task<bool> TryOpenApproachConversation(INetworkClient client, uint npcTemplateId, uint npcHandle)
		{
			return await OpenRoot(client, npcTemplateId, ConversationTrigger.Approach, null, npcHandle) != null;
		}

		public async Task<bool> TryOpenTriggeredConversation(INetworkClient client, uint npcTemplateId, uint worldObjectId)
		{
			// The escortee is found by template, so a same-named bystander cannot answer for them. The session already
			// carries this map beside the name one, written on the adjacent line.
			var handle = client.Session.SpawnedNpcTemplatesByHandle
				.FirstOrDefault(kv => kv.Value == npcTemplateId)
				.Key;
			if (handle == 0)
				return false; // the escortee is not spawned for this player - the conversation simply does not open

			return await OpenRoot(client, npcTemplateId, ConversationTrigger.UseWorldObject, worldObjectId, handle) != null;
		}

		/// <summary>
		///     Opens one of an NPC's dialogue roots and serves its screen.
		/// </summary>
		/// <remarks>
		///     The three ways retail opens a window - the player clicking, using an object, or walking up - differ only in
		///     the trigger they look up with, so they are one method rather than three near-copies. Several roots can share
		///     a trigger and differ only by quest gate (Casilda has six talk roots); the gate rule is the one the branches
		///     use.
		/// </remarks>
		/// <returns>
		///     The root served, or null when the NPC has none for this trigger. The root rather than a bool because the
		///     caller needs the NPC's display name afterwards, for the talk-goal match, and the root is where that label
		///     lives - reading it back off the session would be wrong, since a terminal screen ends the conversation and
		///     clears it.
		/// </returns>
		private async Task<ConversationEntry?> OpenRoot(INetworkClient client, uint npcTemplateId, ConversationTrigger trigger, uint? worldObjectId, uint npcHandle)
		{
			var roots = await _database.GetConversationEntries(npcTemplateId, trigger, worldObjectId);
			if (roots.Length == 0)
				return null;

			// Remember the door or gate that opened this window, for a script event fired when it closes: Casilda's
			// `RunToTortage` sends her to the object the player just used, and the handler that knew which one has
			// already returned by then.
			client.Session.ConversationTriggerObjectId = worldObjectId;

			var character = client.Session.Character;
			var quests = character == null
				? []
				: await _database.GetCharacterQuests(character.Id);

			// Retail's greeting is not one line: an NPC opens with a full welcome the first time a player opens its
			// window and a terse one every time after - Elethia's "Hello.{social greet}" becomes "What now?". Only four
			// NPCs in the corpus were captured saying both, so the column is null on nearly every root and narrows
			// nothing; where it is set, the roots for the other meeting must not be candidates at all.
			var repeat = !client.Session.GreetedConversationNpcs.Add(npcTemplateId);
			var eligible = Array.FindAll(roots, r => r.RepeatGreeting == null || r.RepeatGreeting == repeat);
			if (eligible.Length == 0)
				eligible = roots; // the captures only ever caught this NPC on the other meeting - serve what we have

			var opening = PickByLatestQuest(eligible, quests);
			if (opening == null)
			{
				// No gate matched. For an NPC whose greetings are quest-gated, that means the player is in a state no
				// capture recorded, and its unconditional root is not a default - it is the greeting harvested for
				// somebody else's progress. Serving it hands the player plausible but wrong dialogue, so the gap is
				// reported instead: an uncaptured state is a gap to surface, not a hole to fill with the nearest thing.
				//
				// This applies only when the player is past the gated states, never before them. The ungated root is
				// the harvested greeting for a character with no chain progress (see
				// docs/subsystems/knubot-conversation-packets.md - "the least-informed class is the unconditional
				// fallback"), so a fresh character matches no gate for the ordinary reason and must still be served it,
				// or it can never start the chain. Only once a gating quest has been completed does falling through
				// mean a state after everything captured.
				//
				// Scoped to NPCs that have a gate: the 107 NPCs whose only root is unconditional are unaffected, and
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

		/// <summary>
		///     Starts an NPC conversation from its greeting: routes by the clicked NPC's stable template id - handles are
		///     session-volatile and display names are ambiguous - to the NPC's Talk root, and serves the screen it opens on.
		/// </summary>
		/// <remarks>
		///     Runs for world-burst and create-world sessions alike; the create-world scripted intro stays the fallback for
		///     NPCs with no captured dialogue. The graph lives in the database (ConversationEntry, ConversationNode,
		///     ConversationEdge), imported from captures by Faolan.Importer. Option picks after the greeting are
		///     KnubotAnswers, handled by <see cref="AdvanceConversation" />.
		/// </remarks>
		/// <returns>True if a greeting was found and served.</returns>
		public async Task<bool> TryReplayConversation(INetworkClient client, uint field3, uint clickedNpcHandle)
		{
			if (client.Session is { WorldEntered: false, EntryMode: not WorldEntryMode.CreateWorld })
				return false;

			// The client sends TalkToNpc(field3=0), the greeting, then a TalkToNpc(field3!=0) in the same instant, which
			// retail answers with nothing. Only the greeting opens the dialogue; a non-greeting click while a
			// conversation is already active is absorbed - true, with no reply - so it does not reset or close it.
			if (field3 != 0)
				return client.Session.ConversationNodeId != null;

			// Absorb the reflexive greeting the client re-fires right after a close: it keeps the NPC targeted and
			// re-talks with no new SelectDeselect. Suppressed until the player re-selects the NPC, so a deliberate
			// re-talk still opens. This is what makes an unknown path exit and stay exited rather than instantly
			// re-triggering.
			if (clickedNpcHandle != 0 && client.Session.ConversationEndedNpcHandle == clickedNpcHandle)
			{
				_logger.LogInformation("Conversation: suppressed the reflexive post-close greeting for NPC 0x{NpcTemplate:X} (#217; re-select to talk again).", clickedNpcHandle);
				return true;
			}

			if (!client.Session.SpawnedNpcTemplatesByHandle.TryGetValue(clickedNpcHandle, out var npcTemplateId))
				return false; // unknown NPC (not spawned this session) -> create-world scripted intro fallback

			// Pick the greeting in the player's current, pre-completion quest state so a talk stage shows its own
			// dialogue: Casilda's "I'll protect you now" one-off is gated on HasQuest(13480), so it must be picked while
			// 13480 is still active.
			if (await OpenRoot(client, npcTemplateId, ConversationTrigger.Talk, null, clickedNpcHandle) is not { } opened)
				return false;

			// Serve first, complete after. Both quest-completion paths run here, once the text is on the wire, because
			// that is the order retail sends them in. Two independent captures agree, on different characters and
			// different quests:
			//
			//   compeltefirstquest  t=57.3 : KnubotOpenChatWindow -> KnubotAppendText "...{resolvegoal BJGY}{delay 1}"
			//                                -> QuestGoal Satisfied -> QuestIIR 2 -> QuestJournal -> QuestIIR 4
			//                                -> QuestFullUpdate (the next stage)
			//   ...questcomplete    t=373.3: byte-for-byte the same order
			//
			// The grant side is the same shape (...questcomplete t=334.5: AppendText -> MainQuestUpdate ->
			// QuestFullUpdate), so text-precedes-state is the rule for the whole dialogue rather than a completion quirk.
			//
			// The ordering is also what makes the greeting pick correct. Completing before PickConversationScreen flips
			// the quest to Completed, so the pick matches the HasCompletedQuest variant and the player is handed the
			// post-quest "already done" line instead of the turn-in line. Retail has no separate reward node to reach
			// for - the turn-in text is the in-progress variant, carrying an inline {resolvegoal <4CC>} directive - so
			// serving pre-completion serves exactly the right screen.
			await TryTurnInQuests(client, npcTemplateId);

			// Complete the TalkToNpc goal - freeing Casilda takes 13480 to the escort 12407 - so the freeing dialogue
			// shows once, then the stage completes and the next talk sees the completed state and gets the post-quest
			// greeting.
			//
			// Matched by template first, name second. A TalkToNpc goal's own TrailingRefId is not the NPC: on the 12
			// talk goals that carry one it is the item the player hands over (quest 12371's Turach goal carries 193400,
			// the Shipwrecked stone brick). `QuestGoalDefinition.TargetTemplateId` is the id to match on, resolved at
			// import from the name the client's own RDB data states. Both are passed because only 278 goals have
			// resolved so far; the rest name NPCs on maps that have not been imported and still match on the string.
			await _questProgression.AdvanceTalkGoals(client, opened.NpcName, npcTemplateId);
			return true;
		}

		/// <summary>
		///     The branch out of <paramref name="fromNodeId" /> that clicking option <paramref name="optionId" /> takes, or
		///     null when the captures never walked it.
		/// </summary>
		/// <remarks>
		///     The gate lives on the branch rather than on the screen: two harvested walks reached Turach's 2832 by the
		///     same option with identical screen content, one granting quest 12371 and one granting nothing, and that
		///     difference belongs to how the screen was reached. Most options have a single unconditional edge, which is
		///     the fast path.
		/// </remarks>
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

		/// <summary>
		///     The gated row to use when several gates pass at once, or null when none does.
		/// </summary>
		/// <remarks>
		///     Gates overlap by construction: the importer derives them from the quest state each screen was captured in,
		///     and a player deep in a chain holds every earlier quest of it too - Casilda's escort greeting is gated on
		///     12407 Active, and the two greetings after it sit on top of a 12407 that is still active. The beat the player
		///     is actually on is the quest they accepted most recently, and CharacterQuest is insert-ordered, so the
		///     highest row id among the passing gates is that beat. Without this rule the pick falls to whatever order the
		///     query returns, which is a database detail rather than a rule.
		/// </remarks>
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

		/// <summary>
		///     Serves one dialogue screen: re-addresses its captured response packets to the live player and clicked NPC
		///     and sends them, then records the screen as the session's position in the graph, together with the option ids
		///     it put on the player's display - which is what the next KnubotAnswer's index is resolved against.
		/// </summary>
		/// <param name="grantQuestId">
		///     The quest the branch that got here grants, not a property of the screen: the same screen reached two ways
		///     can grant on one route and not the other, as Turach's 2832 does.
		/// </param>
		private async Task ServeScreen(INetworkClient client, ConversationNode node, uint clickedNpcHandle, uint? grantQuestId = null)
		{
			var liveInstance = client.Session.Character.Id;

			// The session slots are regenerated into each screen's remoteformat XML from live values, since the stored
			// screen has them blank: the player who is actually talking, their guild (empty when guildless), and the NPC
			// guild label, which carries this realm's name rather than the capture's retail server. Character.Guild is a
			// navigation nobody loads, lazy loading being off, so the guild is fetched by id.
			var character = client.Session.Character;
			var playerName = character?.Name ?? "";
			// The grammatical gender the localized text declines the player's name against. Every captured screen
			// carries flags="m" because every capture's player was male, so the stored flag must not be carried through
			// - it would decline a female character as male in every line of dialogue naming her.
			var playerSex = character?.Sex ?? Sex.None;
			var guildName = character?.GuildId is { } guildId
				? (await _database.GetGuild(guildId))?.Name ?? ""
				: "";
			var npcGuildName = RemoteFormatSession.NotInGuildLabel(character != null
				? (await _database.GetRealm(character.RealmId))?.Name ?? ""
				: "");

			// A captured screen that offers options (AnswerList 0xF164A4A5) sometimes also carries a trailing
			// CloseChatWindow (0xC4AF39EB), because the captured session closed the window right after. Replaying that
			// close auto-shuts the dialogue the player should be picking from - Casilda's greeting opens and instantly
			// closes, with no chance to accept. When the screen has a real AnswerList the spurious close is dropped; a
			// pure terminal screen, CloseChatWindow only, is kept.
			var orderedPackets = node.Packets.OrderBy(p => p.Sequence).ToList();
			var hasAnswerList = orderedPackets.Any(p => p.SubOpcode == ConversationSubOpcode.KnubotAnswerList);

			// Remember the GameAgent instance this conversation's packets are sent from; retail varies it per
			// conversation. SendConversationClose must reuse it - a close from a different instance does not match the
			// dialogue, and the client re-fires the greeting.
			if (orderedPackets.Count > 0)
				client.Session.ConversationAgentInstance = orderedPackets[0].SenderInstance ?? DefaultSenderInstance;

			// A grant node accepts a quest. Retail announces it interleaved inside the grant screen, in this order:
			// AppendText, QuestLogList (0x504B7924, the J-panel list), QuestFullUpdate (0x63268258, the detail), then
			// AnswerList, with no QuestIIR and no QuestJournal (capture randochar-login-get-casilda-quest frames
			// 687-688). The QuestLogList is what populates the log; sending only the detail leaves it empty.
			var grantDef = grantQuestId is > 0 and { } granted
				? await _questProgression.Grant(client, granted, $"{node.NpcName}'s dialogue")
				: null;
			var grantSent = false;

			foreach (var p in orderedPackets)
			{
				// Every conversation packet is stored structured: the importer requires a recognised SubOpcode, so the
				// columns are the sole construction path and there is no raw-data fallback. A null SubOpcode is an import
				// bug, and all 596 shipped packets have one. It must throw rather than log and skip: skipping hands the
				// player a conversation with a line missing from the middle, which reads as a content bug rather than a
				// broken import.
				var subOp = p.SubOpcode
					?? throw new InvalidOperationException($"Conversation packet #{p.Sequence} (node {node.Id}) has no SubOpcode - the importer did not columnize it, so this dialogue cannot be built.");

				if (hasAnswerList && subOp == ConversationSubOpcode.KnubotCloseChatWindow)
					continue; // skip the spurious close so the option list stays interactive

				// Deliver the quest packets immediately before the screen's AnswerList, mirroring retail's order.
				if (grantDef != null && !grantSent && subOp == ConversationSubOpcode.KnubotAnswerList)
				{
					await _questProgression.SendGrantPackets(client, grantDef);
					grantSent = true;
				}

				// The sender instance, which retail varies per conversation, is a stored column; the record body is built
				// from the typed screen columns re-addressed to the live player and clicked NPC.
				var senderInstance = p.SenderInstance ?? DefaultSenderInstance;
				if (subOp == ConversationSubOpcode.SetDialogueCamera)
					await SendDialogueCamera(client, senderInstance, liveInstance, clickedNpcHandle, CameraArgsOf(p));
				else
					await SendConstructedKnubot(client, senderInstance, liveInstance, BuildKnubotFromColumns(p, liveInstance, clickedNpcHandle, playerName, guildName, npcGuildName, playerSex));
			}

			if (grantDef != null && !grantSent) // no AnswerList on this screen - send after the (terminal) screen
				await _questProgression.SendGrantPackets(client, grantDef);

			// Accepting the quest changed the player's quest state, so the giver's '!' must update in place - retail
			// clears it to 0x00 the instant the quest is accepted (capture frame 698) - rather than persisting until
			// the NPC re-streams. The clicked NPC is the giver and is in view.
			if (grantDef != null)
			{
				await _questBroadcaster.RefreshQuestGiverMarker(client, clickedNpcHandle);
				// Accepting a quest may reveal quest-gated NPCs - Scavenger Tuut, once the player holds Casilda's kill
				// quest - so re-run interest and they stream in immediately rather than only after the next move.
				await _broadcaster.RefreshQuestVisibility(client);
			}

			// A pure terminal screen - a CloseChatWindow with no AnswerList - ends the dialogue: the window is now shut
			// on the client. Recording it as the active position would leave the session inside a finished
			// conversation, so the greeting the client fires right after would re-open it. Ending here arms the
			// post-close guard in EndConversation, and that follow-up greeting is absorbed.
			var isTerminalClose = !hasAnswerList && orderedPackets.Any(p => p.SubOpcode == ConversationSubOpcode.KnubotCloseChatWindow);
			if (isTerminalClose)
			{
				client.Session.ConversationNpcHandle = clickedNpcHandle; // ensure the guard arms for this NPC
				EndConversation(client);
				_logger.LogInformation("Conversation ended (npc={Name}, screen 0x{Node:X8}) for NPC 0x{NpcTemplate:X}.", node.NpcName, node.Id, clickedNpcHandle);
				await FireScriptEvents(client, node, clickedNpcHandle);
				return;
			}

			client.Session.ConversationNpcName = node.NpcName;
			client.Session.ConversationNodeId = node.Id;
			// The option buttons as the client now draws them, so the next KnubotAnswer's index resolves to the
			// option's own LDB id. Taken from the screen just sent rather than looked up again: the index is only
			// meaningful against what is actually on the display.
			client.Session.ConversationOptions = [.. orderedPackets
				.Where(p => p.SubOpcode == ConversationSubOpcode.KnubotAnswerList)
				.OrderBy(p => p.Sequence)
				.LastOrDefault()?.Screens.OrderBy(s => s.Sequence).Select(s => s.ScreenId) ?? []];
			client.Session.ConversationNpcHandle = clickedNpcHandle;

			_logger.LogInformation("Conversation screen (npc={Name}, screen 0x{Node:X8}) -> {Packets} packet(s), {Options} option(s) for NPC 0x{NpcTemplate:X}.", node.NpcName, node.Id,
				node.Packets.Count, client.Session.ConversationOptions.Count, clickedNpcHandle);
		}

		/// <summary>
		///     The scripted events this creature's dialogue fires, run once the window has closed - which is where retail
		///     runs them. In the capture, Saddur's `Fight` action rides the second-to-last screen and the fight itself
		///     starts about 3 s after `KnubotCloseChatWindow`: hate list, threat, CombatMode 4, then the first swing.
		/// </summary>
		/// <remarks>
		///     Dispatched on the row's action, never on the event string. The string is retail's and names one quest's
		///     fiction - `RunToTortage` fits nobody but Casilda - so matching it here would mean a new constant and a new
		///     branch for every dialogue that wants the same behaviour. The action is what the strings have in common, so a
		///     future `RunToKhemi` or `SpawnBandits` is wired by stamping <c>NpcScriptEvent.Action</c> and nothing here
		///     changes. An unmapped event - four of the seven shipped - is logged rather than passed over silently: an
		///     unimplemented script that says nothing is indistinguishable from one that does not exist.
		/// </remarks>
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
					// The flags, plus the one record only this path can send. The simulation's own aggro pass then
					// hate-lists the player, draws combat mode and swings - the same burst it produces for any other
					// aggro, and the one retail sends here - so authoring that again from this path would be a second
					// copy of the combat opening.
					//
					// The collision-group restate is not part of that burst and has to go out here, because it is the
					// only thing that distinguishes this flip from an ordinary mob's aggro: an ordinary mob spawned in
					// the mob group already, and this one spawned as a townsperson. Without it the NPC swings at the
					// player while still drawing peaceful and refusing to be hit - see BroadcastNpcTurnedHostile for the
					// corpus measurement that dates it to exactly here, ahead of the hate list.
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
					// gone. Retail plays it out - MoveTask then n3ToClientQuit - rather than blinking her away, which is
					// why the exit point is the trigger object rather than a hard-coded pair of coordinates: the object
					// that opened this conversation is the place the escort was leading to, the same reasoning
					// `TryOpenTriggeredConversation` uses to find the escortee at all.
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
					// retail's second path agrees: on a relog Scavenger Tuut arrives in the ordinary world-entry stream
					// with no scry event, gated on the quest being held. The quest says who and this says when, which is
					// also why a future spawn script needs no code here - only a gated goal and this action stamped on
					// its event.
					await _broadcaster.RefreshQuestVisibility(client);
					_logger.LogInformation("Script: {Name} fired \"{Event}\" - its quests' creatures are now in the world.", node.NpcName, name);
					continue;
				}

				_logger.LogInformation("Script: {Name} fired \"{Event}\", which is harvested but carries no mapped action yet (#839).", node.NpcName, name);
			}
		}

		/// <summary>
		///     Where a leaving NPC walks to: the world object that opened this conversation. Null when the player opened it
		///     themselves, which is the honest answer - a farewell with no door behind it has no gate to head for.
		/// </summary>
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

		/// <summary>
		///     Completes any quest given by the NPC template whose objective is done (Active with GoalProgress at or above
		///     GoalRequired): marks it Completed and sends QuestIIR state 2 then 4, so the client journal moves it to the
		///     completed tab.
		/// </summary>
		/// <remarks>
		///     The kill only advances the objective; the quest is marked Completed here, on turn-in at the giver. Called
		///     after the greeting has been served, so the state change follows the text on the wire as in retail, and the
		///     greeting is picked while the quest is still Active - the player sees the turn-in line rather than the
		///     post-quest "already done" one.
		/// </remarks>
		private async Task TryTurnInQuests(INetworkClient client, uint npcTemplateId)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var givers = await _database.GetQuestGiverMap(); // NpcTemplateId -> EVERY quest id it grants
			if (!givers.TryGetValue(npcTemplateId, out var givenQuestIds))
				return;

			// Every quest the NPC grants, not just one: collapsing the map to a single quest id would let a giver who
			// hands out two - Turach's "The Quarry" and "The Guard", Laranga's "The Blacksmith" and "The Barmaid" - turn
			// in only one, leaving the other Active forever with its objective done.
			var quests = await _database.GetCharacterQuests(character.Id);
			foreach (var givenQuestId in givenQuestIds)
			{
				var held = Array.Find(quests, q => q.QuestId == givenQuestId);
				if (held == null || held.State != QuestState.Active || held.GoalRequired == 0 || held.GoalProgress < held.GoalRequired)
					continue;

				await _database.SetCharacterQuest(character.Id, held.QuestId, QuestState.Completed, held.GoalProgress, held.GoalRequired, held.GoalTargetName);
				_questBroadcaster.ForgetMarkerContext(character.Id);
				await QuestBroadcaster.SendQuestIir(client, held.QuestId, QuestCodec.StateActive);
				await QuestBroadcaster.SendQuestIir(client, held.QuestId, QuestCodec.StateCompleted);

				// A turn-in quest completes here, so this is where it pays. The guard above only lets a quest through
				// while it is still Active, so the flip - and the payment - happen exactly once.
				await _rewards.GrantCompletionRewards(client, held.QuestId);
				_logger.LogInformation("Quest {Quest} turned in to template {NpcTemplate} by {Char} (objective {Progress}/{Required}).", held.QuestId, npcTemplateId, character.Name,
					held.GoalProgress, held.GoalRequired);
			}
		}

		/// <summary>
		///     Clears the session's conversation state - the player is no longer in a dialogue. Sends nothing, so use it
		///     when the client ended the conversation by sending KnubotCloseChatWindow and no close should be echoed back.
		/// </summary>
		/// <remarks>Leaving the state set lets a stale conversation be re-entered.</remarks>
		public static void EndConversation(INetworkClient client)
		{
			// Remember which NPC was just closed, so the reflexive post-close greeting - which the client re-fires with
			// no new SelectDeselect - is absorbed rather than re-opening the dialogue. Cleared when the player
			// re-selects an NPC, which is genuine intent to talk again.
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
			// The close's KnubotBase header carries the NPC handle the client uses to tear down that dialogue and
			// release its dialogue camera, so it must be the live handle (retail sends e.g. 0x6A0A for Kalanthes).
			// Keeping the captured, stale handle leaves the client unable to match the live dialogue: the camera stays
			// locked until a relog, and the client re-fires TalkToNpc so the conversation re-opens.
			//
			// Which window to close: the caller's NPC, else the active dialogue's - captured before EndConversation
			// clears it - else the captured handle as a last resort.
			var target = npcHandle ?? client.Session.ConversationNpcHandle;
			// The close must ride the same GameAgent instance the conversation's screens used, captured before
			// EndConversation clears it; 0x08 is the fallback when there was no active conversation.
			var agentInstance = client.Session.ConversationAgentInstance ?? 0x08;
			EndConversation(client); // end any active conversation
			var id = client.Session.Character.Id;

			// Construct the close through KnubotCodec from the captured empty-remoteformat reply, re-addressed to the
			// live player and the live NPC; the captured string itself is reused verbatim. AsSpan(4) skips the leading
			// [u32 len]: the captured blob is the whole carrier payload and that prefix belongs to the sender, which
			// SendConstructedKnubot writes back on.
			var captured = (KnubotCloseChatWindowIIR)KnubotCodec.Parse(KnubotCloseChatWindowBody.AsSpan(4));
			var record = KnubotCodec.Build(captured with
			{
				PlayerInstance = id,
				NpcHandle = target ?? captured.NpcHandle
			});

			return SendConstructedKnubot(client, agentInstance, id, record);
		}

		/// <summary>
		///     Constructs a Knubot record from the structured columns: the screen is rebuilt from its typed
		///     ConversationScreen rows (RemoteFormatScreenCodec.Build) with the live player name and guild regenerated in.
		/// </summary>
		/// <remarks>
		///     The rows are gated byte-exact at import (ConversationScreenCodec.Parse), so this build is authoritative and
		///     there is no raw-data fallback. It is byte-identical to a raw-capture construct for the same inputs
		///     (ConversationConstructTests). The record carries no length prefix - the sender frames it.
		/// </remarks>
		internal static byte[] BuildKnubotFromColumns(ConversationPacket p, uint liveInstance, uint npcHandle, string playerName, string guildName, string npcGuildName = "",
			Sex playerSex = Sex.None)
		{
			var env = p.Env ?? 0;
			var screens = ConversationScreenColumns.ToScreens(p.Screens); // ordered RemoteFormatScreen list

			if (p.SubOpcode == ConversationSubOpcode.KnubotOpenChatWindow)
				return KnubotCodec.Build(new KnubotOpenChatWindowIIR(liveInstance, npcHandle, env, p.Flag ?? 0));
			if (p.SubOpcode == ConversationSubOpcode.KnubotAppendText)
				return KnubotCodec.Build(new KnubotAppendTextIIR(liveInstance, npcHandle, env, p.Field ?? 1, Screen(0), p.Flag ?? 1));

			// CloseChatWindow carries one screen behind a TextType word rather than a string list, and the stored Field
			// is that word. Every captured close holds exactly one screen.
			if (p.SubOpcode == ConversationSubOpcode.KnubotCloseChatWindow)
				return KnubotCodec.Build(new KnubotCloseChatWindowIIR(liveInstance, npcHandle, env, p.Field ?? 1, screens.Count == 0 ? [] : Screen(0)));

			return KnubotCodec.Build(new KnubotAnswerListIIR(liveInstance, npcHandle, env, screens.Select((_, i) => Screen(i)).ToList()));

			byte[] Screen(int i)
			{
				// With no live player - an empty name - keep the screen's stored content as-is: the construct-against-capture
				// gate feeds it capture-parsed rows and expects them back verbatim. Otherwise regenerate the session slots -
				// player name, their guild, the NPC guild label - from the live values.
				var screen = string.IsNullOrEmpty(playerName)
					? screens[i]
					: RemoteFormatSession.WithLivePlayer(screens[i], playerName, guildName ?? "", npcGuildName ?? "", playerSex);

				return Encoding.Latin1.GetBytes(RemoteFormatScreenCodec.Build(screen));
			}
		}

		/// <summary>
		///     Whether a conditioned variant applies: the player holds ConditionQuestId in state ConditionQuestState.
		/// </summary>
		/// <remarks>
		///     These are the two shapes retail's dialogue Lua used: <c>HasCompletedQuest(X)</c> is (X, Completed), and
		///     <c>HasQuest(X) and not HasCompletedQuest(X)</c> is (X, Active). Kept internal and static so
		///     ConversationConditionTests can pin the gate.
		/// </remarks>
		internal static bool QuestConditionMet(IQuestGated gated, IReadOnlyCollection<CharacterQuest> quests)
		{
			return gated.ConditionQuestId is { } questId && quests.Any(q => q.QuestId == questId && q.State == gated.ConditionQuestState);
		}

		/// <summary>
		///     Frames a constructed Knubot record as retail does, verified against the captures: the conversation runs
		///     sender <c>GameAgent:&lt;conversationAgentInstance&gt;</c> to receiver
		///     <c>GameInterface:&lt;playerHandle&gt;</c> on opcode 0x2000.
		/// </summary>
		/// <remarks>
		///     Retail varies the GameAgent instance per conversation (0x07 to 0x5a observed, 0x08 the most common) and the
		///     client addresses its KnubotAnswer back to whatever instance sent the screen, so the captured packet's sender
		///     instance is preserved rather than hardcoded. The carrier's [u32 len] is written here; KnubotCodec emits the
		///     record alone.
		/// </remarks>
		private static Task SendConstructedKnubot(INetworkClient client, uint senderInstance, uint liveInstance, byte[] record)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, senderInstance), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, liveInstance),
				GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
		}

		/// <summary>
		///     Constructs the dialogue camera (DispatchSetDialogueCamera 0x2015) from the structured columns:
		///     <c>GameCharAgent:&lt;agentInstance&gt;</c> to <c>GameCharInterface:&lt;player&gt;</c>, body
		///     [0xC350][npc][0xC350][player][params]. Byte-identical to the captured packet re-addressed, gated by the
		///     construct test.
		/// </summary>
		internal static Rpc BuildDialogueCameraPacket(uint agentInstance, uint liveInstance, uint npcHandle, CameraArgs camera)
		{
			return new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, agentInstance), Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceType, liveInstance),
				SetDialogueCameraCodec.Method, SetDialogueCameraCodec.Build(npcHandle, liveInstance, camera));
		}

		private static Task SendDialogueCamera(INetworkClient client, uint agentInstance, uint liveInstance, uint npcHandle, CameraArgs camera)
		{
			return client.SendAsync(BuildDialogueCameraPacket(agentInstance, liveInstance, npcHandle, camera));
		}

		// The camera arguments as they sit on the wire, rebuilt from the packet row's typed columns.
		internal static CameraArgs CameraArgsOf(ConversationPacket row)
		{
			ArgumentNullException.ThrowIfNull(row);

			return new CameraArgs(row.ShotSize ?? 0, row.ShotType ?? 0, row.ShotEmphasis ?? 0, row.ShotFov ?? 0, row.ShotTilt ?? 0, row.Autoface == true, row.HeadPaddingY ?? 0f);
		}
	}
}
