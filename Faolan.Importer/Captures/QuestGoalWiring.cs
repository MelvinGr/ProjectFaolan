using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Decides what each imported quest goal is - its trigger, its count, its target - from the evidence the
	///     captures and the client catalogs carry. <see cref="WireQuests" /> is the whole pipeline and the only entry
	///     point; everything else is one step of it.
	/// </summary>
	/// <remarks>
	///     This is a bootstrap pass: every rule skips a goal another rule has already typed, so the order in
	///     <see cref="WireQuests" /> is the evidence ranking, strongest first. That ordering is the design - do not
	///     reorder the steps to group them by shape.
	///     Nothing here reads a capture or writes a new row from one: it re-reads rows the import steps already landed
	///     and decides what they mean.
	/// </remarks>
	public static class QuestGoalWiring
	{
		/// <remarks>
		///     The "Acheronian Gate" world object on the beach (map 4010, 523.4/754.7) - what quest 12369's goal 3492
		///     "Unlock the Acheronian gate" is solved by using. <c>WorldObject.Id</c> is the importer's derived key, so
		///     this survives a re-harvest of the world objects.
		/// </remarks>
		private const uint AcheronianGateObjectId = 975369919;

		/// <summary>
		///     Overlays the hand-authored quest wiring onto the harvested definitions - the fields captures do not
		///     carry: the server trigger (GoalType, TargetName and QuestItemId via <see cref="SeedGoal" />), the chain
		///     successor (NextQuestId via <see cref="Chain" />), and the byte-exact objective-region and snapshot
		///     detail the beach and Casilda quests need.
		/// </summary>
		/// <remarks>
		///     Runs as its own importer module (QuestWiringModule) after the quests module, so it applies last: the
		///     trigger fields are preserved by the objective-goals refresh, and the region-detail seeds are calibrated
		///     to match the capture. Trigger and chain seeds are bootstrap-only (see <see cref="SeedGoal" /> and
		///     <see cref="Chain" />), so a re-run never reverts a quest-editor edit.
		/// </remarks>
		public static async Task WireQuests(DatabaseContext db)
		{
			// The order is the evidence ranking, strongest first, because every step is bootstrap-only: whoever types a
			// goal first wins and the rest skip it. Getting it wrong silently produces wrong triggers - a marker rule
			// ahead of the goal's own sentence turns talks and escorts into walk-ups, and a sentence rule ahead of a
			// hand-written override defeats the override's whole purpose.
			//
			//   1. a human decided        - the Seed* steps, including every editorial call (GatesTargetVisibility,
			//                               the Nadini stand-in) and everything no source states (QuestItemId, chains)
			//   2. the goal's own verb    - its plaintext objective sentence, then the wire kind tag
			//   3. its typed reference    - "the trailing ref is an item, so this is a gather"
			//   4. its map marker         - the weakest: a pin sits on whatever the objective is about, which is the
			//                               NPC to talk to as readily as the place to walk to
			await SeedBeachQuestGoals(db);
			await SeedShipwreckedCityChain(db);
			await SeedCasildaChain(db);
			await SeedCasildaQuestSnapshot(db);
			await TypeItemGoalsFromTrailingCategory(db); // first of the rules, and the only one that retypes a row
			await TypeTalkGoalsFromCatalogName(db);
			await TypeHandInGoalsFromCatalogName(db);
			// Orthogonal to the typing rules - it fills a count, not a trigger - so it is not IsWired-guarded.
			await SetGoalCountsFromCatalogName(db);
			await TypeKillGoalsFromCatalogName(db);
			await TypeKillGoalsFromKind(db);
			await TypeInvestigateGoalsAsReach(db);
			await TypeUseGoalsFromKind(db);
			await TypeLocationGoalsFromTrailingCategory(db);
			await DeriveQuestIndexFromLine(db);
			await SeedQuestWorldEffects(db);
			// Last, and deliberately so: it resolves whatever `TargetName` the steps above settled on, so it has to see
			// their final answer. Orthogonal to all of them - it fills an id for a target already chosen, never a
			// trigger - so it is not IsWired-guarded.
			await ResolveGoalTargetTemplates(db);
		}

		/// <summary>
		///     The beach starter (quest 10238): the Quest_c scalars the harvest does not carry, plus its two triggers.
		/// </summary>
		/// <remarks>
		///     The per-goal objective record is not seeded here: the quests harvest reproduces every field of it
		///     exactly, LDB keys included, and recreates the goal rows from nothing if they are deleted. The single
		///     exception is goal 1448's TrailingLocalized, which is set below.
		///     The Quest_c scalars do have to be seeded. The harvest carries only Level and IndexMagic of them, and
		///     dropping the rest takes HasSnapshotRecord with it, which falls the quest out of the login snapshot.
		///     Measure that with the seed absent: the default importer plan includes quest-wiring, so a measurement
		///     that runs it is measuring the seed restoring its own values.
		/// </remarks>
		private static async Task SeedBeachQuestGoals(DatabaseContext db)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == 10238);
			if (quest == null)
				return;

			quest.BandLevel = 4;
			quest.QuestLineFlags = QuestFullUpdates.LiveQuestLineFlags; // the beach record's `n` (@+0x14)
			quest.FieldEc = 1;
			quest.FieldF0 = 0x96;
			quest.FieldF8 = 15;
			quest.HasSnapshotRecord = true;
			quest.IndexMarker = 0x7E2;
			quest.IndexTrailerWord = 0x000156E7;
			// The magic, the (sequence, remaining) pair and the line catalog are DERIVED from QuestLines +
			// JournalSequence by DeriveQuestIndexFromLine. tf1/tf2 are stamped non-zero here so that step knows
			// this quest's index is one the snapshot builds from; it overwrites both with the derived pair.
			quest.IndexTableField1 = 1;
			quest.IndexTableField2 = 1;

			// The one @609 field the harvest disagrees with: the capture set this quest carries writes goal 1448's
			// trailing text as `<remoteformat>` (the FieldF0 == 150 spelling), not the `<localized>` one - see
			// QuestDefinition.FieldF0, which selects between them.
			if (quest.Goals.FirstOrDefault(g => g.GoalId == 1448) is { } jungle)
				jungle.TrailingLocalized = false;

			await db.SaveChangesAsync();

			// 10238 is "Shipwrecked / I. The Slaver"; its completing goal 3490 is "Kill slavemaster Saddur", proven by
			// the Hoxxling gameplay capture (Saddur dies fr11942 -> 10238 completes -> 12369 -> 12373). The kill verb
			// types the goal, so what is seeded is the editorial half: `GatesTargetVisibility` (a call the client's data
			// does not state anywhere - see that column) and `TargetName`, because the objective says "slavemaster
			// Saddur" while the live NPC is "Saddur".
			//
			// Goal 1448 "Enter the jungle" is a reach at its own marker. Its reference is a 51200 bounded area rather
			// than a location, so TypeLocationGoalsFromTrailingCategory does not see it, and a `RegionScoped` goal is
			// not a reach by kind alone (goal 11712 has RequiredCount 3 - the region scopes the objective). Its verb is
			// what settles it, so it stays a seed until the "Enter <place>" shape is a rule.
			await SeedGoal(db, 10238, 1448, QuestGoalType.ReachArea);
			await SeedGoal(db, 10238, 3490, QuestGoalType.Kill, "Saddur", gatesVisibility: true, staysDead: true);
			await Chain(db, 10238, 12369);
		}

		/// <summary>
		///     The Shipwrecked destiny chain past stage I. The beach dump proves the supersession 10238 -> 12369 ->
		///     12373 (stages I "The Slaver", II "The Acheronian Gate", III "The City of Tortage").
		/// </summary>
		/// <remarks>
		///     Stage III's goal 3498 is a reach at the city marker (4020, 913/718), so entering the city completes it.
		///     Stage III is chain-final: the later stages have no imported goal targets yet. Markers and types here are
		///     best-effort rather than byte-exact. Idempotent.
		///     Open work tracked in Gitea #358.
		/// </remarks>
		private static async Task SeedShipwreckedCityChain(DatabaseContext db)
		{
			// II "The Acheronian Gate" -> III "The City of Tortage". Its key objective is goal 7134 "Find the gate
			// key": looting the Acheronian Gate Key (item 193186) from Saddur's corpse bag, the same kill-then-loot
			// pattern as Casilda's Tuut key (Hoxxling_mage-heraldofxotli_login_gameplay: Saddur dies at fr11942, his
			// corpse becomes a loot bag at fr11947, the key reaches the 0x71 quest container at fr12023).
			//
			// The two goals are easy to swap, and the marker cannot tell them apart: 3492's 524/755 pin sits ~3u from
			// both Saddur's spawn (521/757) and the world object "Acheronian Gate" (975369919, at 523.4/754.7). The
			// goal names settle it - 3492 is "Unlock the Acheronian gate" and 7134 is "Find the gate key". 7134's own
			// marker (574/944) lands on the "Acheronian Chest" (2018094960), the key's other end.
			//
			// Their order is set in the RDB catalog importer (ResequenceGoalsByPhase): the client's phase says 7134 is
			// played first, and the corpus agrees - 7134 takes the `Advanced` hand-off, which only a non-last goal
			// gets.
			//
			// So 3492 is a use of the Acheronian Gate itself while holding the key, not a reach: the gate object
			// stands on the goal's marker, so a reach completes the moment the player walks up to it, before the key
			// is looted and without the gate being touched. Retail's order in the same dump is unambiguous - fr12193
			// the player selects the gate dynel, fr12196 sends the use, fr12204 goal 3492 ticks 1/1, fr12205 the quest
			// completes. The key rides along as the goal's QuestItemId: `UseWorldObject` requires it to be held and,
			// unlike a delivery, does not take it.
			//
			// It stays a seed while its sibling 3498 "Enter the City of Tortage" is typed by a rule: 3498 carries a
			// TrailingCategory 100 location reference, so TypeLocationGoalsFromTrailingCategory places it from the
			// marker, while 3492 carries no typed reference at all. Nothing but the verb "Unlock" can place it, and
			// one goal is not a shape.
			await Chain(db, 12369, 12373);
			await CorrectAcheronianGateGoals(db);
			await SeedGoal(db, 12369, 7134, QuestGoalType.LootFromNpc, "Saddur", 193186, 1);
			await SeedGoal(db, 12369, 3492, QuestGoalType.UseWorldObject, questItemId: 193186, worldObjectId: AcheronianGateObjectId);

			// V "The Quarry" (12371) is granted by Turach's dialogue (ConversationNodes.GrantQuestId 12371), so it is
			// not chained here. Goal 3495 "get 3 stone bricks" is a CollectItem of item 193400 (its TrailingRefId),
			// picked up from the "Stone Brick" QuestItemEffect dynels at the quarry (4020); goal 3497 "deliver 3 to
			// Turach" is a talk at his spawn (824/722, which is the marker), keeping the captured count of 3 so the
			// objective region stays byte-exact while the talk solves it. Decoded from
			// Hoxxling_mage-heraldofxotli_login_gameplay2: pickups at fr4310/4367/4447, delivery at fr5102.
			await SeedGoal(db, 12371, 3497, QuestGoalType.TalkToNpc, "Turach");

			// The city talk stages are progressed by each NPC's conversation GrantQuestId, already imported, so only
			// the goal target is needed and no chain. Confirmed in Hoxxling_mage-heraldofxotli_login_gameplay2 by the
			// talk-then-complete pairs: Laranga (guard, 0x4FDA) at fr5680 completes 10286 at fr5829; Tina (0x4FF0) at
			// fr9214 completes 10287 at fr9738. 10320 "talk Turach" is the pre-quarry "The Blacksmith" stage - granted
			// by the entry guard Laranga, completed by talking to Turach, whose dialogue then grants the quarry 12371.
			await SeedGoal(db, 10286, 399, QuestGoalType.TalkToNpc, "Laranga"); // VI The Guard - talk the city guard
			await SeedGoal(db, 10287, 400, QuestGoalType.TalkToNpc, "Tina"); // VII The Barmaid - talk Tina

			// VIII "Nadini" (10915, granted by Tina): the objective is "Talk to Nadini", but Nadini 806 has no
			// conversation imported, so her greeting cannot fire the talk trigger (PickConversationScreen returns
			// null). Her spawn (1026/670 on map 4022, the night city) exactly matches the goal marker, so it is wired
			// as a reach until her dialogue is imported. Chain end: "The Awakening I" is a separate destiny quest, and
			// Nadini cannot grant it without dialogue.
			await SeedGoal(db, 10915, 1352, QuestGoalType.ReachArea);
		}

		/// <summary>
		///     Resets quest 12369's goal 3492 when it carries one of the two mistyped shapes a seed can leave behind,
		///     so <see cref="SeedShipwreckedCityChain" /> can type it properly.
		/// </summary>
		/// <remarks>
		///     It has to run before <see cref="SeedShipwreckedCityChain" />'s bootstrap guard: a wired goal is skipped
		///     by <c>IsWired</c> forever, so a mistyping in a shipped database would outlive the fix.
		///     Narrow on purpose - only the exact rows a seed could have produced, so a quest-editor edit or a hand
		///     seed carrying anything else is left alone. It also clears 7134's Partial flag: the goal now carries its
		///     kind tag, marker, count and trigger, so it is a whole goal and <c>AllGoalsComplete</c> must stop
		///     skipping it.
		/// </remarks>
		private static async Task CorrectAcheronianGateGoals(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.QuestDefinitionId == 12369).ToListAsync();
			var unlock = goals.Find(g => g.GoalId == 3492);
			if (unlock is { GoalType: QuestGoalType.LootFromNpc, TargetName: "Saddur", QuestItemId: 193186 })
			{
				unlock.GoalType = QuestGoalType.None; // the shape IsWired reads as "not wired"
				unlock.TargetName = null;
				unlock.QuestItemId = null;
				Console.WriteLine("Corrected quest 12369: goal 3492 'Unlock the Acheronian gate' was carrying goal 7134's key loot (#527).");
			}

			// The other shape: a bare ReachArea with no target of any kind, which the gate object standing on the
			// goal's marker completes on proximity - before the key is looted and without the gate being touched.
			// Narrow in the same way, so a hand edit is left alone.
			if (unlock is { GoalType: QuestGoalType.ReachArea, TargetName: null, QuestItemId: null, TargetWorldObjectId: null })
			{
				unlock.GoalType = QuestGoalType.None; // hand it back to the seed below, which types it properly
				Console.WriteLine("Corrected quest 12369: goal 3492 'Unlock the Acheronian gate' was a ReachArea that completed on proximity (#527).");
			}

			var findKey = goals.Find(g => g.GoalId == 7134);
			if (findKey is { Partial: true })
				findKey.Partial = false;

			await db.SaveChangesAsync();
		}

		/// <summary>
		///     The Tortage intro quest chain: 13479 is a four-stage supersession chain. Idempotent.
		/// </summary>
		/// <remarks>
		///     None of this is harvestable. NextQuestId is not a field on the wire - a supersession is visible in a
		///     capture only as a sequence of events - and neither is goal 3544's escortee template id.
		/// </remarks>
		private static async Task SeedCasildaChain(DatabaseContext db)
		{
			await Chain(db, 13479, 13563);
			// gatesVisibility: Tuut is a one-time story target - hidden until 13479 is active, and he stays dead
			// (retail solo-instances him). The only kill goal besides Saddur that gets it; see GatesTargetVisibility.
			await SeedGoal(db, 13479, 5744, QuestGoalType.Kill, "Scavenger Tuut", required: 1, gatesVisibility: true, staysDead: true);
			// The loot stage's key item: Scavenger Tuut's bag drops 319224 "Scavenger's Key", so the player sees and
			// takes the actual key rather than an empty source-matched bag. Which corpse holds which quest item is in
			// neither the wire goal record nor the RDB catalog, so this cannot be harvested.
			await Chain(db, 13563, 13480);
			await SeedGoal(db, 13563, 5867, QuestGoalType.LootFromNpc, "Scavenger Tuut", 319224, 1);
			// "Free Casilda" - a talk objective whose verb no talk shape recognises.
			await Chain(db, 13480, 12407);
			await SeedGoal(db, 13480, 5745, QuestGoalType.TalkToNpc, "Casilda", required: 1);
			await Chain(db, 12407, null); // escort is the chain end
			await SeedGoal(db, 12407, 3544, QuestGoalType.Escort, "Casilda", required: 1);

			// The escortee's template id (Casilda 9411) as the Escort goal's TrailingRefId, so the runtime follow
			// trigger resolves her by id rather than name. Set authoritatively every run rather than bootstrap-only:
			// the captured escort objective record carries no trailing ref and this module runs after the
			// objective-goals refresh, so it has to win. Byte-safe, since 12407's objective region is not in the
			// verified set.
			var escortGoal = await db.QuestGoalDefinitions.FirstOrDefaultAsync(g => g.QuestDefinitionId == 12407 && g.GoalId == 3544);
			if (escortGoal != null)
			{
				escortGoal.TrailingRefId = 9411;
				await db.SaveChangesAsync();
			}
		}

		/// <summary>
		///     Casilda's quest 13479 Quest_c scalars: the journal-record fields that live only in the FullChar snapshot
		///     and are not in the harvest. Idempotent - set every run.
		/// </summary>
		/// <remarks>
		///     Its per-goal objective record needs no seed: the harvest reproduces goal 5744's whole record field for
		///     field, and Level, BandLevel, FieldEc, HasSnapshotRecord and QuestLineFlags with it. Only the fields
		///     below are missing.
		/// </remarks>
		private static async Task SeedCasildaQuestSnapshot(DatabaseContext db)
		{
			var quest = await db.QuestDefinitions.FirstOrDefaultAsync(q => q.QuestId == 13479);
			if (quest == null)
				return;

			quest.FieldF0 = 80;
			quest.FieldF8 = 7;
			quest.XpReward = 80;
			// Overwritten with the derived pair by DeriveQuestIndexFromLine; non-zero here so that step knows this
			// quest's index is one the snapshot builds from. See the beach seed.
			quest.IndexTableField1 = 1;
			quest.IndexTableField2 = 1;
			await db.SaveChangesAsync();
		}

		/// <summary>
		///     A goal whose trailing reference is an item is a gather objective, so it is typed CollectItem.
		/// </summary>
		/// <remarks>
		///     The goal row says so without any RDB or LDB lookup: TrailingCategory is a typed reference and 50200 is
		///     the client's own <c>item.name</c> table (docs/rdb/ldb-string-index.md), so the ref is an item id.
		///     A rule rather than a per-quest seed, and the rule is what matters: such goals otherwise sit at the
		///     fresh-harvest Kill default with no target, i.e. inert, because the kill matcher looks for an NPC whose
		///     template id equals an item id, and the two id ranges do not overlap.
		///     It does not try to distinguish how the item is obtained. The client's data does not say, and the set
		///     mixes mob drops, world pickups and bought or gathered consumables. It does not need to: a gather
		///     objective is satisfied by holding the item, so the runtime ticks these on the item reaching the
		///     inventory from any source (QuestProgressionService.AdvanceCollectGoals, off the single grant choke
		///     point). Guessing LootFromNpc versus CollectItem per quest would buy nothing, and a wrong guess makes the
		///     quest silently un-completable.
		///     Bootstrap-only like every other wiring step: an already-wired goal (anything but the bare Kill default)
		///     is left exactly as it is, so a quest-editor edit or a hand seed survives a re-import.
		/// </remarks>
		private static async Task TypeItemGoalsFromTrailingCategory(DatabaseContext db)
		{
			const uint itemNameCategory = 50200; // LDB `item.name`

			var goals = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == itemNameCategory && g.TrailingRefId > 0)
				.ToListAsync();

			int typed = 0, untyped = 0;
			foreach (var goal in goals)
			{
				// Corrective, and it has to run before the bootstrap guard: a goal typed CollectItem counts as wired,
				// so the guard below would skip it forever and a mistyping in a shipped database would outlive this
				// rule. Reset to `None` - no trigger - so the quest is visibly stuck rather than silently completing
				// on a pickup. `None` and not some other verb: any real trigger is a different wrong one, and only
				// safe while no NPC template id collides with an item id.
				//
				// Narrow on purpose: only a row this rule itself could have produced (CollectItem with no TargetName
				// and no QuestItemId). A hand-seeded or Quest-editor row carries one of those and is left alone.
				if (UntypedByArrival.Contains(goal.Separator ?? 0) && goal is { GoalType: QuestGoalType.CollectItem, TargetName: null, QuestItemId: null })
				{
					goal.GoalType = QuestGoalType.None;
					untyped++;
					continue;
				}

				if (IsWired(goal))
					continue;

				// Not every item goal is a collect: the goal's own `Separator` is the verb (see QuestGoalKind), and for
				// five of them obtaining the item is not doing the objective. Typing one of those as a collect
				// completes the quest the moment the item reaches the backpack, skipping the objective entirely - a
				// silent chain-progression bug, where leaving it unwired is at least visibly stuck.
				//
				//   Deliver (20)       hands the item over - obtaining it is the start of the objective, not the end
				//   Craft (5)          "Make Visionwine" - a bought Visionwine is not a made one
				//   Use (10)           "Use the Vial to Capture Gas" - the vial (4349109) is in the catalog, so a
				//                      collect here completes the objective on picking the vial up
				//   DestroyObject (25) "Destroy Bowl of Set" - destroying an object does not put it in the bag, so
				//                      this can only ever be wrong, in whichever direction
				//   Investigate (24)   "Investigate the Ancient Gateway" - typed ReachArea instead, below
				if (UntypedByArrival.Contains(goal.Separator ?? 0))
					continue;

				goal.GoalType = QuestGoalType.CollectItem;
				typed++;
			}

			if (typed > 0 || untyped > 0)
				await db.SaveChangesAsync();

			if (untyped > 0)
				Console.WriteLine($"Un-typed {untyped} item goal(s) whose verb is not satisfied by the item ARRIVING (craft/use/destroy/deliver/investigate) - they read as un-wired now rather than completing on a pickup (#522).");
		}

		/// <remarks>
		///     The verbs whose objective is not satisfied by the item arriving in the backpack - see
		///     <see cref="TypeItemGoalsFromTrailingCategory" />, the only place this is consulted.
		/// </remarks>
		private static readonly FrozenSet<uint> UntypedByArrival = new[]
		{
			(uint)QuestGoalKind.Deliver, (uint)QuestGoalKind.Craft, (uint)QuestGoalKind.Use, (uint)QuestGoalKind.DestroyObject, (uint)QuestGoalKind.Investigate
		}.ToFrozenSet();

		/// <summary>
		///     A goal whose own objective sentence asks the player to go and talk to someone is a TalkToNpc goal, and
		///     the sentence names who.
		/// </summary>
		/// <remarks>
		///     The sentence is QuestGoalDefinition.Name - the plaintext <c>QuestGoal.name</c> of the client's quest
		///     catalog (RDB 1000104), present on all but a handful of goals. The encrypted LDB 50304 text states the
		///     same sentence but needs a per-string TEA key only a capture yields, which is why this rule reads the
		///     catalog column instead; that also lets it run on every import rather than only under the opt-in RDB
		///     module, so it can sit ahead of the marker rule that would otherwise claim these goals.
		///     Pure talk shapes only - "Talk/Speak/Report to X", "Ask X about Y". The delivery shapes are the larger
		///     set by far and are deliberately not typed from the sentence: a delivery hands an item over, so it must
		///     be gated on the player holding it (QuestProgress.CanHandIn, which keys on the wire kind and the item the
		///     trailing reference names). A catalog-only goal has neither, so typing one TalkToNpc would complete it on
		///     a bare greeting with the goods still in the shop. They stay None until their wire record is captured.
		///     The name is reconciled against the NPC catalog exactly as the text rule does it: a TargetName is a match
		///     key compared for equality, and prose gives the short form ("Bronwith" for "Battlemaster Bronwith").
		///     Bootstrap-only. A target that is not held is recorded anyway and counted: it is the client's own
		///     statement of who the goal is about, and the goal starts working the day its map is imported.
		/// </remarks>
		private static async Task TypeTalkGoalsFromCatalogName(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Name != null && g.Name != "").ToListAsync();
			var npcNames = (await db.NpcTemplates.Where(t => t.Name != null).Select(t => t.Name!).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			// A TalkToNpc goal advances only once a greeting is served, so an NPC with no ConversationNode rows cannot
			// complete one however correctly it is wired. Counted apart from "NPC missing" because the fix differs: one
			// needs the map's spawns, the other a capture of the dialogue.
			var withDialogue = (await db.ConversationNodes.Select(n => n.NpcName).Distinct().ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

			int typed = 0, noName = 0, gated = 0, reconciled = 0, withoutNpc = 0, withoutDialogue = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal))
					continue;

				var read = QuestObjectiveText.Read(goal.Name);
				if (!read.IsTalkShape)
					continue;

				if (read.Recipient is not { } recipient)
				{
					// The sentence names a role, not a name - "Talk to the city guard", "Talk to the Red Hand
					// Sergeant". Only the goal's marker can say which NPC that is, and many goals have none.
					noName++;
					continue;
				}

				if (!npcNames.Contains(recipient))
				{
					var titled = npcNames.Where(n => n.EndsWith(' ' + recipient, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
					if (titled.Count == 1)
					{
						recipient = titled[0];
						reconciled++;
					}
				}

				if (read.RequiredLevel is { } level)
				{
					goal.RequiredLevel = level;
					gated++;
				}

				if (read.RequiredRankTitle is { } title)
				{
					goal.RequiredRankTitle = title;
					goal.RequiredRank = read.RequiredRank;
					gated++;
				}

				if (!npcNames.Contains(recipient))
					withoutNpc++;
				else if (!withDialogue.Contains(recipient))
					withoutDialogue++;

				goal.GoalType = QuestGoalType.TalkToNpc;
				goal.TargetName = recipient;
				typed++;
			}

			if (typed > 0)
				await db.SaveChangesAsync();

			Console.WriteLine($"Typed {typed} talk goal(s) from the client's own plaintext objective name ({withoutNpc} naming an NPC we do not "
			                  + $"hold yet, {withoutDialogue} held but with no captured dialogue, so inert either way); {reconciled} short name(s) "
			                  + $"reconciled to the catalog spelling, {gated} precondition(s) recorded, {noName} naming a role rather than a name (#704).");
		}

		/// <summary>
		///     A hand-in goal whose sentence names both ends - the recipient and the goods - is a gateable delivery, so
		///     it is typed and the item recorded.
		/// </summary>
		/// <remarks>
		///     This is the exception to "the delivery shapes are not typed from the sentence", and the condition is
		///     what makes it safe rather than the verb. The blanket refusal exists because a delivery must be gated on
		///     the player holding the goods (QuestProgress.CanHandIn) and a catalog-only goal has no wire kind and no
		///     item reference to gate on. That argument disappears once the item resolves: QuestItemId is what CanHandIn
		///     reads, so these goals are gated as tightly as a captured Deliver is. Goals whose item does not resolve
		///     stay None.
		///     Both ends must resolve unambiguously, and the item must be the whole noun phrase - see
		///     QuestObjectiveText.HandOverPhrase for why a substring match is not good enough.
		/// </remarks>
		private static async Task TypeHandInGoalsFromCatalogName(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Name != null && g.Name != "").ToListAsync();
			var npcNames = (await db.NpcTemplates.Where(t => t.Name != null).Select(t => t.Name!).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			// Name -> the single item that owns it. A name held by several catalog rows is dropped: `CanHandIn` tests
			// one template id, so picking either would gate on an item the player may legitimately not be holding.
			var itemsByName = (await db.Items.Where(i => i.Name != null).Select(i => new { i.Id, i.Name }).ToListAsync())
				.GroupBy(i => i.Name!, StringComparer.OrdinalIgnoreCase)
				.Where(g => g.Select(i => i.Id).Distinct().Count() == 1)
				.ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

			int typed = 0, noRecipient = 0, noItem = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal) || QuestObjectiveText.HandOverPhrase(goal.Name) is not { } phrase)
					continue;

				// A goal whose wire record already names the item is left alone - the reference is direct evidence
				// where the sentence is inferred, and `IsDelivery`'s captured branch already gates it. Explicit rather
				// than relying on run order: this rule fires before the opt-in `rdb-quest-text` module, so without the
				// guard the sentence would outrank the capture.
				//
				// The two agree wherever both exist, which is what makes the sentence trustworthy at all: over the
				// captured Deliver goals the phrase resolves half and matches the wire's item id on every one it
				// resolves, with no disagreements. The rest name something the catalog does not hold under that
				// spelling, or a name two items share - which the wire separates and this rule refuses, as it refuses
				// every ambiguous name.
				if (goal.TrailingCategory == QuestProgress.ItemNameCategory && goal.TrailingRefId != 0)
					continue;

				var read = QuestObjectiveText.Read(goal.Name);
				if (read.Recipient is not { } recipient)
				{
					noRecipient++;
					continue;
				}

				if (!npcNames.Contains(recipient))
				{
					var titled = npcNames.Where(n => n.EndsWith(' ' + recipient, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
					if (titled.Count != 1)
					{
						// The recipient is not an NPC we hold. Unlike a pure talk goal - where the name is recorded
						// anyway and the goal waits for its map - a delivery with an unresolvable end is not worth
						// typing: the gate below still needs the item, and both halves have to land for it to be safe.
						noRecipient++;
						continue;
					}

					recipient = titled[0];
				}

				// The catalog spells some objectives in the plural ("Give the Wolf Tails to Cavan" against the item
				// `Wolf Tail`). One trailing `s`, and only after the exact form has failed.
				if (!itemsByName.TryGetValue(phrase, out var itemId)
				    && !(phrase.EndsWith('s') && itemsByName.TryGetValue(phrase[..^1], out itemId)))
				{
					noItem++;
					continue;
				}

				if (read.RequiredLevel is { } level)
					goal.RequiredLevel = level;

				if (read.RequiredRankTitle is { } title)
				{
					goal.RequiredRankTitle = title;
					goal.RequiredRank = read.RequiredRank;
				}

				goal.GoalType = QuestGoalType.TalkToNpc;
				goal.TargetName = recipient;
				goal.QuestItemId = itemId;
				typed++;
			}

			if (typed > 0)
				await db.SaveChangesAsync();

			Console.WriteLine($"Typed {typed} hand-in goal(s) from the client's own plaintext objective name, each with the item it hands over so "
			                  + $"the hand-in is GATED; {noRecipient} name a recipient we cannot resolve, {noItem} a hand-over phrase that is not an "
			                  + "item we hold - both stay un-typed rather than completing on a bare greeting (#704).");
		}

		/// <summary>
		///     The amount an objective asks for, read from its own sentence ("Hand in 20 Yew", "Collect ten Frost
		///     Serpent Skins").
		/// </summary>
		/// <remarks>
		///     This is the one thing the client's quest catalog states that nothing else records. RequiredCount
		///     otherwise comes only from the wire goal record, so a goal no capture has shown sits at 0 - and the
		///     runtime reads 0 as one (<c>Math.Max(RequiredCount, 1)</c> in AllGoalsComplete and CanHandIn), so a
		///     "hand in 20" objective completes on the first one.
		///     Restricted to catalog-only goals, and that guard is what makes writing a wire field safe: RequiredCount
		///     is written straight back out in the byte-exact objective region, so overwriting a captured one would
		///     break the golden tests. A goal with Separator null was never on the wire - none rides the login snapshot
		///     and none belongs to a quest with any wire goal - so no region built here can contain one. Paired with
		///     RequiredCount == 0, which is never harvested and never hand-set, this cannot touch an established value.
		///     The sentence agrees with the wire wherever both exist, which is what makes the reading credible; the
		///     guard is what carries the safety.
		/// </remarks>
		private static async Task SetGoalCountsFromCatalogName(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Separator == null && g.RequiredCount == 0 && g.Name != null && g.Name != "").ToListAsync();

			int set = 0, plural = 0;
			foreach (var goal in goals)
			{
				if (QuestObjectiveText.StatedCount(goal.Name) is not { } count)
					continue;

				goal.RequiredCount = count;
				set++;
				if (count > 1)
					plural++;
			}

			if (set > 0)
				await db.SaveChangesAsync();

			Console.WriteLine($"Read the required amount off {set} goal(s) own objective name ({plural} asking for more than one - they completed "
			                  + "on the FIRST tick before, since an un-harvested count of 0 reads as 1) (#704).");
		}

		/// <summary>
		///     A kill goal whose sentence names a creature the catalog holds gets that name as its target.
		/// </summary>
		/// <remarks>
		///     A catalog-only kill goal has TrailingRefId 0 and no TargetName, so MatchingKillGoal can match it by
		///     neither id nor name and it is typed-and-inert at best. The sentence names the creature, and the name is
		///     what that matcher's fallback branch wants: it is compared against the live entity's name, which is what
		///     a "kill N of &lt;group&gt;" goal needs anyway, a group having no single template id.
		///     The name must resolve to exactly one NpcTemplates row, and that is the safety property rather than
		///     politeness about coverage: an unresolvable name would be stored and never match anything, which is inert
		///     dressed up as wired, while a held name is by construction the thing the player can kill. Where the wire
		///     established the target too, the sentence never contradicts it.
		///     The sentences that resolve to nothing are roles ("Kill the Chief"), titles the prose adds or drops
		///     ("Kill slavemaster Saddur" for Saddur) and the occasional editorial rename - all of which correctly
		///     yield nothing rather than a guess.
		///     GatesTargetVisibility is not set and must not be: these are population mobs, and gating one hides every
		///     same-named NPC from every player not on the quest while forcing NoRespawn. See that column.
		/// </remarks>
		private static async Task TypeKillGoalsFromCatalogName(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Name != null && g.Name != "").ToListAsync();
			// Name -> the single template that owns it. A name several templates share is dropped: `MatchingKillGoal`
			// compares the live entity's name, so a shared name is not ambiguous at match time, but it is ambiguous
			// about which creature the quest meant - three distinct "Scavenger Lackey" templates is the known case.
			var byName = (await db.NpcTemplates.Where(t => t.Name != null).Select(t => t.Name!).ToListAsync())
				.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
				.Where(g => g.Count() == 1)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

			int typed = 0, unresolved = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal) || goal.TargetName != null)
					continue;

				// A goal whose wire record names an npc_template is left alone - `RdbQuestGoalTargetImporter` fills
				// those from the reference, which is direct evidence where the sentence is inferred.
				if (goal.TrailingCategory == QuestProgress.NpcTemplateNameCategory && goal.TrailingRefId != 0)
					continue;

				var match = QuestObjectiveText.KillTargetCandidates(goal.Name)
					.Select(c => byName.TryGetValue(c, out var name) ? name : null)
					.FirstOrDefault(n => n != null);

				if (match == null)
				{
					if (QuestObjectiveText.KillObjective.IsMatch(goal.Name!))
						unresolved++;

					continue;
				}

				goal.GoalType = QuestGoalType.Kill;
				goal.TargetName = match;
				typed++;
			}

			if (typed > 0)
				await db.SaveChangesAsync();

			Console.WriteLine($"Typed {typed} kill goal(s) from the creature their own objective name states; {unresolved} name something that is "
			                  + "not one creature we hold - a role, a title the prose adds, or a map we have not imported - and stay un-typed (#704).");
		}

		/// <summary>
		///     A goal is a kill goal because the client's own verb says so (Separator = QuestGoalKind.Kill), and for no
		///     other reason.
		/// </summary>
		/// <remarks>
		///     The un-typed default is None rather than Kill, so a goal with no verb and no target stays untyped
		///     instead of inheriting the kill trigger.
		///     The goals this types split two ways, and both are typed - the verb decides, not whether the target
		///     resolves: most name an NPC template (TrailingCategory 51000), which is what MatchingKillGoal matches by
		///     id; a few name a monster category (51100 <c>categorytree.description</c>) that resolves to an editor
		///     group name matching no NPC, so they are typed and inert until that group can be expanded. Typing those
		///     anything else would misstate a goal read correctly, and leaving them None would lose the one thing the
		///     client did state.
		///     Bootstrap-only like every wiring step; the seed steps run before it (see <see cref="WireQuests" />) so a
		///     hand-seeded kill keeps the editorial fields only a seed sets.
		/// </remarks>
		private static async Task TypeKillGoalsFromKind(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Separator == (uint)QuestGoalKind.Kill).ToListAsync();

			var typed = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal))
					continue;

				goal.GoalType = QuestGoalType.Kill;
				typed++;
			}

			if (typed > 0)
			{
				await db.SaveChangesAsync();
				Console.WriteLine($"Typed {typed} goal(s) Kill from the client's own kill verb (#522).");
			}
		}

		/// <summary>
		///     An investigate goal is a place to go and look at, so it is typed as a reach - the same shape
		///     <see cref="TypeLocationGoalsFromTrailingCategory" /> types, reached from the verb rather than from a
		///     location reference.
		/// </summary>
		/// <remarks>
		///     One goal corpus-wide, "Investigate the Ancient Gateway" (8278, quest 15051), carrying a real map marker
		///     (2090 at 928/1011). Its trailing reference is an item (category 50200), which would otherwise sweep it
		///     up as a collect for an item the catalog does not hold - an objective that can never complete.
		///     This is an assumption worth stating: retail may want an interaction with the gateway rather than
		///     proximity to it. There is no world object at that marker to interact with, so a reach is the only
		///     trigger the data supports. It errs toward completing the objective by walking to the gateway, which a
		///     player cannot do without going there; the alternative is a goal that never completes at all.
		///     Marker-guarded like its sibling, and for the same load-bearing reason: a reach with nowhere to walk
		///     turns a visibly inert quest into a silently uncompletable one.
		/// </remarks>
		private static async Task TypeInvestigateGoalsAsReach(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Separator == (uint)QuestGoalKind.Investigate).ToListAsync();

			var typed = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal) && goal.GoalType != QuestGoalType.CollectItem)
					continue; // hand-reviewed or already given a better type

				// A placeholder marker is not a marker: (50, 50) and (1, 1) each appear on several playfields in the
				// shipped data, so typing a reach off one produces a goal solved by standing in the corner of the map.
				// `HasReachablePin` covers the markerless case as well.
				if (!QuestProgress.HasReachablePin(goal))
					continue;

				goal.GoalType = QuestGoalType.ReachArea;
				typed++;
			}

			if (typed > 0)
			{
				await db.SaveChangesAsync();
				Console.WriteLine($"Typed {typed} investigate goal(s) ReachArea from their own marker (#522).");
			}
		}

		// a `Use` goal is solved by USING the item its typed reference names - the verb is the trigger, and the
		// row states everything needed for it (`TrailingCategory` 50200 = an item, `TrailingRefId` = which one).
		//
		// One goal corpus-wide: *"Use the Vial to Capture Gas"* (11252, quest 17234), reference 4349109
		// `Po-Sha Potency Vial` - an item we do hold, and one that carries an on-use spell, so the whole path exists.
		//
		// It must not be typed `CollectItem`, which is what `TypeItemGoalsFromTrailingCategory` would otherwise do
		// with an item reference: the vial is HANDED to the player by the quest giver, so counting its arrival solves
		// the objective before the player has used it. `UntypedByArrival` already excludes the kind for exactly that
		// reason; this is the trigger that was missing on the other side of that exclusion.
		//
		// Guarded on the item EXISTING in our catalog, and the guard is load-bearing rather than defensive: a use
		// goal pointing at an item nobody can obtain is a silently-uncompletable objective, which is worse than a
		// visibly inert one (the same rule the marker guards next door apply).
		private static async Task TypeUseGoalsFromKind(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Separator == (uint)QuestGoalKind.Use).ToListAsync();

			var typed = 0;
			var noItem = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal) && goal.GoalType != QuestGoalType.CollectItem)
					continue; // hand-reviewed or already given a better type

				if (goal is not { TrailingRefId: > 0 } || goal.TrailingCategory != QuestProgress.ItemNameCategory)
					continue;

				if (!await db.Items.AnyAsync(i => i.Id == goal.TrailingRefId))
				{
					noItem++;
					continue;
				}

				goal.GoalType = QuestGoalType.UseItem;
				typed++;
			}

			if (typed > 0 || noItem > 0)
			{
				await db.SaveChangesAsync();
				Console.WriteLine($"Typed {typed} goal(s) UseItem from the client's own use verb; {noItem} left inert (the item is not in the catalog) (#522).");
			}
		}

		/// <summary>
		///     A goal whose trailing reference is a location is a reach objective, so it is typed ReachArea.
		/// </summary>
		/// <remarks>
		///     The sibling of <see cref="TypeItemGoalsFromTrailingCategory" />, with the same argument:
		///     TrailingCategory is a typed reference, 100 is the client's location category, and the goal already
		///     carries the map marker to reach - so this is a rule over a column, not a per-quest seed. Category 51200
		///     (bounded_areas, which RdbRegionImporter resolves into RegionId) is a different and much smaller set, and
		///     those goals already carry a RegionId.
		///     A marker is required, and that guard is load-bearing rather than defensive: most un-wired category-100
		///     goals carry no map marker at all, and a reach objective with no place to walk to can never be solved, so
		///     typing one would turn a visibly inert quest into a silently uncompletable one. Those stay inert and are
		///     counted.
		///     Bootstrap-only, like every wiring step, so the category-100 goals already typed TalkToNpc or Escort are
		///     invisible to it.
		/// </remarks>
		private static async Task TypeLocationGoalsFromTrailingCategory(DatabaseContext db)
		{
			const uint locationCategory = 100;

			var goals = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == locationCategory)
				.ToListAsync();

			var typed = 0;
			var noMarker = 0;
			var otherVerb = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal))
					continue;

				// The goal's own sentence outranks its marker, and this guard is what makes that true on a
				// from-scratch import. A marker is the weakest evidence a goal carries: "Speak to Chulainn" has a pin
				// at Chulainn's feet, and "Escort Casilda to the city" has one where Casilda is standing when the
				// escort is granted - so typing either from the marker gives a talk that closes without its
				// conversation ever playing, or an escort that closes the instant it begins.
				//
				// The sentence is read from `Name`, the plaintext `QuestGoal.name` of the client's quest catalog and
				// already a column here. The decrypted LDB 50304 text says the same thing (RdbQuestTextImporter) but
				// needs a per-string key and an RdbDir, and this module deliberately runs without one.
				if (QuestObjectiveText.NeedsMoreThanReachingItsMarker(goal.Name))
				{
					otherVerb++;
					continue;
				}

				// The marker columns are nullable and null is the common case here, so this must test for a value
				// rather than for zero: `goal.MapId == 0` on a `uint?` is false when the column is null, which lets
				// markerless goals through - the silently uncompletable quest this guard exists to prevent.
				//
				// A placeholder marker is not a marker either: (50, 50) and (1, 1) each appear on several playfields in
				// the shipped data, so a reach typed off one is solved by standing in the corner of the map. Both cases
				// are `QuestProgress.HasReachablePin`, which the runtime trigger asks as well.
				if (!QuestProgress.HasReachablePin(goal))
				{
					noMarker++;
					continue;
				}

				goal.GoalType = QuestGoalType.ReachArea;
				typed++;
			}

			if (typed > 0)
				await db.SaveChangesAsync();

			Console.WriteLine($"Typed {typed} location goal(s) ReachArea from TrailingCategory 100"
			                  + (noMarker > 0 ? $"; {noMarker} carry no map marker and stay inert" : "")
			                  + (otherVerb > 0 ? $"; {otherVerb} state a talk/escort objective and are left for their own verb (#522)" : "") + " (#641).");
		}

		/// <summary>
		///     The quest index fields, derived from the quest's own line rather than hand-typed per quest.
		/// </summary>
		/// <remarks>
		///     QuestIndexRegions.ForQuests prefers the catalog its caller loads from the database and reads these
		///     columns only as a fallback (the generation tests, which build a region from one definition with no line
		///     loaded), so the stored values duplicate QuestLines plus JournalSequence. IndexMagic equals
		///     QuestLines.Magic on every quest that carries one, and a line's catalog reproduces exactly from its
		///     quests ordered by JournalSequence.
		///     Scoped to the quests that already carry an index: filling every quest would put a fallback on thousands
		///     that nothing reads and churn faolan.sql for it.
		/// </remarks>
		private static async Task DeriveQuestIndexFromLine(DatabaseContext db)
		{
			// A quest is in the index if any of its index columns is set: the magic comes off the harvest (the
			// QuestJournal's line header) and the marker/table pair off the snapshot seeds, and either alone is enough
			// - scoping on the magic only would drop a snapshot quest whose journal no capture carried.
			var quests = await db.QuestDefinitions.Where(q => q.IndexMagic != 0 || q.IndexMarker != 0 || q.IndexTableField1 != 0 || q.IndexTableField2 != 0).ToListAsync();
			if (quests.Count == 0)
				return;

			var lineIds = quests.Select(q => q.QuestLineId).ToHashSet();
			var lines = await db.QuestLines.Where(l => lineIds.Contains(l.QuestLineId)).ToDictionaryAsync(l => l.QuestLineId, l => l.Magic);

			// The same catalog the runtime loads (DatabaseRepository.GetQuestDefinitionsByLine): the line's steps that
			// have journal text refs, ordered by JournalSequence. The refs filter is load-bearing - the client's own
			// quest catalog contributes thousands of quests no capture ever showed, and listing those would put ids on
			// the wire that no capture supports.
			var catalog = (await db
					.QuestDefinitions.Where(q => lineIds.Contains(q.QuestLineId) && q.TextRefs.Any())
					.Select(q => new { q.QuestLineId, q.QuestId, q.JournalSequence })
					.ToListAsync())
				.GroupBy(q => q.QuestLineId)
				.ToDictionary(g => g.Key, g => g.OrderBy(q => q.JournalSequence).Select(q => q.QuestId).ToList());

			var disagreed = new List<string>();
			foreach (var quest in quests)
			{
				// A magic the line does not carry is a harvest the line table has not caught up with - say so rather
				// than overwriting the captured value with a zero.
				if (lines.TryGetValue(quest.QuestLineId, out var magic) && magic != 0)
				{
					if (quest.IndexMagic != magic)
						disagreed.Add($"quest {quest.QuestId} carries magic 0x{quest.IndexMagic:X8} but line {quest.QuestLineId} says 0x{magic:X8}");

					quest.IndexMagic = magic;
				}

				if (!catalog.TryGetValue(quest.QuestLineId, out var steps) || steps.Count == 0)
					continue;

				// tf1 is the quest's position in its line and tf2 the number of steps after it; tf3 is 0 in every
				// captured block. Only the quests the snapshot builds from carry the pair - the rest were harvested
				// with it zero, and inventing one would be a claim no capture makes.
				if (quest.IndexTableField1 != 0 || quest.IndexTableField2 != 0)
				{
					quest.IndexTableField1 = quest.JournalSequence;
					quest.IndexTableField2 = steps.Count > quest.JournalSequence
						? (uint)steps.Count - quest.JournalSequence
						: 0;
					quest.IndexTableField3 = 0;
					quest.IndexQuestLineQuestIds = string.Join(",", steps);
				}
			}

			await db.SaveChangesAsync();
			foreach (var line in disagreed)
				Console.WriteLine($"  ⚠️ quest index: {line}");

			Console.WriteLine($"Quest index derived from the line catalog for {quests.Count} quest(s) (#641).");
		}

		/// <summary>
		///     After-quest world-object effects: tags decor with the quest whose state changes its dynel look
		///     (WorldObject.EffectQuestId and EffectDynelState, applied by WorldBroadcaster.ApplyQuestWorldEffects).
		///     Idempotent.
		/// </summary>
		/// <remarks>
		///     The Tortage beach "invisible wall" is the Casilda chain decor's collision, dropped (SimpleDynel
		///     visual-change state 5) once the escort quest 12407 is active.
		/// </remarks>
		private static async Task SeedQuestWorldEffects(DatabaseContext db)
		{
			var chain = await db.WorldObjects.FirstOrDefaultAsync(o => o.Name != null && o.Name.Contains("decor_chain_hangingwoman"));
			if (chain == null)
				return;

			chain.EffectQuestId = 12407;
			chain.EffectDynelState = 5;
			await db.SaveChangesAsync();
			Console.WriteLine("Seeded after-quest world effect: beach chain-wall (decor_chain_hangingwoman) -> drop on quest 12407 (state 5).");
		}

		/// <summary>
		///     Resolves a goal's TargetName to the stable TargetTemplateId - the id half of the pair described on that
		///     column. A display name is ambiguous in the world (Saddur is two templates, "Halen van Dummy" many), so
		///     every trigger that matches a live entity wants the id and keeps the name only as a fallback.
		/// </summary>
		/// <remarks>
		///     The sources are ordered by evidence strength, and are different in kind:
		///     <list type="number">
		///         <item>
		///             The goal's own typed reference. TrailingCategory 51000 is <c>npc_template.name</c>, so
		///             TrailingRefId is the target's template id as stated by the client - direct evidence, and the
		///             only source needing no string comparison. It never contradicts the name where both exist.
		///         </item>
		///         <item>The name, when exactly one catalog row carries it. The bulk of the fill.</item>
		///         <item>
		///             The name plus the goal's own map, when several rows carry it. MapId cannot be a general match
		///             key - it is a marker pin and is null on most of these goals - but where it is set it separates
		///             same-named templates cleanly, because they stand on different maps. Goal 7134 "Find the gate
		///             key" is the case: it names Saddur with no reference, and both Saddurs are candidates until its
		///             MapId 4010 picks the Tortage Island one (9500) over the Stygian Galley one (2993). Its sibling
		///             goal 3490 resolves by the first rule to the same template, which cross-checks the map reading.
		///         </item>
		///         <item>
		///             For a talk goal only: the name plus the conversation catalog, when exactly one candidate has a
		///             conversation at all. A TalkToNpc goal is completed by greeting the target, and greeting is
		///             driven by ConversationEntries, so a candidate with no conversation row cannot complete the goal
		///             on any evidence held. Where that leaves one candidate standing the choice is forced rather than
		///             guessed. Scoped to talk goals on purpose: a kill target has no reason to own a conversation, so
		///             the same test there would be an absence-of-evidence coin flip.
		///         </item>
		///     </list>
		///     Anything still ambiguous is left null and reported, never guessed: a wrong template is worse than none,
		///     because the name fallback still works while a wrong id silently matches the wrong creature.
		///     A template that is not held cannot be stored - TargetTemplateId is a foreign key - so goals naming NPCs
		///     on maps that have not been imported keep matching by name and resolve themselves as the maps land,
		///     which is also why this rule is safe to re-run.
		///     Bootstrap-only on its own column: a goal that already has a template id, from a hand pick settling an
		///     ambiguity or from a prior run, is left alone.
		/// </remarks>
		private static async Task ResolveGoalTargetTemplates(DatabaseContext db)
		{
			var goals = await db
				.QuestGoalDefinitions.Where(g => g.TargetTemplateId == null && g.TargetName != null && g.TargetName != "")
				.ToListAsync();

			var templates = await db.NpcTemplates.Select(t => new { t.TemplateId, t.Name }).ToListAsync();
			var held = templates.Select(t => t.TemplateId).ToHashSet();
			var byName = templates
				.Where(t => !string.IsNullOrEmpty(t.Name))
				.GroupBy(t => t.Name!, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.Select(t => t.TemplateId).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

			// Which maps each template stands on - the third rule's discriminator. A template with no spawn
			// contributes nothing, so it can never be picked by map, which is correct: it is not on the goal's map.
			var mapsOf = (await db.NpcSpawns.Select(s => new { s.NpcTemplateId, s.MapId }).Distinct().ToListAsync())
				.GroupBy(s => s.NpcTemplateId)
				.ToDictionary(g => g.Key, g => g.Select(s => s.MapId).ToHashSet());

			// The fourth rule's discriminator: the templates the conversation catalog can actually greet.
			var conversational = (await db.ConversationEntries.Select(e => e.NpcTemplateId).Distinct().ToListAsync()).ToHashSet();

			int byReference = 0, byUniqueName = 0, byMap = 0, byConversation = 0, ambiguous = 0, notHeld = 0, unknownName = 0;
			var unresolved = new List<string>();
			foreach (var goal in goals)
			{
				// 1. the goal's own typed reference.
				if (goal is { TrailingCategory: QuestProgress.NpcTemplateNameCategory, TrailingRefId: > 0 })
				{
					if (held.Contains(goal.TrailingRefId))
					{
						goal.TargetTemplateId = goal.TrailingRefId;
						byReference++;
						continue;
					}

					// The client states the template and no imported map holds it. Counted separately from a name that
					// resolves to nothing: this is a known answer that cannot be stored yet, not an unresolvable
					// string.
					notHeld++;
					continue;
				}

				if (!byName.TryGetValue(goal.TargetName!, out var candidates))
				{
					unknownName++;
					continue;
				}

				// 2. exactly one catalog row carries the name.
				if (candidates.Count == 1)
				{
					goal.TargetTemplateId = candidates[0];
					byUniqueName++;
					continue;
				}

				// 3. several do - let the goal's own map choose, and only when it picks exactly one.
				var onGoalMap = goal.MapId is { } mapId
					? candidates.Where(id => mapsOf.TryGetValue(id, out var maps) && maps.Contains(mapId)).ToList()
					: [];

				if (onGoalMap.Count == 1)
				{
					goal.TargetTemplateId = onGoalMap[0];
					byMap++;
					continue;
				}

				// 4. a TALK goal, and exactly one candidate is greetable.
				if (goal.GoalType == QuestGoalType.TalkToNpc)
				{
					var greetable = candidates.Where(conversational.Contains).ToList();
					if (greetable.Count == 1)
					{
						goal.TargetTemplateId = greetable[0];
						byConversation++;
						continue;
					}
				}

				ambiguous++;
				unresolved.Add($"goal {goal.GoalId} '{goal.TargetName}' -> {candidates.Count} templates ({string.Join("/", candidates)})");
			}

			if (byReference + byUniqueName + byMap + byConversation > 0)
				await db.SaveChangesAsync();

			Console.WriteLine($"Resolved {byReference + byUniqueName + byMap + byConversation} quest goal target template(s): {byReference} from the goal's own 51000 "
			                  + $"reference, {byUniqueName} from a uniquely-named catalog row, {byMap} from the name + the goal's own map, "
			                  + $"{byConversation} from the name + the conversation catalog on a talk goal (#521, #707).");
			Console.WriteLine($"  Left by NAME only: {notHeld} whose stated template no imported map holds, {unknownName} whose name matches no "
			                  + $"catalog row, {ambiguous} genuinely ambiguous. These still match on TargetName.");
			foreach (var line in unresolved)
				Console.WriteLine($"  ambiguous: {line}");
		}

		/// <summary>
		///     Configures one goal's server-trigger fields, finding or creating it by GoalId: the type and how it is
		///     matched at runtime (Kill, LootFromNpc and TalkToNpc by name, Kill also by TrailingRefId template id,
		///     CollectItem by TrailingRefId item, ReachArea by marker). The wire display fields - markers, keys, counts
		///     - are left as harvested. Chain the quest separately with <see cref="Chain" />.
		/// </summary>
		/// <param name="required">
		///     Set only when given: pass it for a hand-authored chain goal, omit it to preserve the captured
		///     RequiredCount, which is part of the byte-exact objective region.
		/// </param>
		/// <remarks>
		///     Bootstrap-only: it configures an un-wired goal (still the fresh-harvest default, GoalType None), so a
		///     re-import never clobbers trigger fields set by hand or by a prior run. A newly created goal is un-wired,
		///     so a fresh import still bootstraps.
		/// </remarks>
		private static async Task SeedGoal(DatabaseContext db, uint questId, uint goalId, QuestGoalType type, string? target = null, uint? questItemId = null,
			uint? required = null, bool gatesVisibility = false, bool staysDead = false, uint? worldObjectId = null)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null)
				return;

			var goal = quest.Goals.FirstOrDefault(g => g.GoalId == goalId);
			if (goal != null && IsWired(goal))
				return; // already configured, by hand or a prior seed

			if (goal == null)
			{
				goal = new QuestGoalDefinition
				{
					GoalId = goalId,
					Sequence = 1
				};
				quest.Goals.Add(goal);
			}

			goal.GoalType = type;
			goal.TargetName = target;
			goal.QuestItemId = questItemId;
			// The door, gate or lever a UseWorldObject goal is solved by using. Server-side, like TargetName beside it:
			// the goal record names its object nowhere on the wire.
			goal.TargetWorldObjectId = worldObjectId;
			// Editorial, and off by default: a one-time story target hides until its quest is active, and stays dead.
			// See QuestGoalDefinition.GatesTargetVisibility for why neither is ever derived from having a TargetName.
			// Two flags rather than one because a quest-only target on a repeatable quest wants the visibility gate
			// without the one-time kill.
			goal.GatesTargetVisibility = gatesVisibility;
			goal.TargetStaysDead = staysDead;
			if (required is { } r)
				goal.RequiredCount = r;
			if (goal.Sequence == 0)
				goal.Sequence = 1;

			// The key item's Type (Quest, 113) is not forced here: the `items` module types it from data, because the
			// chain captures deliver the key by a live DispatchAddItem into the 0x71 quest container, which
			// ItemHashHarvester.ExtractFromAddItem reads as ItemType.Quest.

			await db.SaveChangesAsync();
			Console.WriteLine($"Seeded goal: quest {questId} goal {goalId} = {type}{(target != null ? $" '{target}'" : "")}.");
		}

		/// <summary>
		///     Sets a quest's chain successor - the QuestFullUpdate the supersession swaps to.
		/// </summary>
		/// <param name="nextQuestId">The successor, or null for a chain end.</param>
		/// <remarks>
		///     Bootstrap-only: it sets an un-chained quest (NextQuestId still null), so a re-import never clobbers a
		///     successor set by hand or by a prior run. A fresh capture-harvested quest has NextQuestId null, so it
		///     still bootstraps, and a chain-end seed is a no-op on the already-null quest.
		///     NextQuestId is not in the harvest: a supersession is visible in a capture as a sequence of events, not
		///     as a field.
		/// </remarks>
		private static async Task Chain(DatabaseContext db, uint questId, uint? nextQuestId)
		{
			var quest = await db.QuestDefinitions.FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null || quest.NextQuestId != null)
				return;

			quest.NextQuestId = nextQuestId;
			await db.SaveChangesAsync();
		}

		/// <summary>
		///     A goal is wired once something has given it a server trigger - a typing rule, a seed or the quest
		///     editor. Bootstrap-only seeds skip a wired goal so a re-import never reverts a hand edit.
		/// </summary>
		/// <remarks>
		///     A single field test, because the un-typed default is a real value: <see cref="QuestGoalType.None" />
		///     means "no trigger" and nothing else.
		/// </remarks>
		private static bool IsWired(QuestGoalDefinition goal)
		{
			return goal.GoalType != QuestGoalType.None;
		}
	}
}
