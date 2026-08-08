using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// One extracted conversation SCREEN, keyed by its answer-index PATH within the NPC's dialogue tree. The path is
	// the sequence of option indices the player picked to reach this screen ("" = the greeting, "0" = after picking
	// option 0, "0>1" = then option 1, ...). Packets are the ordered server responses to replay for that screen.
	// NpcName is resolved from the same capture's FA015807 spawn for the clicked handle (the stable identity the
	// runtime routes on; the handle is session-volatile and only used as a fallback identity when no name resolves).
	// (Mode/Field3 were the OLD key but both are session-volatile counters, not branch ids - the AnswerPath is the
	// stable branch key; memory protocol_conversation_routing.)
	// QuestContextId: the quest that was ACTIVE (last QuestFullUpdate) when the player opened this conversation - the
	// player's quest state AT THE GREETING. Lets a single capture that talks to one NPC at MULTIPLE chain stages (e.g.
	// Casilda: offer pre-quest, then "free me" once you have the key) be split per-state, since the answer path alone
	// ("" greeting both times) can't distinguish them (#264). 0 = no quest active at the greeting.
	// RepeatGreeting: whether this instance RE-opened a window the capture had already opened on that NPC. The gate the
	// runtime serves on is `RequiresRepeatGreeting`, assigned by ConversationVariants when the flag is what tells two
	// captures of one screen apart - retail's NPCs say a full welcome the first time and a terse line thereafter (#645).
	// ParentContentKey/OptionId: the screen the player was looking at when they picked, and the LDB id of the option
	// they clicked - both read straight off the wire (#642). This is what LINKS the walk to its parent in the folded
	// graph, and it has to be harvested rather than derived from the answer path: the path only says "one pick
	// shallower", and when a screen has quest-gated VARIANTS that is several different screens. Casilda's `2` sub-tree
	// is the worked example - harvested under her 5-option 12373 greeting, but ungated itself, so a path-derived parent
	// attached it to the 3-option unconditional greeting and index 2 named the wrong option ("Goodbye").
	public sealed record ConversationWalk(
		uint TypeId,
		string AnswerPath,
		uint CapturedNpcHandle,
		List<byte[]> Packets,
		string NpcName,
		uint? NpcTemplateId = null,
		uint? ConditionQuestId = null,
		QuestState? ConditionQuestState = null,
		uint GrantQuestId = 0,
		uint QuestContextId = 0,
		bool ServerInitiated = false,
		uint? TriggerWorldObjectId = null,
		IReadOnlyDictionary<uint, QuestState>? QuestStates = null,
		string ParentContentKey = "",
		uint OptionId = 0,
		bool RepeatGreeting = false,
		bool? RequiresRepeatGreeting = null);

	// Reconstructs each NPC's dialogue as a TREE of screens keyed by answer-index PATH, from the captured request/
	// response flow. The grammar (memory protocol_conversation_routing): a conversation INSTANCE = TalkToNpc(f3=0)
	// [greeting] then the run of server responses + the player's KnubotAnswer(idx) picks, until the next greeting (a
	// re-walk). The greeting's responses are the "" screen; the responses after the player's first pick (idx i0) are
	// the "i0" screen; after the next pick they're "i0>i1"; etc. The branches are spread across the re-walks (each
	// Mode picked different options), so MERGING the instances by answer-path unions the full option tree.
	// `packets` must be one capture's GameServer packets in chronological order.
	public static class ConversationExtractor
	{

		// How far back a UseObject may sit and still be taken as the TRIGGER for a server-opened window. MEASURED over
		// the whole corpus: both door conversations open 45 packets after their UseObject, and the nearest interaction
		// behind any of the five proximity-triggered ones is 134 - so the two populations are cleanly separated and 90
		// sits between them with margin on both sides.
		//
		// Deliberately not "the last UseObject, whenever it was": attributing the WRONG trigger is worse than
		// attributing none, since none merely leaves the node waiting for the proximity model.
		private const int UseObjectTriggerWindow = 90;

		// How recently a quest must have changed state for that change to be read as the beat a server-opened
		// conversation is ABOUT. Same reasoning as the trigger window, and the same failure it prevents: without it the
		// "most recent" change can be arbitrarily stale - Saddur's beach confrontation was gated on quest 12407, whose
		// grant was 6,600 packets earlier, when it plainly belongs to 10238 ("kill Saddur"), which completes moments
		// AFTER it. Casilda's farewell sits 30 packets after its 12407 completion, so a window of a few hundred keeps
		// the real gate and drops the stale one. A WRONG gate is worse than none: it hides the conversation from every
		// player not in that state.
		private const int QuestStateGateWindow = 300;

		// The speaker's template id inside a screen's `npcname` parameter - see NpcTemplateFromText.
		private static readonly Regex NpcNameParameter =
			new Regex(@"<parameter\s+value=""[^""]*?id=&quot;(\d+)&quot;[^""]*?category=&quot;51000&quot;[^""]*""\s+name=""npcname""", RegexOptions.Compiled);

		// The Knubot conversation-display response sub-opcodes - the family KnubotCodec parses (642CD3D6 echo excluded:
		// it is a generic interact ack, not a screen).
		private static readonly HashSet<uint> ResponseSubOpcodes = [.. KnubotCodec.HandledTypes.Select(t => (uint)t)];

		public static List<ConversationWalk> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var namesByHandle = BuildSpawnNameMap(packets);
			// handle -> NPC TEMPLATE id (#244): the runtime gates the conversation on the clicked NPC's template so a
			// same-named NPC (25 share "Halen van Dummy") can't open another's dialogue.
			var templatesByHandle = NpcSpawnExtractor.Extract(packets).Where(e => e.Handle != 0 && e.TemplateId != 0).GroupBy(e => e.Handle).ToDictionary(g => g.Key, g => g.First().TemplateId);

			// template -> name, joined over the handle the two maps share. A SERVER-opened conversation (#523) has no
			// clicked handle to name its NPC by, but its text carries an `npcname` parameter holding the template id -
			// so this is how those get attributed.
			var namesByTemplate = new Dictionary<uint, string>();
			foreach (var (handle, template) in templatesByHandle)
			{
				if (namesByHandle.TryGetValue(handle, out var n) && !string.IsNullOrEmpty(n))
					namesByTemplate.TryAdd(template, n);
			}

			// handle -> the STABLE WorldObjects.Id of the object with that handle in this capture, so a UseObject trigger
			// resolves to a row rather than to a session-volatile handle. Same identity function the world-objects import
			// uses, so the two agree by construction.
			var worldObjectIdByHandle = new Dictionary<uint, uint>();
			foreach (var w in WorldObjectExtractor.Extract(packets))
			{
				if (w.Handle == 0)
					continue;

				try
				{
					worldObjectIdByHandle[w.Handle] = ImportOps.PopulateObject(new WorldObject
					{
						MapId = w.MapId,
						PositionX = w.X,
						PositionY = w.Y,
						PositionZ = w.Z
					}, w).ComputeId();
				}
				catch
				{
					// An object whose blob will not populate cannot be a trigger target; it is not a conversation error.
				}
			}

			var entries = new List<ConversationWalk>();

			// Per-conversation-instance state, reset at each greeting (TalkToNpc f3=0).
			var active = false;
			uint typeId = 0, npc = 0, instance = 0;
			var serverInitiated = false; // this instance was opened BY THE SERVER, with no player TalkToNpc (#523)
			uint serverNpcTemplate = 0; // its NPC, read out of the text's `npcname` parameter (there is no clicked handle)
			uint? serverTrigger = null; // the world object whose use opened it, when one is close enough behind
			// The quest whose state changed most recently, and that state. A server-opened conversation is ABOUT a quest
			// beat, and this is the cheapest honest reading of which: at Casilda's two openings it is 12407 both times,
			// Active at the first and Completed at the second - which is exactly what tells her two "" screens apart.
			uint lastQuestId = 0;
			QuestState? lastQuestState = null;
			var lastQuestAt = int.MinValue;
			// The player's WHOLE quest state as the capture has told it so far - every quest the capture has seen a
			// full-update or a state flip for, in its latest state. QuestContextId (the single last quest) is a
			// one-quest projection of this, and it is too coarse to tell some variants apart: the Hoxxling gameplay
			// capture greets Casilda TWICE at context 13479 and gets a different option list each time, because what
			// actually changed between them was a DIFFERENT quest's state (ConversationVariants.SplitConflicts).
			var questStates = new Dictionary<uint, QuestState>();
			(uint? Quest, QuestState? State) serverCondition = (null, null);
			uint lastUseObjectHandle = 0;
			var lastUseObjectAt = int.MinValue; // packet index of that UseObject, for the window test
			var index = -1;
			var path = ""; // answer-index path of the CURRENT screen (the one whose responses we're collecting)
			var screen = new List<byte[]>();
			var parentContentKey = ""; // the screen the player picked FROM, by content (#642)
			uint optionId = 0; // and the LDB id of the option they clicked
			uint grant = 0; // a quest the server granted while collecting THIS screen's responses (0 = none)
			var questsSeen = new HashSet<uint>(); // every quest whose full-update appeared earlier in THIS capture
			// The last quest whose full-update appeared - the active quest at any point in the capture.
			//
			// ⚠️ SEEDED from the capture's first quest JOURNAL, because a full-update only fires on a grant or a
			// re-display: a capture that opens mid-chain has none for the quest the player is already on, so every
			// greeting before the first grant used to read as context 0. That made 0 mean two different things -
			// "no chain progress" and "we have not been told yet" - and the two collide (#526). The journal is the
			// missing half: it is the whole J-panel, so its highlighted entry names the quest the player is on
			// REGARDLESS of when the capture started. In the necromancer play2 capture the first journal arrives at
			// 84.5s naming 12373, which is what the player was on for the two Turach greetings at 29.3s and 43.6s.
			var currentQuest = InitialQuestContext(packets);
			var instanceContext = currentQuest; // currentQuest AT the active instance's greeting - the player's state when they talked
			var instanceQuests = new Dictionary<uint, QuestState>(); // questStates AT that greeting - the whole picture, not just the last quest
			// Which NPCs the player has ALREADY talked to here, and whether the active instance is a re-greeting (#645).
			//
			// Retail's greeting is not one line: an NPC says a full welcome the first time you open his window and a
			// terse one every time after ("Hello.{social greet}" -> "What now?"). Four of the six screens the quest-state
			// split could not tell apart are exactly that pair, and the corpus states it outright - in every capture that
			// opens one of these windows twice, the first opening is the welcome and every later one the terse form.
			// Keyed by the clicked HANDLE, which is the specific NPC the player re-approached.
			var greeted = new HashSet<uint>();
			var instanceRepeat = false;

			foreach (var p in packets)
			{
				index++;
				if (p is { IsDownload: false, IsIirCarrier: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
				{
					if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var interact))
						continue;

					// A USE-OBJECT is remembered, not acted on: it is the trigger for a window the server opens a moment
					// LATER (#523), so it only means anything once an unattributed OpenChatWindow shows up behind it.
					if (interact.BehaviorId == (uint)BehaviorId.UseDynel)
					{
						// ⚠️ The object handle is in the behaviour TAIL, NOT in args.Target - see BehaviorTailScan.
						if (BehaviorTailScan.InteractTargetHandle(interact.Tail) is var target and not 0)
						{
							lastUseObjectHandle = target;
							lastUseObjectAt = index;
						}

						continue;
					}

					if (interact.BehaviorId != (uint)BehaviorId.UseCharacter)
						continue;

					var (parsedTypeId, _, field3, parsedNpc) = ParseArgs(interact.Args);
					if (field3 != 0)
						continue; // a non-greeting TalkToNpc (retransmit / options re-request) - not an instance boundary

					// A greeting opens a new conversation instance: flush the previous one's last screen, then reset.
					Flush();
					active = true;
					serverInitiated = false;
					serverNpcTemplate = 0;
					serverTrigger = null;
					serverCondition = (null, null);
					typeId = parsedTypeId;
					npc = parsedNpc;
					instance = interact.Actor;
					path = "";
					screen = [];
					grant = 0;
					parentContentKey = "";
					optionId = 0;
					instanceContext = currentQuest; // the quest active WHEN the player opened this conversation (#264)
					instanceQuests = new Dictionary<uint, QuestState>(questStates);
					instanceRepeat = !greeted.Add(npc);
				}
				else if (p is { IsDownload: true, IsIirCarrier: true, SubOpcode: ConversationScreenDecoder.OpenChatWindow } && !active)
				{
					// #523: retail also opens a conversation ITSELF, with no player TalkToNpc in front of it - Casilda's
					// city-door farewell is the worked example. Every one of those used to be dropped on the floor, taking
					// its {resolvegoal}/{gameactiononnpc} payload with it, because only a greeting could start an instance.
					//
					// An OpenChatWindow arriving while NO instance is active is exactly that case: a window opened inside
					// an active instance is that instance's own, and is collected as a response below.
					Flush();
					active = true;
					serverInitiated = true;
					serverNpcTemplate = 0; // resolved from the first text screen's `npcname` parameter
					// Gate the conversation on the quest beat it belongs to (#523). Casilda's two server-opened screens
					// both sit at AnswerPath "" and both are about quest 12407 - the first while it is Active, the second
					// once the door has Completed it - so the state is what tells them apart, through the SAME
					// ConditionQuestId/State the talk tree already uses (and which ComputeId already keys on).
					serverCondition = lastQuestId != 0 && index - lastQuestAt <= QuestStateGateWindow
						? ((uint?)lastQuestId, lastQuestState)
						: (null, null); // nothing changed recently enough to be what this conversation is about
					serverTrigger = index - lastUseObjectAt <= UseObjectTriggerWindow
						? worldObjectIdByHandle.GetValueOrDefault(lastUseObjectHandle) is var w and not 0
							? w
							: null
						: null; // no interaction close enough behind it -> a proximity trigger we cannot name yet
					typeId = 0; // there is no BCC action to take one from - see ConversationNode.ServerInitiated
					npc = 0;
					path = "";
					screen = [p.Bytes];
					grant = 0;
					parentContentKey = "";
					optionId = 0;
					instanceContext = currentQuest;
					instanceQuests = new Dictionary<uint, QuestState>(questStates);
					instanceRepeat = false; // a server-opened window is an EVENT, not a greeting the player repeated
				}
				// The player picked option `idx`: the current screen is complete (emit it), and the responses that
				// follow belong to the child screen reached by that option. Selected by the RECORD's type rather
				// than by a sub-opcode - the family codec is what knows which sub-opcodes it owns (#534), and rule 7
				// keeps an IIR opcode from being named outside Codecs/.
				else if (p is { IsDownload: false, IsIirCarrier: true } && active
					&& p.TryParse(KnubotCodec.Parse, out var record) && record is KnubotAnswerIIR pick)
				{
					var idx = pick.Index;
					// The screen being flushed IS the one that offered this option, so name the branch off it - that is
					// the whole point of harvesting the link rather than deriving it from the answer path.
					//
					// ⚠️ Named BEFORE the flush but ASSIGNED after it: Flush() emits the walk for the screen the player
					// is leaving, and that walk carries the link that reached IT, not the one being taken now.
					var columnized = ConversationGraph.Columnize(screen);
					var offered = ConversationGraph.OptionIdsOf(columnized);
					var named = idx < offered.Count;
					Flush();
					parentContentKey = named ? ConversationGraph.ContentKeyOf(columnized) : "";
					optionId = named ? offered[(int)idx] : 0;
					path = path.Length == 0
						? idx.ToString()
						: $"{path}>{idx}";
					screen = [];
					grant = 0;
				}
				else if (p is { IsDownload: true, IsIirCarrier: true, SubOpcode: QuestCodec.Opcode })
				{
					// Only the two states the journal actually flips through are taken.
					if (p.TryParse(QuestCodec.Parse, out var flip) && flip.State is QuestCodec.StateActive or QuestCodec.StateCompleted)
					{
						lastQuestId = flip.QuestId;
						lastQuestState = flip.State == QuestCodec.StateActive
							? QuestState.Active
							: QuestState.Completed;
						lastQuestAt = index;
						questStates[flip.QuestId] = lastQuestState.Value;
					}
				}
				else if (p is { IsDownload: true, IsIirCarrier: true, SubOpcode: QuestFullUpdateCodec.Opcode })
				{
					// A quest full-update. It's a GRANT (vs a login restore or a re-display) only if: a conversation is
					// active, the player has navigated past the greeting (path != ""), and this is the quest's FIRST
					// appearance in the capture (a restore re-sends a quest first seen at login). That pins the accept to
					// the dialogue node the player walked. Always remember the quest so later re-sends aren't mistaken
					// for grants.
					//
					// Only the quest ID is wanted, and the record around it is the one region a level-80 journal can
					// still fail to decode - so this reads the id off the record's own key word rather than gating the
					// grant signal on the whole record parsing (the same reason ItemHashHarvester takes SnapshotRoot
					// over the full snapshot codec, #383).
					if (QuestFullUpdates.TryReadQuestId(p.Record, out var questId))
					{
						currentQuest = questId; // track the active quest for the per-greeting QuestContextId (#264)

						// A full-update is also how a quest BECOMES active (the grant), and the escort arrives that way -
						// 12407 is granted by a full-update with no accompanying state flip - so it must count here too or
						// the gate for anything opened during it reads as "no quest".
						lastQuestId = questId;
						lastQuestState = QuestState.Active;
						lastQuestAt = index;

						// A full-update does NOT un-complete a quest: retail re-sends the record on a re-display, and the
						// journal keeps showing completed entries. Only an explicit state flip may move a quest back.
						if (questStates.GetValueOrDefault(questId) != QuestState.Completed)
							questStates[questId] = QuestState.Active;
						if (active && path.Length != 0 && questsSeen.Add(questId))
							grant = questId;
						else
							questsSeen.Add(questId);
					}
				}
				else if (p is { IsDownload: true, IsIirCarrier: true, SubOpcode: ConversationScreenDecoder.CloseChatWindow } && active)
				{
					// The window CLOSES: the instance is over. The close packet is part of the exchange, so it joins the
					// screen before the flush.
					//
					// ⚠️ Nothing used to end an instance - `active` was set at the first greeting and never cleared - so
					// every Knubot packet for the rest of the capture was glued onto whatever screen was last open. That
					// is what hid the server-opened conversations: they could not start one (an instance was always
					// "active"), and their screens were appended to an unrelated NPC's last screen instead. OpenChatWindow
					// and CloseChatWindow are exactly paired on the wire (4 and 4 in the beach-to-city capture), so the
					// close is the reliable boundary.
					screen.Add(p.Bytes);
					Flush();
					active = false;
					screen = [];
				}
				else if (p.IsDownload && active && (p.WireOpcode == CapturedPacket.Wire(GameCharInterfaceMethods.DispatchSetDialogueCamera) || (p.IsIirCarrier && ResponseSubOpcodes.Contains(p.SubOpcode))))
				{
					screen.Add(p.Bytes);
					if (serverInitiated && serverNpcTemplate == 0)
						serverNpcTemplate = NpcTemplateFromText(p);
				}
			}

			Flush();
			return entries;

			void Flush()
			{
				if (!active || screen.Count == 0)
					return;

				// A server-opened instance is named through its template (there is no clicked handle); a player-opened
				// one through the handle it clicked. Either can fail to resolve, and the fallback keeps the entry
				// DISTINCT and addressable rather than dropping it - a silent skip here is data loss.
				var (name, template) = serverInitiated
					? (namesByTemplate.GetValueOrDefault(serverNpcTemplate) ?? (serverNpcTemplate != 0
						? $"#tpl{serverNpcTemplate}"
						: ""), serverNpcTemplate == 0
						? (uint?)null
						: serverNpcTemplate)
					: (namesByHandle.GetValueOrDefault(npc)!, templatesByHandle.TryGetValue(npc, out var tpl)
						? tpl
						: null);


				entries.Add(new ConversationWalk(typeId, path, npc, [.. screen], name, template,
					ConditionQuestId: serverInitiated
						? serverCondition.Quest
						: null,
					ConditionQuestState: serverInitiated
						? serverCondition.State
						: null,
					GrantQuestId: grant, QuestContextId: instanceContext, ServerInitiated: serverInitiated,
					TriggerWorldObjectId: serverTrigger, QuestStates: new Dictionary<uint, QuestState>(instanceQuests),
					ParentContentKey: parentContentKey, OptionId: optionId, RepeatGreeting: instanceRepeat));
			}
		}

		// Every distinct NPC (by resolved spawn name) the player sent a GREETING TalkToNpc to (field3 == 0) in these
		// packets - the DENOMINATOR for conversation coverage. An NPC greeted here but absent from ConversationNodes is
		// a gap: a dump of the interaction exists, but no dialogue was decoded/imported for it (it fell to the "no
		// dialogue imported" bubble). Greetings whose handle isn't in the capture's spawn-name map resolve to null and
		// are dropped (not attributable to a named NPC).
		public static IReadOnlyCollection<string> GreetedNpcNames(IReadOnlyList<CapturedPacket> packets)
		{
			var namesByHandle = BuildSpawnNameMap(packets);

			// Only handles that spawned as an NPC count. UseCharacter (0x16) is the generic "interact with a character", so
			// aiming it at a PLAYER is a TRADE REQUEST (#101) rather than a greeting - and since the spawn name map
			// covers players too, the two-client capture had both players counted as dialogue NPCs with missing
			// dialogue, quietly deflating the coverage figure this feeds (34/37 -> 34/39) and putting player names in
			// the gap list. A player has no NPC template, which is the cheapest way to tell them apart.
			var npcHandles = NpcSpawnExtractor.Extract(packets).Where(e => e.Handle != 0 && e.TemplateId != 0).Select(e => e.Handle).ToHashSet();
			var names = new HashSet<string>();
			foreach (var p in packets)
			{
				if (p is not { IsDownload: false, IsIirCarrier: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
					continue;

				if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var interact) || interact.BehaviorId != (uint)BehaviorId.UseCharacter)
					continue;

				var (_, _, field3, parsedNpc) = ParseArgs(interact.Args);
				if (field3 != 0)
					continue;

				if (npcHandles.Contains(parsedNpc) && namesByHandle.GetValueOrDefault(parsedNpc) is { Length: > 0 } name)
					names.Add(name);
			}

			return names;
		}

		// The quest the player was already on when the capture STARTED, from the first QuestJournalIIR in it.
		//
		// The journal is re-sent whole on every change and its trailing block names one quest again - in every capture
		// the entry that was just accepted or advanced (QuestJournalCodec). At the FIRST journal of a capture that is
		// the quest the player was carrying before we started watching, which is exactly the context a pre-grant
		// greeting needs. 0 when the capture holds no journal, or none that parses - the honest answer there is still
		// "we do not know", and the caller treats 0 as the unconditional fallback.
		//
		// A journal that fails to parse is skipped rather than fatal: this is a context HINT for grouping, and a
		// capture whose journal we cannot read must still contribute its dialogue.
		private static uint InitialQuestContext(IReadOnlyList<CapturedPacket> packets)
		{
			foreach (var p in packets)
			{
				if (p is not { IsDownload: true, IsIirCarrier: true, SubOpcode: QuestJournalCodec.Opcode })
					continue;

				try
				{
					var journal = QuestJournalCodec.Parse(p.Record);
					if (journal.Current.QuestId != 0)
						return journal.Current.QuestId;

					// A one-quest journal carries (0, 0, [], 0) in the trailing block, so fall back to the single entry
					// the area block does hold.
					var only = journal.Area.Entries;
					return only.Count > 0 ? only[^1].QuestId : 0;
				}
				catch (CodecParseException)
				{
					return 0;
				}
			}

			return 0;
		}

		// Merges screens from many captures/instances, deduped by (NpcTemplateId, TypeId, AnswerPath) - the stable
		// branch key. This is what UNIONS the option tree: each conversation re-walk (a different Mode) explored a
		// different option at a screen, so it contributes a different answer-path screen; merging them assembles the
		// full tree. The winner is the MOST COMPLETE capture of the screen - the one with the most response packets; a
		// truncated capture must never displace a full one.
		//
		// #646: the identity is the NPC's TEMPLATE, not its display name. Merging by name would union the walks of two
		// same-named but DIFFERENT NPCs into one screen here, before the fold ever sees them - and a name is ambiguous
		// in the world ("Halen van Dummy" is 27 templates). A walk whose template did not resolve falls back to the
		// name and then to the captured handle: it cannot become a row either way (the fold drops it), but grouping it
		// by the best signal left is what makes the fold's report say "Kalanthes: 5 walks" rather than one line per
		// handle.
		//
		// It used to also prefer a capture whose player instance was a 4-byte varint, because the runtime SUBSTITUTED
		// the live instance into the replayed bytes and a shorter captured varint changed the record length. #188
		// retired that path - every packet is now constructed from typed columns with the live instance - so the term
		// selected for a property that could no longer affect anything. Measured over the whole corpus before
		// removing it: 242 merge keys, 29 with more than one candidate, and the winner changed in ZERO of them.
		// The NPC a walk belongs to, for grouping: its TEMPLATE where one resolved (#646), else the display name, else
		// the captured handle. Shared by Merge and ConversationVariants so the two cannot disagree about which walks
		// belong to the same NPC.
		public static string WalkIdentity(ConversationWalk walk)
		{
			ArgumentNullException.ThrowIfNull(walk);
			if (walk.NpcTemplateId is { } template)
				return template.ToString(CultureInfo.InvariantCulture);

			return string.IsNullOrEmpty(walk.NpcName)
				? $"#{walk.CapturedNpcHandle}"
				: walk.NpcName;
		}

		public static List<ConversationWalk> Merge(IEnumerable<ConversationWalk> all)
		{
			var best = new Dictionary<(string, uint, string, uint?, QuestState?, uint?, bool?), (int score, ConversationWalk entry)>();
			foreach (var e in all)
			{
				var identity = WalkIdentity(e);
				// The quest-state gate is part of the key so a NPC's conditioned variants (e.g. Casilda's pre- vs
				// post-first-quest greeting) stay SEPARATE rows instead of merging into one mixed screen.
				// The TRIGGER is part of the key for the same reason it is part of ConversationEntry's identity: an NPC can
				// have SEVERAL server-opened conversations, and they all sit at AnswerPath "". Without it Casilda's two
				// collapse into one here - before the importer ever computes an id - and the shorter (her city-door
				// "{hidegoodbye}Friend?" opener) loses to the longer.
				// RequiresRepeatGreeting likewise (#645): a first-meeting welcome and its terse repeat are one screen at one
				// answer path with the same quest state, so without it the packet-count tiebreak drops one of them.
				var key = (identity, e.TypeId, e.AnswerPath, e.ConditionQuestId, e.ConditionQuestState, e.TriggerWorldObjectId, e.RequiresRepeatGreeting);
				var score = e.Packets.Count;
				if (!best.TryGetValue(key, out var cur) || score > cur.score)
				{
					// Preserve a grant seen in any capture of this screen even if the highest-scoring replay didn't see it.
					var grant = e.GrantQuestId != 0
						? e.GrantQuestId
						: cur.entry?.GrantQuestId ?? 0;
					best[key] = (score, e with
					{
						GrantQuestId = grant
					});
				}
				else if (e.GrantQuestId != 0 && cur.entry.GrantQuestId == 0)
				{
					best[key] = (cur.score, cur.entry with
					{
						GrantQuestId = e.GrantQuestId
					});
				}
			}

			return best.Values.Select(v => v.entry).ToList();
		}

		// Maps each FA015807 spawn's handle to its embedded name (0x0A <len> <utf8>), so a conversation's NPC handle
		// can be resolved to a stable name within the same capture.
		internal static Dictionary<uint, string> BuildSpawnNameMap(IReadOnlyList<CapturedPacket> packets)
		{
			var map = new Dictionary<uint, string>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || !p.IsIirCarrier || p.SubOpcode != SimpleCharFullUpdateCodec.Opcode)
					continue;

				var body = p.Body;
				if (body.Length < SpawnPacketBuilder.HandleOffset + sizeof(uint))
					continue;

				// The name comes off the CoreCharacter message (#541); the handle is at the codec's own header
				// offset. This used to use a lenient byte scan that started at offset 16 and accepted a
				// SINGLE-character run with no letter in it - which on one captured spawn named a Crocodile "\",
				// because a stray 0x0A ahead of the message won. The reader finds the same name on every other one
				// of the 11632 captured spawns, and names nothing extra.
				if (SpawnBodyReader.TryReadCharacterMessage(body, out var read) && read.Message.Name is { Length: > 0 } name)
					map[body.ReadU32Be(SpawnPacketBuilder.HandleOffset)] = name;
			}

			return map;
		}


		// The TalkToNpc action's args message. This is a CommandMessage (the client-recovered BCC payload), NOT the
		// old hand-reconstructed "ProtoInteractIIR" - see Gitea #387. It used to be walked by a hand-written
		// tag/varint parser here, duplicating what the generated class does; the call sites already guarantee the
		// span is the args protobuf (they check the actionType and that the declared length fits), so a decode
		// failure is a real surprise and is left to throw rather than silently returning zeros.
		private static (uint BccActionId, uint CommandqueueClientId, uint CommandqueueServerId, uint NpcTemplate) ParseArgs(ReadOnlySpan<byte> a)
		{
			var args = a.FromProtobuf<CommandMessage>();

			// target is a ProtoID32 { type, instance }; the NPC handle is its instance. (The old model called this
			// field a "SpellTarget" and its first word a spell_id - it is an ENTITY CLASS, e.g. 0xC350.)
			return (args.BccActionId, args.CommandqueueClientId, args.CommandqueueServerId, args.Target?.Instance ?? 0);
		}

		// The NPC TEMPLATE id a conversation screen names itself with, or 0.
		//
		// Every screen retail sends carries an `npcname` parameter holding a typed LDB reference to the speaker:
		//   <parameter value="7;&lt;localized id=&quot;9411&quot; category=&quot;51000&quot; … &gt;" name="npcname" />
		// Category 51000 is `npc_template.name`, so the `id` IS the template id - 9411 being Casilda. That is the only
		// statement of WHO is talking in a server-opened conversation, which has no clicked handle to resolve.
		//
		// The value is XML-escaped inside the attribute, so the inner id is matched as `&quot;`-wrapped rather than by
		// unescaping the whole document. Returns 0 for any screen that carries no such parameter.
		private static uint NpcTemplateFromText(CapturedPacket p)
		{
			if (ConversationScreenDecoder.Decode(p.Bytes)?.Screens is not { } screens)
				return 0;

			foreach (var xml in screens)
			{
				var m = NpcNameParameter.Match(xml);
				if (m.Success && uint.TryParse(m.Groups[1].Value, out var template))
					return template;
			}

			return 0;
		}


	}
}
