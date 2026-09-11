using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	/// <summary>
	///     Decides what a quest NPC is worth to a given player - the marker byte over its head, whether it owes them a
	///     quest item off its corpse, and which quests they hold. It reads the database and answers a question; it
	///     sends nothing and touches no world model.
	/// </summary>
	/// <remarks>
	///     This is the state-and-decision half of the quest-marker path. The wire half belongs to
	///     <see cref="World.WorldBroadcaster" />, which owns the "which entity is this, and who can see it" questions
	///     (<c>RefreshQuestMarkerFor</c>, <c>RefreshQuestGiverMarker</c>, <c>QuestMarkerStateForHandle</c>,
	///     <c>SendQuestMarker</c>) and calls in here for the answer. The split is what lets one answer serve both the
	///     interest-spawn push and the 0x2020-poll reply without either path re-deriving it.
	/// </remarks>
	public sealed class QuestMarkerResolver
	{
		// DispatchSetQuestMarker (0x2008) state byte. The enum is the client's own, from
		// `n3VisualDynel::SetQuestMarker` (`FUN_005affb0`), which switches the byte straight onto a particle effect
		// (`GamecodeClient_t::SetQuestMarker` @01395460 resolves the ID32 and passes the byte through untouched):
		//
		//   0x00  (no effect - clears whatever was there)   0x02  quest_new_active        = gold '!'
		//   0x01  quest_new_inactive       = silver '!'     0x04  quest_progress_active   = gold '?'
		//   0x03  quest_progress_inactive  = silver '?'  <- exists in the client, never sent by retail
		//
		// So the axes are `new`(!) vs `progress`(?), each `active` (bright) or `inactive` (the greyed form). `0x03` is
		// real and unused: zero packets carry it across the whole corpus (`--report quest-markers`), so it gets no
		// constant here either - a constant for a value nothing emits would be an unsupported claim.
		//
		// The client applies no level logic - it plays the effect it is told to. "Grey means come back at a higher
		// level", and the far-below-you quest that draws nothing, are both server choices of which byte to send, so
		// neither is recoverable by more RE of the client.
		// [V1: tools/ghidra/DecompileFns.java FNS=005affb0]
		//
		// Silver is the second most common state on the wire: of the 301 replies in
		// xxx_mage-necromancer_login_play2, 181 are 0x00, 74 are 0x01, 30 are 0x02, 16 are 0x04. 0x00 and 0x01 are not
		// interchangeable - 52 NPCs in that capture only ever returned 0x00, and they are the ones no quest touches at
		// all. Silver says "a chain runs through this NPC"; none says "this NPC is scenery".
		//
		// Silver is mostly not about what this NPC grants (`--report quest-markers` over 34,837 resolved marker
		// packets). Of the 456 silver packets retail sent, only 31% are on an NPC that grants anything at all, while
		// 85% are on an NPC some untouched quest's goal will send the player to. Gold '!' is the opposite: 68% grant a
		// quest the player has neither taken nor finished, and zero are on a grant the player already holds. That is
		// why `ChainRunsThrough` exists - 38 of the 62 NPC templates retail ever lit grant nothing.
		//
		// The rule ships with a measured error: `--report quest-markers` scores it against retail and it lights 6
		// packets across 2 NPCs that retail leaves dark (0.03% of the dark population) while demonstrating no positive
		// hit at all - every one of the 50 silver packets whose viewer level the corpus captured sits on an NPC the
		// quest harvest holds no relation for. The line clause alone, which needs no viewer level and so has nine times
		// the evidence, recovers 80% of retail's silver (278 of 347).
		//
		// The condition is the quest line, not the quest. Casilda looks like a counterexample - 13480 targets her and
		// is untouched while she is dark 178 times - until the relation is read one level up: 13480 is in line 1054,
		// the line the player is already on. Of the 257 packets about an NPC whose every line the player had already
		// started, zero carried silver (229 dark, 27 gold '?', 1 gold '!'). So silver means "a chain you have not begun
		// runs through here", and once you are on that chain the NPC goes dark until it is your turn to act on them.
		// Turach stays silver because lines 222 and 1305 pass through him untouched, not because of an unharvested
		// grant.
		//
		// That is necessary, not sufficient: an untouched line lights an NPC only 20% of the time. The second gate is
		// level, and it is two-sided - retail lights a marker only for a quest at or near the player's own level:
		//
		//   delta (player - quest)   -76/-75/-57  |   -7   |  -2..+1  |  +56  |  +57/+75
		//   lit / observations          0 / 243   | 0 / 10 | 299/382  | 0/127 |  0 / 37
		//
		// Both cutoffs are bracketed, not pinned - the corpus is sparse in delta and the gaps either side of the band
		// hold no observation at all: a quest above the player is dark somewhere in `-7 < T <= -2`, one below them
		// somewhere in `1 < T <= 56`. Picking a number inside an unobserved gap would be inventing data, so what ships
		// is the observed edge (see LitBandBelowPlayer).
		//
		// That measurement depends on the completed-quest archive being read out of the login snapshot: the live
		// QuestIIR traffic is one packet in the level-80 captures, so without it every quest they ever finished reads
		// as "never taken" and their darkness looks like a level rule when it is just completion.
		public const byte None = 0x00, Unavailable = 0x01, Available = 0x02, TurnIn = 0x04;

		/// <summary>
		///     The band of (player level − quest level) in which retail was observed to light a marker: -2 through +1.
		///     Outside it every observation is dark - 0/243 for a quest far above the player, 0/164 far below.
		/// </summary>
		/// <remarks>
		///     These are the observed edges, not the real cutoffs. The corpus brackets each cutoff without pinning it
		///     (<c>-7 &lt; T &lt;= -2</c> above, <c>1 &lt; T &lt;= 56</c> below) and the gaps hold no observation at
		///     all, so widening into a gap would be inventing data while the observed edge is the data. The band
		///     therefore errs by going dark inside the gaps rather than by lighting something retail would not.
		///     Pinning the cutoffs needs an in-client run: park a character beside one giver and <c>.setlevel</c>
		///     upward. Open work tracked in Gitea #848.
		/// </remarks>
		public const int LitBandBelowPlayer = -2, LitBandAbovePlayer = 1;

		private readonly ICharacterQuestRepository _quests;
		private readonly INpcRepository _npcs;
		private readonly IWorldRepository _world;
		private readonly ILogger _logger;

		// Quest-giver NPC template id -> every quest it grants, cached once (small set).
		private Dictionary<uint, uint[]> _questGivers = null!;

		// NPC template id -> every quest whose goal targets it, cached the same way. This is the relation the marker
		// rule is mostly about, and the giver map does not carry it.
		private Dictionary<uint, uint[]> _goalTargets = null!;

		public QuestMarkerResolver(ICharacterQuestRepository quests, INpcRepository npcs, IWorldRepository world, ILogger logger)
		{
			_quests = quests;
			_npcs = npcs;
			_world = world;
			_logger = logger;
		}

		/// <summary>
		///     Whether any quest touches this NPC template at all - it grants one, or a quest's goal sends a player to
		///     it. The cheap gate the spawn push applies before computing a state it would only throw away.
		/// </summary>
		/// <remarks>
		///     This gate must stay at least as broad as the marker rules it guards: an NPC it rejects never reaches
		///     <see cref="MarkerStateFor" />, so a gate narrower than the rule silently caps the marker population and
		///     looks like the rule never firing. In particular it must admit non-givers, 38 of the 62 templates retail
		///     lights grant nothing.
		/// </remarks>
		public async Task<bool> IsQuestRelevant(uint npcTemplateId)
		{
			var givers = await Givers();
			var targets = await Targets();
			return givers.ContainsKey(npcTemplateId) || targets.ContainsKey(npcTemplateId);
		}

		// The quest-marker state byte for a viewer + an NPC in view, from the player's CharacterQuests: no quest held
		// -> '!' (0x02); goals complete -> '?' (0x04); active/in-progress -> cleared (0x00). Shared by the
		// interest-spawn push and the 0x2020-poll reply so they never disagree.
		//
		// It takes the entity rather than a name because its two questions want different keys: "is this the NPC my
		// next talk goal names" is name-vs-name, since a talk goal's target is a string in the client's own RDB data,
		// while "does this NPC give a quest" is answered by the template, which is what the dialogue graph the giver
		// map is derived from is keyed by.
		//
		// This overload builds a context for one call. A batch of NPCs must build one context (`ContextFor`) and pass
		// it to the overload below - that is the difference between a 2.5 s entry step and a 30 ms one.
		public async Task<byte> MarkerStateFor(INetworkClient client, WorldEntity npc)
		{
			var character = client.Session.Character;
			if (npc?.Name == null || character == null)
				return None;

			return await MarkerStateFor(client, npc, await ContextFor(character));
		}

		// One context per online character, kept until that character's quest state is written or they leave. The
		// client polls once per NPC it loads and waits for each answer, so a context built per poll costs ~95 ms x 52
		// polls on a teleport into Tortage (4020): fourteen seconds of serialized backlog, which the player feels as a
		// lag spike after the loading screen. Every writer of quest state forgets the entry (QuestProgressionService,
		// the reward pick, the dialogue completion, the DeleteQuest echo) and the disconnect path drops it.
		//
		// The TTL is a bound, not the mechanism: a writer this list does not know about would otherwise serve stale
		// markers for ever. Ten seconds covers the whole poll storm and limits an unknown writer's damage to that
		// window; the explicit Forget calls are what keep a marker current the instant a quest moves.
		private readonly ConcurrentDictionary<uint, (QuestMarkerContext Context, long BuiltAt)> _contexts = new ConcurrentDictionary<uint, (QuestMarkerContext, long)>();
		private static readonly long ContextTtlTicks = Stopwatch.Frequency * 10;

		public async Task<QuestMarkerContext> ContextFor(Character character)
		{
			if (_contexts.TryGetValue(character.Id, out var cached) && Stopwatch.GetTimestamp() - cached.BuiltAt < ContextTtlTicks)
				return cached.Context;

			var context = await QuestMarkerContext.Load(_quests, _world, character);
			_contexts[character.Id] = (context, Stopwatch.GetTimestamp());
			return context;
		}

		// The character's quest state moved (or they left): the next marker rebuilds from the rows.
		public void Forget(uint characterId)
		{
			_contexts.TryRemove(characterId, out _);
		}

		public async Task<byte> MarkerStateFor(INetworkClient client, WorldEntity npc, QuestMarkerContext context)
		{
			var character = client.Session.Character;
			var npcName = npc?.Name;
			if (npc == null || npcName == null || character == null)
				return None;

			// '?' (0x04) when the player has an active quest whose current goal is to talk to this NPC - it is the next
			// step (e.g. the Casilda chain's talk-to-free stage 13480, active after looting Tuut's key). This is a
			// separate source from the giver logic below, which cannot answer it: the giver quest (13479) is already
			// Completed by then. Retail sends 0x2008 state 0x04 to her handle at the loot->talk transition
			// (captive-quest capture, Blablatest fr1455).
			//
			// This runs once per 0x2020 spawn poll and the client polls once per NPC it is loading - ~77 of them on the
			// Tortage beach - so it must stay one batched read rather than a query per active quest. Order is
			// preserved, so the first match wins. The giver logic below needs the raw rows in every state (held /
			// completed), so that stays a plain read.
			foreach (var (_, qDef) in context.Active)
			{
				// Template first, name second - the same test the talk trigger uses, so the marker and what fires on
				// the greeting cannot disagree.
				var qGoal = QuestProgress.InPlayOrder(qDef.Goals).FirstOrDefault();
				if (qGoal?.GoalType == QuestGoalType.TalkToNpc && QuestProgress.TargetsNpc(qGoal, npc.NpcTemplateId, npcName))
					return TurnIn;
			}

			if (!(await Givers()).TryGetValue(npc.NpcTemplateId, out var questIds))
				return await ChainRunsThrough(context, npc)
					? Unavailable
					: None;

			// The giver's quests' line / level / step, in one projection - the fold below reads nothing else off them.
			var giverQuests = await context.Memberships(questIds);

			// The marker describes the next quest this NPC can grant, and a giver is not one quest - Turach hands out
			// two, Laranga two, Cramaleico two. So the answer is a fold over all of them:
			//
			//   * one of theirs is held with its objective done   -> '?' (0x04), turn it in here;
			//   * some grant is still outstanding (neither held nor completed):
			//       - its line has reached it -> gold '!' (0x02), it is theirs to take;
			//       - it has not             -> silver '!' (0x01), "there is something here you cannot take yet";
			//   * nothing outstanding at all                      -> no marker. The giver is spent.
			//
			// That last clause is what a carried quest hits, and it is the difference between Casilda and Alyssa.
			// Casilda grants exactly one quest, so the instant it is accepted she has nothing left to offer and goes
			// blank - `Blablatest_mage-necromancer-create_casilda_quest_first_stage` and six other captures record the
			// same 0x02 -> 0x00 -> 0x04 -> 0x00 walk on her, and across all 275 captures she is 0x00 x178 / 0x02 x170 /
			// 0x04 x21 and never 0x01. Alyssa sits on a four-step line ending in "Report back to Alyssa" and goes
			// silver eight frames after granting hers (Hoxxling_..._login_gameplay2, grant frame 9603 -> 0x01 frame
			// 9611). Both are carrying the giver's quest; the marker differs because what is left to grant differs.
			//
			// Silver is therefore neither "your level is too low" nor simply "the chain has not reached them". It is
			// "they have one you cannot take yet", and the captures do not separate the two reasons: Turach and Laranga
			// stay silver even past their last harvested grant, which fits an unharvested further quest but is not
			// proven.
			var outstanding = false;
			var offerable = false;
			foreach (var questId in questIds)
			{
				// There is deliberately no map gate here. `QuestDefinition.MapId` is the objective's marker pin - where
				// the quest sends the player - not where its giver stands, so gating on it suppresses markers retail
				// demonstrably sends: it blocks 15 of the 28 giver quests in the shipped data, among them Turach and
				// Laranga (quests on 4010, they stand on 4020), Alyssa (10800 vs 4020), Caor, Oracle Vateesa and
				// Hadrathus. Keeping a same-named NPC elsewhere in the world from wearing the marker ("Halen van Dummy"
				// is the internal name of 27 unrelated templates) is the template-keyed lookup above, not this.
				// docs/subsystems/quests.md states the same rule: MapId is a marker pin, not a match key.
				var giverQuest = giverQuests[questId];
				var held = context.Held(questId);
				// '?' once the objective is done but the quest is still active (awaiting turn-in at the giver - a quest
				// is only marked Completed on turn-in, not on the kill). It outranks the rest: a giver the player can
				// hand something to says so before it advertises what it has left.
				if (held is { State: QuestState.Active, GoalRequired: > 0 } && held.GoalProgress >= held.GoalRequired)
					return TurnIn;

				// Carried, or already finished: this one is not on offer. Retail is explicit about the finished half in
				// xxx_mage-necromancer_login_play3 - Tina carries '?' at t=59.3 and 69.6, quest 10287 completes at
				// t=101.7, and she is set to 0x00 at t=102.0, three packets, the instant it finishes.
				if (held != null)
					continue;

				outstanding = true;
				offerable |= giverQuest == null || await IsLineReady(giverQuest, context);
			}

			// A giver with nothing left to offer can still be silver for a different chain that runs through it -
			// Turach is dark on line 241 (the one he is granting) and silver for 222 and 1305, which pass through him
			// and the player never began. So the non-giver rule applies here too, not only to NPCs the giver map has
			// never heard of.
			if (!outstanding)
				return await ChainRunsThrough(context, npc) ? Unavailable : None;

			return offerable ? Available : Unavailable;
		}

		/// <summary>
		///     Builds the two catalog maps at startup, logging their cost: they are the first thing the entry's per-NPC
		///     quest markers reach for, so on a cold server they otherwise dominate that step.
		/// </summary>
		public async Task WarmAsync()
		{
			await Givers();
			await Targets();
		}

		private async Task<Dictionary<uint, uint[]>> Givers()
		{
			if (_questGivers != null)
				return _questGivers;

			var clock = System.Diagnostics.Stopwatch.StartNew();
			_questGivers = await _npcs.GetQuestGiverMap();
			_logger.LogInformation("QuestMarkerResolver: quest-giver map ({Count} templates) built in {Ms} ms.", _questGivers.Count, clock.ElapsedMilliseconds);
			return _questGivers;
		}

		private async Task<Dictionary<uint, uint[]>> Targets()
		{
			if (_goalTargets != null)
				return _goalTargets;

			var clock = System.Diagnostics.Stopwatch.StartNew();
			_goalTargets = await _npcs.GetQuestGoalTargetMap();
			_logger.LogInformation("QuestMarkerResolver: goal-target map ({Count} templates) built in {Ms} ms.", _goalTargets.Count, clock.ElapsedMilliseconds);
			return _goalTargets;
		}

		/// <summary>
		///     The silver rule for an NPC this player has nothing to take from: does a quest chain they have not begun
		///     run through it, at a quest level retail would light for them?
		/// </summary>
		/// <remarks>
		///     Two measured conditions (<c>--report quest-markers</c>, 34,837 marker packets):
		///     <list type="number">
		///         <item>
		///             The relation is the quest line, and it is necessary: of the 1,559 packets about an NPC whose
		///             every line the player had already started, zero carried silver. Silver means "a chain you have
		///             not begun runs through here" - runs through, not "is granted by": 38 of the 62 templates retail
		///             ever lit grant nothing at all, and 85% of silver is on an NPC some untouched quest's goal will
		///             send the player to. That relation is <c>GetQuestGoalTargetMap</c>.
		///         </item>
		///         <item>
		///             The second gate is level, and it is two-sided: every lit observation in the corpus sits at a
		///             delta of -2..+1 (player level minus quest level), and every delta outside that band is
		///             unanimously dark - 0/243 far above, 0/164 far below.
		///         </item>
		///     </list>
		///     The band used here is the observed lit range and deliberately not one unit wider: both real cutoffs are
		///     only bracketed (<c>-7 &lt; T &lt;= -2</c> above, <c>1 &lt; T &lt;= 56</c> below) and the gaps hold no
		///     observation, so it errs by under-lighting inside a gap rather than by sending a marker retail would not.
		///     <para />
		///     The pair is not sufficient: inside the band and on an untouched line retail lights 299 of 382, so a
		///     third gate exists and this over-lights about a fifth of the time. Open work tracked in Gitea #857.
		/// </remarks>
		private async Task<bool> ChainRunsThrough(QuestMarkerContext context, WorldEntity npc)
		{
			if (!(await Targets()).TryGetValue(npc.NpcTemplateId, out var targetingQuestIds))
				return false;

			// "Begun" is per line and counts both states: a completed quest began its line just as much as a held one.
			// Reading only the active set would call a finished line untouched and light a chain the player has been
			// all the way through, which is the single biggest way this rule can go wrong.
			//
			// The held set's memberships are already in the context; only this NPC's targeting ids can be new, and they
			// come back in one projection rather than a definition load per quest.
			var memberships = await context.Memberships(context.Quests.Select(q => q.QuestId).Concat(targetingQuestIds));
			var startedLines = new HashSet<uint>();
			foreach (var held in context.Quests)
			{
				if (memberships.GetValueOrDefault(held.QuestId) is { } m)
					startedLines.Add(m.QuestLineId);
			}

			return LightsSilverByMembership(targetingQuestIds.Select(memberships.GetValueOrDefault), startedLines, context.Character.Level);
		}

		/// <summary>
		///     The silver decision itself, pure over its inputs: do any of the quests running through this NPC belong
		///     to a line the player has not begun, at a level inside the observed lit band?
		/// </summary>
		/// <remarks>
		///     Kept separate from <see cref="ChainRunsThrough" /> so <c>--report quest-markers</c> can score the
		///     shipping rule against the corpus rather than a second copy of it - the same reason
		///     <see cref="LineReached" /> is public. A rule measured in one implementation and shipped in another is a
		///     rule with no measurement.
		/// </remarks>
		public static bool LightsSilver(IEnumerable<QuestDefinition?> throughQuests, ISet<uint> startedLines, uint playerLevel, bool applyLevelBand = true)
		{
			return LightsSilverByMembership(throughQuests.Select(d => d == null ? null : new QuestLineMembership(d.QuestId, d.QuestLineId, d.Level, d.JournalSequence)), startedLines, playerLevel, applyLevelBand);
		}

		// The lean form the live resolver runs on; the QuestDefinition overload above is the report's and the tests'
		// entry point and delegates here, so there is exactly one rule.
		public static bool LightsSilverByMembership(IEnumerable<QuestLineMembership?> throughQuests, ISet<uint> startedLines, uint playerLevel, bool applyLevelBand = true)
		{
			foreach (var def in throughQuests)
			{
				if (def == null || startedLines.Contains(def.QuestLineId))
					continue;

				// `applyLevelBand: false` is the line clause on its own, for `--report quest-markers` to score the two
				// gates apart. It is not a second model: only 50 of the corpus's 456 silver packets carry a viewer
				// level, so scoring them together measures the line rule on 11% of its evidence.
				if (!applyLevelBand)
					return true;

				// A quest of level 0 states no level, so the band cannot be applied to it and a guess would become the
				// rule. Left dark: an unstated level is not evidence of a lit one.
				if (def.Level == 0)
					continue;

				var delta = (int)playerLevel - (int)def.Level;
				if (delta >= LitBandBelowPlayer && delta <= LitBandAbovePlayer)
					return true;
			}

			return false;
		}

		// The quest item the killer needs off `npcName`'s corpse - the item id of their active LootFromNpc goal
		// targeting it, or null when they have no such goal. A non-null result forces the bag drop (the item must be
		// lootable, not a 50/50 roll) and names what to put in it. Both goals in the game data want an actual key:
		// Saddur -> 193186 "Acheronian Gate Key", Scavenger Tuut -> 319224 "Scavenger's Key", each item type 113
		// (Quest).
		public async Task<uint?> ActiveLootGoalItemFor(uint characterId, string npcName, uint npcTemplateId = 0)
		{
			if (string.IsNullOrEmpty(npcName) && npcTemplateId == 0)
				return null;

			// One batched read rather than a query per active quest - this sits on the loot path and runs on every kill
			// that could drop a bag.
			foreach (var (quest, def) in await _world.GetCharacterQuestsWithDefinitions(characterId, QuestState.Active))
			{
				// Template first, name second - the same test MatchingLootGoal applies when the item is actually taken.
				// The two must agree, or the drop is forced for a goal the take then refuses to advance.
				var goal = QuestProgress.InPlayOrder(def.Goals).FirstOrDefault();
				if (goal is not { GoalType: QuestGoalType.LootFromNpc } || !QuestProgress.TargetsNpc(goal, npcTemplateId, npcName))
					continue;

				// A goal that names no item cannot be satisfied: the objective is receiving the item, and there is none
				// to receive or to match a take against (QuestProgress.MatchingLootGoal). Forcing a bag drop for it
				// yields an empty bag that looks lootable and advances nothing. Every LootFromNpc goal in the data
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

			var quests = await _quests.GetCharacterQuests(characterId.Value);
			entity!.Knowledge.ActiveQuestIds.Clear();
			entity.Knowledge.CompletedQuestIds.Clear();
			foreach (var quest in quests)
			{
				if (quest.State == QuestState.Active)
					entity.Knowledge.ActiveQuestIds.Add(quest.QuestId);
				else if (quest.State == QuestState.Completed)
					entity.Knowledge.CompletedQuestIds.Add(quest.QuestId); // drives "hide once done" NPC gating
			}
		}

		// Whether `quest`'s line has actually reached it - every earlier step of the same quest line completed. This is
		// what separates the gold '!' from the silver one for a quest the player does not hold: retail greys a giver
		// whose quest exists but is not yet reachable, rather than drawing no marker.
		//
		// JournalSequence 0 is a legitimate captured value, not "unordered" - 74 captured quests carry it and 182 lines
		// have it on every step (see GetQuestDefinitionsByLine). A line ordered entirely by 0s cannot say what comes
		// first, so a 0 on either side is skipped rather than guessed at and an unorderable line stays gold. Only a
		// genuine lower sequence gates.
		private static async Task<bool> IsLineReady(QuestLineMembership quest, QuestMarkerContext context)
		{
			return quest.JournalSequence == 0 || LineReached(quest.JournalSequence, await context.LineSteps(quest.QuestLineId), context.Quests);
		}

		// The rule itself, split from the fetch so it can be tested against the real quest lines without a database.
		public static bool LineReached(QuestDefinition quest, IEnumerable<QuestDefinition> line, CharacterQuest[] quests)
		{
			return LineReached(quest.JournalSequence, line, quests);
		}

		public static bool LineReached(uint questSequence, IEnumerable<QuestDefinition> line, CharacterQuest[] quests)
		{
			foreach (var step in line)
			{
				if (step.JournalSequence == 0 || step.JournalSequence >= questSequence)
					continue;

				if (Array.Find(quests, q => q.QuestId == step.QuestId) is not { State: QuestState.Completed })
					return false;
			}

			return true;
		}
	}
}
