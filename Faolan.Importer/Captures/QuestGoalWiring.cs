using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Deciding what each imported quest GOAL is - its trigger, its count, its target - from the evidence the captures
	// and the client catalogs carry. `WireQuests` is the whole pipeline and the only entry point; everything else is
	// one step of it.
	//
	// ⚠️ This is a BOOTSTRAP pass: every rule skips a goal another rule has already typed, so the ORDER in WireQuests
	// is the evidence ranking, strongest first. That ordering is the design - see the comment on WireQuests, and do
	// not reorder the steps to group them by shape.
	//
	// It lived in `ImportOps` until the placement audit (#825). It is not an import op: nothing here reads a capture
	// or writes a new row from one. It re-reads rows the Import* steps already landed and decides what they MEAN,
	// which is why it is worth its own file rather than another 1,250 lines in the grab-bag.
	public static class QuestGoalWiring
	{
		// Overlay the hand-authored quest WIRING onto the harvested definitions - the fields captures don't carry: the
		// server trigger (GoalType/TargetName/QuestItemId via SeedGoal), the chain successor (NextQuestId via Chain), and
		// the byte-exact @609/snapshot detail the beach + Casilda quests need. Run as its OWN importer module
		// (QuestWiringModule) AFTER the `quests` module, so it applies last - the trigger fields are preserved by the
		// objective-goals refresh, and the @609-detail seeds are authoritative (they're calibrated to match the capture).
		// Trigger/chain seeds are BOOTSTRAP-ONLY (see SeedGoal/Chain), so a re-run never reverts a Quest-editor edit.
		public static async Task WireQuests(DatabaseContext db)
		{
			// ⚠️ ORDER IS EVIDENCE STRENGTH, strongest first, because every step is bootstrap-only: whoever types a goal
			// first wins and the rest skip it. Getting this backwards is not a style question - it silently produces
			// wrong triggers, twice now (#522: the marker rule beat the goal's own sentence and turned seven talks and
			// an escort into walk-ups; #704: the sentence rule beat the Nadini override, whose whole point is that she
			// has no imported dialogue so a greeting can never fire).
			//
			//   1. a HUMAN decided        - the Seed* steps, including every editorial call (GatesTargetVisibility,
			//                               the Nadini stand-in) and everything no source states (QuestItemId, chains)
			//   2. the goal's own VERB    - its plaintext objective sentence, then the wire kind tag
			//   3. its TYPED REFERENCE    - "the trailing ref is an item, so this is a gather"
			//   4. its MAP MARKER         - the weakest: a pin sits on whatever the objective is ABOUT, which is the
			//                               NPC to talk to as readily as the place to walk to
			await SeedBeachQuestGoals(db);
			await SeedShipwreckedCityChain(db);
			await SeedCasildaChain(db);
			await SeedCasildaQuestSnapshot(db);
			await TypeItemGoalsFromTrailingCategory(db); // first of the rules: it also CORRECTS rows an older version mistyped
			await TypeTalkGoalsFromCatalogName(db);
			await TypeHandInGoalsFromCatalogName(db);
			// Orthogonal to the typing rules - it fills a COUNT, not a trigger - so it is not IsWired-guarded.
			await SetGoalCountsFromCatalogName(db);
			await TypeKillGoalsFromCatalogName(db);
			await TypeKillGoalsFromKind(db);
			await TypeInvestigateGoalsAsReach(db);
			await TypeUseGoalsFromKind(db);
			await TypeLocationGoalsFromTrailingCategory(db);
			await DeriveQuestIndexFromLine(db);
			await SeedQuestWorldEffects(db);
			// LAST, and deliberately so: it resolves whatever `TargetName` the steps above settled on, so it has to see
			// their final answer. Orthogonal to all of them - it fills an ID for a target already chosen, never a
			// trigger - so it is not IsWired-guarded.
			await ResolveGoalTargetTemplates(db);
		}

		// The beach starter (quest 10238): the `Quest_c` scalars the harvest does NOT carry, plus its two triggers.
		//
		// ⚠️ The per-goal @609 TRANSCRIPTION is gone (#705). It used to restate both goals' whole objective record -
		// `Separator`, `SubId`, marker, `Extra5/6`, the objective/trailing refs and categories, and FOUR hard-coded
		// base85 LDB keys. Measured field by field against a blanked probe: the `quests` harvest reproduces every one
		// of them exactly, keys included, and recreates the goal rows from nothing if they are deleted outright. Only
		// goal 1448's `TrailingLocalized` differs, and it is kept below.
		//
		// ⚠️ The `Quest_c` scalars are NOT the same story and stay. The first pass at this deleted them on a
		// measurement that had `--only quests` restoring them - which was wrong, because that plan silently INCLUDES
		// `quest-wiring`, so the seed was restoring its own values. Re-measured with the seed absent, the harvest
		// carries `Level` and `IndexMagic` and nothing else here: dropping the rest takes `HasSnapshotRecord` with it,
		// and 10238 falls out of the login snapshot (objective regions 98 of 99 instead of 99).
		private static async Task SeedBeachQuestGoals(DatabaseContext db)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == 10238);
			if (quest == null)
				return;

			quest.Field118 = 4;
			quest.QuestLineFlags = QuestFullUpdates.LiveQuestLineFlags; // the beach record's `n` (@+0x14)
			quest.FieldEc = 1;
			quest.FieldF0 = 0x96;
			quest.FieldF8 = 15;
			quest.HasSnapshotRecord = true;
			quest.IndexMarker = 0x7E2;
			quest.IndexTrailerWord = 0x000156E7;
			// The magic, the (sequence, remaining) pair and the line catalog are DERIVED from QuestLines +
			// JournalSequence by DeriveQuestIndexFromLine (#641). tf1/tf2 are stamped non-zero here so that step knows
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
			// the Hoxxling gameplay capture (Saddur dies fr11942 -> 10238 completes -> 12369 -> 12373). The kill VERB
			// types the goal, so what is seeded is the editorial half: `GatesTargetVisibility` (a call the client's data
			// does not state anywhere - see that column) and `TargetName`, because the objective says "slavemaster
			// Saddur" while the live NPC is "Saddur".
			//
			// Goal 1448 "Enter the jungle" is a reach at its own marker. Its reference is a 51200 bounded AREA rather
			// than a location, so TypeLocationGoalsFromTrailingCategory does not see it, and a `RegionScoped` goal is
			// not a reach by kind alone (goal 11712 has RequiredCount 3 - the region SCOPES the objective). Its verb is
			// what settles it, so it stays a seed until the "Enter <place>" shape is a rule (#704).
			await SeedGoal(db, 10238, 1448, QuestGoalType.ReachArea);
			await SeedGoal(db, 10238, 3490, QuestGoalType.Kill, "Saddur", gatesVisibility: true, staysDead: true);
			await Chain(db, 10238, 12369);
		}

		// The Shipwrecked destiny chain past stage I (#262/#358). Beach dump proves the supersession 10238 -> 12369 ->
		// 12373 (stages I "The Slaver" -> II "The Acheronian Gate" -> III "The City of Tortage"). Stages II/III are wired
		// as ReachArea goals off their captured MARKERS (verified world X,Z): II's goal 3492 at the beach ruins
		// (4010, 524/755), III's goal 3498 in the city itself (4020, 913/718) - "enter the city" completes on arrival.
		// Stage III is chain-final for now: stages IV-VIII (talk Turach 951 / bricks 12371 / guard / Tina 2805 / Nadini
		// 806) have no imported goal targets yet - tracked on #358. NOT byte-exact; markers/types are best-effort ("polish
		// later"). Idempotent.
		private static async Task SeedShipwreckedCityChain(DatabaseContext db)
		{
			// II "The Acheronian Gate" -> III "The City of Tortage". Its key objective is LOOTING the Acheronian Gate
			// Key (item 193186) from Saddur's corpse bag - the same kill->loot pattern as Casilda's Tuut key: Hoxxling
			// gameplay fr11942 Saddur dies -> fr11947 his corpse becomes a loot bag -> fr12023 the key is added to the
			// 0x71 quest container.
			//
			// ⚠️ That objective is goal **7134** ("Find the gate key"), NOT 3492. This seed had the pair the wrong way
			// round (#527): it put the loot on 3492 and dismissed 7134 as *"retail's 2nd goal 'unlock the gate' wasn't
			// imported"*, but the imported NAMES say the opposite - 3492 IS "Unlock the Acheronian gate" and 7134 IS
			// "Find the gate key". The mix-up came from reading 3492's 524/755 marker as Saddur's spot: Saddur spawns
			// at 521/757, and the world object **"Acheronian Gate" (975369919) stands at 523.4/754.7**, ~3u away - so
			// the marker is ambiguous between the two and the goal's own name is what settles it. 7134's marker
			// (574/944) lands on the "Acheronian Chest" (2018094960, at 573.6/946.7), which is the key's other end.
			//
			// The ORDER was wrong for the same reason and is fixed in the RDB catalog importer
			// (ResequenceGoalsByPhase): the client's phase says 7134 is played first, and the corpus agrees - 7134 is
			// the goal that takes the `Advanced` hand-off, which only a NON-last goal gets.
			//
			// 3492 = ReachArea at the gate itself - the gate object sits on its marker, and arriving there with the key
			// is what "unlock" amounts to for us until a use-the-object goal type exists. III is chain-final for now.
			//
			// ⚠️ It STAYS a seed while its sibling 3498 "Enter the City of Tortage" went (#705): 3498 carries a
			// TrailingCategory 100 location reference, so TypeLocationGoalsFromTrailingCategory types it from the
			// marker, and 3492 carries NO typed reference at all (category 0). Nothing but the verb "Unlock" can place
			// it, and one goal is not a shape.
			await Chain(db, 12369, 12373);
			await CorrectAcheronianGateGoals(db);
			await SeedGoal(db, 12369, 7134, QuestGoalType.LootFromNpc, "Saddur", 193186, 1);
			await SeedGoal(db, 12369, 3492, QuestGoalType.ReachArea);

			// V "The Quarry" (12371) - granted by Turach's dialogue (ConversationNodes.GrantQuestId 12371), so NOT chained
			// here. goal 3495 "get 3 stone bricks" = CollectItem of item 193400 (its TrailingRefId), picked up from the 12
			// "Stone Brick" QuestItemEffect dynels at the quarry (4020). goal 3497 "deliver 3 to Turach" = TalkToNpc Turach
			// (his spawn 824/722 == the marker); the captured count 3 is kept (byte-exact @609) and the talk SOLVES it.
			// Decoded from Hoxxling gameplay2 (pickups fr4310/4367/4447 = DispatchAddItem+SimpleDynelPickup; deliver fr5102).
			await SeedGoal(db, 12371, 3497, QuestGoalType.TalkToNpc, "Turach");

			// The city TALK stages (#358), progressed by each NPC's conversation GrantQuestId (already imported) - so no
			// Chain() is needed, only the goal TARGET. Confirmed in the Hoxxling gameplay2 dump by the talk-then-complete
			// pairs: Laranga (guard, 0x4FDA) convo fr5680 -> 10286 done fr5829; Tina (0x4FF0) convo fr9214 -> 10287 done
			// fr9738. 10320 "talk Turach" (granted by the entry guard Laranga; talking to Turach completes it + his
			// dialogue grants the quarry 12371) is the pre-quarry "The Blacksmith" stage - inferred from the grant graph
			// + wiki, its dump proof is the quarry grant that predates gameplay2.
			await SeedGoal(db, 10286, 399, QuestGoalType.TalkToNpc, "Laranga"); // VI The Guard - talk the city guard
			await SeedGoal(db, 10287, 400, QuestGoalType.TalkToNpc, "Tina"); // VII The Barmaid - talk Tina

			// VIII "Nadini" (10915, granted by Tina): the wiki objective is "Talk to Nadini", but Nadini 806 has NO
			// conversation imported, so her greeting can't fire the talk trigger (PickConversationScreen returns null).
			// Her spawn (1026,670 @4022 night city) EXACTLY matches the goal marker, so wire it as ReachArea (reach her)
			// - the robust stand-in until her dialogue is imported. Chain end (Shipwrecked -> "The Awakening I" is a
			// separate destiny quest, out of scope; Nadini can't grant it without dialogue).
			await SeedGoal(db, 10915, 1352, QuestGoalType.ReachArea);
		}

		// ⚠️ CORRECTIVE, and it has to run BEFORE SeedShipwreckedCityChain's bootstrap guard, exactly like the
		// TypeItemGoalsFromTrailingCategory reset above: goal 3492 was seeded `LootFromNpc "Saddur"` by the older,
		// swapped version of that seed, and a wired goal is skipped by `IsWired` forever - so the mistyping would
		// outlive the fix (#527).
		//
		// Narrow on purpose: only the exact row the old seed could have produced. A Quest-editor edit or a hand seed
		// carries something else and is left alone. It also clears 7134's `Partial` flag, which was set when the goal
		// was known by identity + objective text only - it now carries its kind tag, marker, count and (below) its
		// trigger, so it is a whole goal and `AllGoalsComplete` must stop skipping it.
		private static async Task CorrectAcheronianGateGoals(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.QuestDefinitionId == 12369).ToListAsync();
			var unlock = goals.Find(g => g.GoalId == 3492);
			if (unlock is { GoalType: QuestGoalType.LootFromNpc, TargetName: "Saddur", QuestItemId: 193186 })
			{
				unlock.GoalType = QuestGoalType.None; // the un-wired shape IsWired reads as "not wired"
				unlock.TargetName = null;
				unlock.QuestItemId = null;
				Console.WriteLine("Corrected quest 12369: goal 3492 'Unlock the Acheronian gate' was carrying goal 7134's key loot (#527).");
			}

			var findKey = goals.Find(g => g.GoalId == 7134);
			if (findKey is { Partial: true })
				findKey.Partial = false;

			await db.SaveChangesAsync();
		}

		// The Tortage intro quest CHAIN (#261): 13479 is a four-stage supersession chain. Idempotent.
		//
		// ⚠️ Nothing here turned out to be removable (#705). `NextQuestId` is NOT harvested - measured with the seeds
		// absent, every chained quest came back null, because a supersession is visible in a capture as a SEQUENCE OF
		// EVENTS rather than a field - and neither is goal 3544's escortee template id.
		private static async Task SeedCasildaChain(DatabaseContext db)
		{
			await Chain(db, 13479, 13563);
			// gatesVisibility: Tuut is a one-time story target - hidden until 13479 is active, and he stays dead
			// (retail solo-instances him). The only kill goal besides Saddur that gets it; see GatesTargetVisibility.
			await SeedGoal(db, 13479, 5744, QuestGoalType.Kill, "Scavenger Tuut", required: 1, gatesVisibility: true, staysDead: true);
			// The loot stage's key item: Scavenger Tuut's bag drops 319224 "Scavenger's Key" (#261), so the player sees
			// and takes the actual key, not just an empty source-matched bag. WHICH corpse holds WHICH quest item is in
			// neither the wire goal record nor the RDB catalog, so this one is genuinely un-harvestable today (#705).
			await Chain(db, 13563, 13480);
			await SeedGoal(db, 13563, 5867, QuestGoalType.LootFromNpc, "Scavenger Tuut", 319224, 1);
			// "Free Casilda" - a talk objective whose verb no talk shape recognises.
			await Chain(db, 13480, 12407);
			await SeedGoal(db, 13480, 5745, QuestGoalType.TalkToNpc, "Casilda", required: 1);
			await Chain(db, 12407, null); // escort is the chain end
			await SeedGoal(db, 12407, 3544, QuestGoalType.Escort, "Casilda", required: 1);

			// The escortee's TEMPLATE id (Casilda 9411) as the Escort goal's TrailingRefId, so the runtime follow trigger
			// resolves her by ID not name (#262). Set AUTHORITATIVELY every run (not bootstrap-only): the capture's escort
			// @609 carries no trailing ref and this module runs after the objective-goals refresh, so it must win. Byte-
			// safe - 12407's objective region is not in the tested set.
			//
			// ⚠️ Re-measured for #705 and it stays: a blanked column comes back 0 from the refresh, and a DELETED row is
			// recreated with 0 as well. (The first pass read a probe whose row had been recreated while this override was
			// still in place - the same self-confirming measurement that briefly took the Quest_c scalars out.)
			var escortGoal = await db.QuestGoalDefinitions.FirstOrDefaultAsync(g => g.QuestDefinitionId == 12407 && g.GoalId == 3544);
			if (escortGoal != null)
			{
				escortGoal.TrailingRefId = 9411;
				await db.SaveChangesAsync();
			}
		}

		// Casilda's quest 13479 `Quest_c` scalars (#255): the journal-record fields that live ONLY in the FullChar
		// snapshot and are not in the harvest. Idempotent - set every run.
		//
		// ⚠️ Its per-goal @609 transcription is gone (#705): goal 5744's whole objective record - `Separator`, `SubId`,
		// marker, `Extra5/6`, refs, categories, both LDB keys - is reproduced EXACTLY by the harvest, field for field.
		// `Level`, `Field118`, `FieldEc`, `HasSnapshotRecord` and `QuestLineFlags` are harvested too; these five are not.
		private static async Task SeedCasildaQuestSnapshot(DatabaseContext db)
		{
			var quest = await db.QuestDefinitions.FirstOrDefaultAsync(q => q.QuestId == 13479);
			if (quest == null)
				return;

			quest.FieldF0 = 80;
			quest.FieldF8 = 7;
			quest.XpReward = 80;
			// Overwritten with the derived pair by DeriveQuestIndexFromLine (#641); non-zero here so that step knows
			// this quest's index is one the snapshot builds from. See the beach seed's note.
			quest.IndexTableField1 = 1;
			quest.IndexTableField2 = 1;
			await db.SaveChangesAsync();
		}

		// A goal whose TRAILING REFERENCE is an ITEM is a gather objective, so type it `CollectItem` (#521). The goal
		// row already says so without any RDB or LDB lookup: `TrailingCategory` is a typed reference and 50200 is the
		// client's own `item.name` table (docs/rdb/ldb-string-index.md), so the ref IS an item id.
		//
		// This is a RULE, not a per-quest seed - which is the point. 72 of the 74 such goals sat at the fresh-harvest
		// `Kill` default with no target, i.e. inert: a fetch quest that can never complete, because the kill matcher
		// was looking for an NPC whose template id happens to equal an item id (it never does - NPC ids run 38..22,982
		// and these refs 110,891..4,675,657, so they were harmless as well as useless).
		//
		// ⚠️ It does NOT try to distinguish HOW the item is obtained. The client's data does not say, and the set mixes
		// mob drops (Mangy Yeti Pelt, Hyena Testicles), world pickups (Quarry Stonebrick) and bought/gathered
		// consumables (Pink Salt, Visionwine). It does not need to: a gather objective is satisfied by HOLDING the
		// item, so the runtime ticks these on the item reaching the inventory from any source
		// (QuestProgressionService.AdvanceCollectGoals off the single grant choke point). Guessing
		// LootFromNpc-vs-CollectItem per quest would buy nothing and a wrong guess makes the quest silently
		// un-completable.
		//
		// BOOTSTRAP-ONLY like every other wiring step: an already-wired goal (IsWired - anything but the bare Kill
		// default) is left exactly as it is, so a Quest-editor edit or a hand seed survives a re-import.
		private static async Task TypeItemGoalsFromTrailingCategory(DatabaseContext db)
		{
			const uint itemNameCategory = 50200; // LDB `item.name`

			var goals = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == itemNameCategory && g.TrailingRefId > 0)
				.ToListAsync();

			int typed = 0, untyped = 0;
			foreach (var goal in goals)
			{
				// ⚠️ CORRECTIVE, and it has to run BEFORE the bootstrap guard: nineteen rows were already typed
				// CollectItem by the older version of this rule, which only excluded `Deliver`. A goal typed
				// CollectItem IS wired, so the guard below would skip it forever and the mistyping would outlive the
				// fix. Reset to `None` - NO trigger - so the quest is VISIBLY stuck rather than silently completing on a
				// pickup. (That reset was itself `Kill` until #522 made "no trigger" a value: it moved the twenty rows
				// off a wrong trigger onto a *different* wrong one, safe only while no NPC template id collided with an
				// item id.)
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

				// ⚠️ NOT every item goal is a collect - the goal's own `Separator` is the VERB (see QuestGoalKind), and
				// for five of them OBTAINING the item is not DOING the objective. Typing one of those as a collect
				// completes the quest the moment the item reaches the backpack, skipping the objective entirely - a
				// silent chain-progression bug, where leaving it unwired is at least visibly stuck.
				//
				// `Deliver` was the first one found; #522 named three more and this is where they stop being typed:
				//
				//   Deliver (20)       hands the item OVER - obtaining it is the START of the objective, not the end
				//   Craft (5)          "Make Visionwine" - a BOUGHT Visionwine is not a made one
				//   Use (10)           "Use the Vial to Capture Gas" - and the vial (4349109) IS in our catalog, so
				//                      this one was live: picking the vial up completed the objective
				//   DestroyObject (25) "Destroy Bowl of Set" - destroying an object does not put it in your bag, so
				//                      this can only ever be wrong, in whichever direction
				//   Investigate (24)   "Investigate the Ancient Gateway" - typed ReachArea instead, below
				//
				// ⚠️ #522 recorded these as "inert - nothing advances them". They were not: all twenty carried a real
				// TrailingRefId and were typed CollectItem, so each would advance on ARRIVAL of its item. Most are
				// harmless today only because the item is absent from our catalog, which is coverage, not a design.
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

		// A goal whose TRAILING REFERENCE is a LOCATION is a reach objective, so type it `ReachArea` (#641). The sibling
		// of TypeItemGoalsFromTrailingCategory, and the same argument: `TrailingCategory` is a typed reference, 100 is
		// the client's location category, and the goal already carries the map marker to reach - so this is a RULE over
		// a column, not a per-quest seed.
		//
		// It replaces `SeedCaptureReachQuests`, which hand-listed SEVEN of these with their destination coordinates
		// sitting in comments. That seed was also the reason the rule looked unnecessary: it ran first and the
		// bootstrap guard then made every other cat-100 goal invisible. Removing it and typing the column instead
		// reproduces all seven and reaches 11 more the seed never named.
		//
		// ⚠️ The ticket that raised this expected category 51200 (`bounded_areas`, which RdbRegionImporter resolves into
		// `RegionId`). That is a different, much smaller set - THREE goals corpus-wide, all of which already have a
		// RegionId. These seven are cat-100, whose reach target is the goal's own map MARKER.
		//
		// ⚠️ Requires a marker, and that guard is load-bearing rather than defensive: 23 of the 34 un-wired cat-100
		// goals carry NO map marker at all. A reach objective with no place to walk to can never be solved, so typing
		// one would turn a visibly-inert quest into a silently-uncompletable one. Those 23 stay inert and are counted.
		//
		// BOOTSTRAP-ONLY, like every wiring step: the eight cat-100 goals already typed TalkToNpc (Kalanthes, Sharak,
		// Chulainn, ...) and Casilda's Escort are wired, so the rule does not see them.
		// The verbs whose objective is NOT satisfied by the item arriving in the backpack - see the note in
		// TypeItemGoalsFromTrailingCategory, which is the only place this is consulted.
		private static readonly FrozenSet<uint> UntypedByArrival = new[]
		{
			(uint)QuestGoalKind.Deliver, (uint)QuestGoalKind.Craft, (uint)QuestGoalKind.Use, (uint)QuestGoalKind.DestroyObject, (uint)QuestGoalKind.Investigate
		}.ToFrozenSet();

		// A goal whose own objective SENTENCE asks the player to go and talk to someone is a `TalkToNpc` goal, and the
		// sentence names who (#704).
		//
		// The sentence is `QuestGoalDefinition.Name` - the PLAINTEXT `QuestGoal.name` of the client's quest catalog
		// (RDB 1000104), a column on 7,299 of 7,317 goals. `RdbQuestTextImporter` had been asking the same question of
		// the *encrypted* LDB 50304 text and reporting **7,147 goals "unreadable"**: they were not unreadable, they were
		// being read from the source that needs a per-string TEA key only a capture yields. Same sentence, and this one
		// is free - which also means the rule belongs in the capture-driven module rather than an opt-in RDB one, so it
		// runs on every import and can sit BEFORE the marker rule that would otherwise claim these goals.
		//
		// ⚠️ PURE TALK SHAPES ONLY - "Talk/Speak/Report to X", "Ask X about Y". The DELIVERY shapes are the larger set
		// by far (Give 656, Hand 504, Return 385, Deliver 309 against Talk 473 / Speak 242 / Report 225) and they are
		// deliberately NOT typed from the sentence. A delivery hands an item over, so it must be gated on the player
		// HOLDING it - QuestProgress.CanHandIn, which keys on the wire kind (`Separator` = Deliver) and the item the
		// trailing reference names. A catalog-only goal has neither, so typing one TalkToNpc would complete it on a
		// bare greeting with the goods still in the shop: the same over-eager trigger #522 spent its length removing.
		// They stay `None` until their wire record is captured, and `RdbQuestTextImporter` keeps that half.
		//
		// The name is reconciled against the NPC catalog exactly as the text rule does it - a `TargetName` is a MATCH
		// KEY compared for equality, and prose gives the short form ("Bronwith" for "Battlemaster Bronwith").
		//
		// BOOTSTRAP-ONLY. A target we do not hold is recorded anyway and COUNTED: it is the client's own statement of
		// who the goal is about, and the goal starts working the day its map is imported.
		private static async Task TypeTalkGoalsFromCatalogName(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Name != null && g.Name != "").ToListAsync();
			var npcNames = (await db.NpcTemplates.Where(t => t.Name != null).Select(t => t.Name!).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			// A TalkToNpc goal advances only once a GREETING is served, so an NPC with no ConversationNode rows cannot
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
					// The sentence names a ROLE, not a name - "Talk to the city guard", "Talk to the Red Hand
					// Sergeant". Only the goal's MARKER can say which NPC that is, and 868 of 7,317 goals have none.
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

		// A HAND-IN goal whose sentence names BOTH ends - the recipient and the goods - is a gateable delivery, so type
		// it and record the item (#704).
		//
		// ⚠️ This is the exception to "the delivery shapes are not typed from the sentence", and the condition is what
		// makes it safe rather than the verb. The blanket refusal exists because a delivery must be gated on the player
		// HOLDING the goods (QuestProgress.CanHandIn), and a catalog-only goal has no wire kind and no item reference
		// to gate on - so typing it TalkToNpc completes the quest on a bare greeting with the goods still in the shop.
		// That argument disappears the moment the item RESOLVES: `QuestItemId` is exactly what CanHandIn now reads, so
		// these goals are gated as tightly as a captured `Deliver` is. The 1,846 whose item does not resolve stay
		// `None`, unchanged.
		//
		// BOTH ends must resolve unambiguously, and the item must be the WHOLE noun phrase (see
		// QuestObjectiveText.HandOverPhrase for why a substring match is not good enough - it is twice as productive
		// and 40% wrong). Nine goals qualify today; five of their recipients have captured dialogue, so those are
		// playable now and the rest start working when theirs is captured.
		private static async Task TypeHandInGoalsFromCatalogName(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Name != null && g.Name != "").ToListAsync();
			var npcNames = (await db.NpcTemplates.Where(t => t.Name != null).Select(t => t.Name!).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			// Name -> the single item that owns it. A name held by SEVERAL catalog rows is dropped: `CanHandIn` tests one
			// template id, so picking either would gate on an item the player may legitimately not be the holder of.
			var itemsByName = (await db.Items.Where(i => i.Name != null).Select(i => new { i.Id, i.Name }).ToListAsync())
				.GroupBy(i => i.Name!, StringComparer.OrdinalIgnoreCase)
				.Where(g => g.Select(i => i.Id).Distinct().Count() == 1)
				.ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

			int typed = 0, noRecipient = 0, noItem = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal) || QuestObjectiveText.HandOverPhrase(goal.Name) is not { } phrase)
					continue;

				// ⚠️ A goal whose WIRE record already names the item is left alone - the reference is direct evidence
				// where the sentence is inferred, and `IsDelivery`'s captured branch already gates it. Worth being
				// explicit rather than relying on run order: this rule fires before the opt-in `rdb-quest-text` module,
				// so without the guard the sentence would quietly outrank the capture on goal 5792.
				//
				// The two agree wherever both exist, which is what makes the sentence trustworthy at all: over the 12
				// captured `Deliver` goals the phrase resolves 6 and matches the wire's item id on all 6, with zero
				// disagreements. The other 6 name something the catalog does not hold under that spelling ("3 stone
				// bricks", "the Remedy", "the Letter"), and the two "Cimmerian Furs" goals resolve to a name TWO items
				// share - which the wire separates and this rule refuses, as it refuses every ambiguous name.
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
						// anyway and the goal waits for its map - a delivery with an unresolvable END is not worth
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

		// The AMOUNT an objective asks for, read from its own sentence (#704). "Hand in 20 Yew", "Collect ten Frost
		// Serpent Skins".
		//
		// This is the one thing the client's quest catalog states that NOTHING else records. `RequiredCount` otherwise
		// comes only from the wire goal record, so a goal no capture has shown sits at 0 - and the runtime reads 0 as
		// ONE (`Math.Max(RequiredCount, 1)` in AllGoalsComplete and CanHandIn). 58 shipped goals were in exactly that
		// state, 28 of them asking for more than one: "Hand in 20 Yew" completed on the first log.
		//
		// ⚠️ CATALOG-ONLY GOALS, and that guard is what makes writing a wire field safe. `RequiredCount` is written
		// straight back out in the byte-exact @609 objective region, so overwriting a captured one would break the
		// golden tests. A goal with `Separator` null was never on the wire: measured, none of the 58 rides the login
		// snapshot and none belongs to a quest with ANY wire goal, so no region we build can contain one. Paired with
		// `RequiredCount == 0` (never harvested, never hand-set), this cannot touch a value anyone has established.
		//
		// The sentence AGREES with the wire wherever both exist - goals 3495 "Get 3 stone bricks" and 3497 "Deliver 3
		// stone bricks to Turach" both say 3 and both were captured as 3. Two samples is thin, so the guard above is
		// what carries the safety; the agreement is only what makes the reading credible.
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

		// A KILL goal whose sentence names a creature we HOLD gets that name as its target (#704).
		//
		// A catalog-only kill goal has `TrailingRefId` 0 and no `TargetName`, so `MatchingKillGoal` can match it by
		// neither id nor name - it is typed-and-inert at best. The sentence names the creature, and the name is exactly
		// what that matcher's fallback branch wants: it is compared against the LIVE entity's name, which is what a
		// "kill N of <group>" goal needs anyway (a group has no single template id).
		//
		// ⚠️ The name must resolve to exactly one `NpcTemplates` row. That is not politeness about coverage - it is the
		// safety property. An unresolvable name would be stored and never match anything, which is inert dressed up as
		// wired; a name we hold is by construction the thing the player can kill. Measured against the 24 goals whose
		// target the wire established: the extraction reproduces 17 of the strings exactly, and of those that ALSO
		// resolve to a template it is right 4 times and wrong none.
		//
		// The seven it cannot reproduce are all one of three shapes, and all correctly yield nothing rather than a
		// guess: a ROLE ("Kill the Chief" -> Tribal Chief Rak'tunab, "Kill the Demon" -> Balbet-Zhun), a title the prose
		// adds or drops ("Kill slavemaster Saddur" -> Saddur, "Kill Zhu-la" -> Heiromonk Zhu-La), and one editorial
		// rename ("Destroy Po-Sha's Source of Corruption" -> Ravenous Fiend).
		//
		// ⚠️ `GatesTargetVisibility` is NOT set, and must not be: these are population mobs, and gating one hides every
		// same-named NPC from every player not on the quest while forcing NoRespawn. See that column.
		private static async Task TypeKillGoalsFromCatalogName(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Name != null && g.Name != "").ToListAsync();
			// Name -> the single template that owns it. A name several templates share is dropped: `MatchingKillGoal`
			// compares the live entity's NAME, so a shared name is not ambiguous at match time - but it is ambiguous
			// about which creature the quest MEANT, and three distinct "Scavenger Lackey" templates is the known case.
			var byName = (await db.NpcTemplates.Where(t => t.Name != null).Select(t => t.Name!).ToListAsync())
				.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
				.Where(g => g.Count() == 1)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

			int typed = 0, unresolved = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal) || goal.TargetName != null)
					continue;

				// A goal whose WIRE record names an npc_template is left alone - `RdbQuestGoalTargetImporter` fills those
				// from the reference, which is direct evidence where the sentence is inferred.
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

		// A goal is a KILL goal because the client's own VERB says so (`Separator` = QuestGoalKind.Kill), and for no other
		// reason (#522).
		//
		// This rule exists because `Kill` used to be enum value 0 - the un-typed default - so a kill goal never needed
		// typing at all: it just inherited the trigger. That made "kill" the answer for 7,148 catalog goals with no verb
		// and no target, and every guard in QuestProgress.MatchingKillGoal is a patch over one way that misfired. With
		// the default now `None`, the 30 goals that really do ask for a kill have to say so, and this is where they do.
		//
		// The 30 split two ways, and BOTH are typed - the verb decides, not whether we can resolve the target:
		//   24 name an NPC template (TrailingCategory 51000), which is what MatchingKillGoal matches by id;
		//    6 name a monster CATEGORY (51100 `categorytree.description` - "Kill Atzel's Watch", "Kill Undeads in
		//      Xibaluku"), which resolves to an editor GROUP name matching no NPC, so they are typed and inert until
		//      that group can be expanded. Typing them anything else would be a lie about a goal we read correctly, and
		//      leaving them at `None` would lose the one thing the client did tell us.
		//
		// BOOTSTRAP-ONLY like every wiring step; the Seed* steps run before it (see WireQuests) so a hand-seeded kill
		// keeps the editorial fields - GatesTargetVisibility - that only a seed sets.
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

		// #522: an INVESTIGATE goal is a place you go and look at, so it is a reach - the same shape
		// TypeLocationGoalsFromTrailingCategory types, reached from the VERB rather than from a location reference.
		//
		// One goal corpus-wide, "Investigate the Ancient Gateway" (8278, quest 15051), and it carries a real map
		// marker (2090 @ 928/1011). Its trailing reference is an ITEM (category 50200), which is why it was being
		// swept up as a collect; the item is not in our catalog, so the practical effect was an objective that could
		// never complete.
		//
		// ⚠️ ASSUMPTION, and the honest one to state: retail may want an INTERACTION with the gateway rather than
		// proximity to it. We hold no world object at that marker to interact with - map 2090's WorldObject rows are
		// all worn NPC gear - so a reach is the only trigger the data supports, and #522 proposed exactly this. It
		// errs toward completing the objective by walking to the gateway, which is a thing a player cannot do without
		// going there; the alternative is a goal that never completes at all.
		//
		// Marker-guarded like its sibling, and for the same load-bearing reason: a reach with nowhere to walk turns a
		// visibly-inert quest into a silently-uncompletable one.
		private static async Task TypeInvestigateGoalsAsReach(DatabaseContext db)
		{
			var goals = await db.QuestGoalDefinitions.Where(g => g.Separator == (uint)QuestGoalKind.Investigate).ToListAsync();

			var typed = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal) && goal.GoalType != QuestGoalType.CollectItem)
					continue; // hand-reviewed or already given a better type

				// ⚠️ A PLACEHOLDER marker is not a marker (#522): `(50, 50)` is on nine playfields in the shipped data
				// and `(1, 1)` on three, so typing a reach off one produces a goal solved by standing in the corner of
				// the map. `HasReachablePin` covers the markerless case too, which is what this used to test alone.
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

		// #522: a `Use` goal is solved by USING the item its typed reference names - the verb is the trigger, and the
		// row states everything needed for it (`TrailingCategory` 50200 = an item, `TrailingRefId` = which one).
		//
		// One goal corpus-wide: *"Use the Vial to Capture Gas"* (11252, quest 17234), reference 4349109
		// `Po-Sha Potency Vial` - an item we DO hold, and one that carries an on-use spell, so the whole path exists.
		//
		// ⚠️ It must not be typed `CollectItem`, which is what `TypeItemGoalsFromTrailingCategory` would otherwise do
		// with an item reference: the vial is HANDED to the player by the quest giver, so counting its arrival solves
		// the objective before the player has used it. `UntypedByArrival` already excludes the kind for exactly that
		// reason; this is the trigger that was missing on the other side of that exclusion.
		//
		// ⚠️ Guarded on the item EXISTING in our catalog, and the guard is load-bearing rather than defensive: a use
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

				// ⚠️ The goal's own SENTENCE outranks its marker, and this guard is what makes that true on a
				// from-scratch import. A marker is the weakest evidence a goal carries: "Speak to Chulainn" has a pin
				// at Chulainn's feet, and "Escort Casilda to the city" has one where Casilda is STANDING when the
				// escort is granted. Eight goals came out of a clean rebuild typed ReachArea for that reason - seven
				// talks that would close without their conversation ever playing, and the escort, which would close the
				// instant it began. They only looked correct in the shipped DB because a favourable ORDERING had wired
				// them first and the bootstrap guard then hid the rule.
				//
				// The sentence is read from `Name` - the PLAINTEXT `QuestGoal.name` of the client's quest catalog,
				// already a column here. The decrypted LDB 50304 text says the same thing (RdbQuestTextImporter) but
				// needs a per-string key and an RdbDir, and this module deliberately runs without one.
				if (QuestObjectiveText.NeedsMoreThanReachingItsMarker(goal.Name))
				{
					otherVerb++;
					continue;
				}

				// ⚠️ The marker columns are NULLABLE and null is the common case here, so this must test for a value
				// rather than for zero: `goal.MapId == 0` on a `uint?` is FALSE when the column is null, which let 23
				// markerless goals through on the first run of this rule - the exact silently-uncompletable quest the
				// guard exists to prevent.
				//
				// ⚠️ And a PLACEHOLDER marker is not a marker either (#522) - `(50, 50)` is on nine playfields in the
				// shipped data and `(1, 1)` on three, so a reach typed off one is solved by standing in the corner of
				// the map. Both cases are `QuestProgress.HasReachablePin`, which the runtime trigger asks as well.
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

		// The quest INDEX fields, derived from the quest's own LINE instead of hand-typed per quest (#641).
		//
		// `QuestIndexRegions.ForQuests` prefers the catalog its caller loads from the database and reads these columns
		// only as a FALLBACK (the generation tests, which build a region from one definition with no line loaded). That
		// makes the stored values pure duplication of `QuestLines` + `JournalSequence` - and the beach and Casilda
		// snapshot seeds were hand-typing them, ~12 quest ids and two ACGHash tokens transcribed from a capture.
		//
		// Measured before deleting the literals: `IndexMagic` equals `QuestLines.Magic` on all 16 quests that carry
		// one, and the two hand-typed catalogs reproduce EXACTLY from the line's quests ordered by JournalSequence
		// (line 241 = the eight Shipwrecked steps, line 1054 = Casilda's four). So the derivation is not an
		// improvement on the transcription - it is the same values with a source.
		//
		// Scoped to the quests that already carry an index (a non-zero magic, harvested from the QuestJournal's area
		// header): filling all 5,048 would put a fallback on every quest that nothing reads and churn faolan.sql for it.
		private static async Task DeriveQuestIndexFromLine(DatabaseContext db)
		{
			// A quest is "in the index" if any of its index columns is set: the magic comes off the harvest (the
			// QuestJournal's area header, #544) and the marker/table pair off the snapshot seeds, and either alone is
			// enough - scoping on the magic only would drop a snapshot quest whose journal no capture carried.
			var quests = await db.QuestDefinitions.Where(q => q.IndexMagic != 0 || q.IndexMarker != 0 || q.IndexTableField1 != 0 || q.IndexTableField2 != 0).ToListAsync();
			if (quests.Count == 0)
				return;

			var lineIds = quests.Select(q => q.QuestLineId).ToHashSet();
			var lines = await db.QuestLines.Where(l => lineIds.Contains(l.QuestLineId)).ToDictionaryAsync(l => l.QuestLineId, l => l.Magic);

			// The same catalog the runtime loads (DatabaseRepository.GetQuestDefinitionsByLine): the line's steps that
			// have journal TEXT REFS, ordered by JournalSequence. The refs filter is load-bearing - the client's own
			// quest catalog contributed 3,011 quests no capture ever showed, and listing those would put ids on the
			// wire that no capture supports.
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

				// tf1 is the quest's position in its line and tf2 the number of steps AFTER it; tf3 is 0 in every
				// captured block. Only the quests the snapshot BUILDS from carry the pair - the other 14 were harvested
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

		// After-quest world-object EFFECTS (#262): tag decor with the quest whose state changes its dynel look
		// (WorldObject.EffectQuestId/EffectDynelState, applied by WorldBroadcaster.ApplyQuestWorldEffects). The Tortage
		// beach "invisible wall" = the Casilda chain decor's collision, dropped (SimpleDynel visual-change state 5) once
		// the escort quest (12407) is active - data now, replacing the hard-coded DropBeachWall lookup. Idempotent.
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

		// Resolve a goal's `TargetName` to the stable `TargetTemplateId` (#521) - the id half of the pair described on
		// that column. A display name is ambiguous in the world (`Saddur` is two templates, `Halen van Dummy` 27), so
		// every trigger that matches a live entity wants the id and keeps the name only as a fallback.
		//
		// ⚠️ ORDER IS EVIDENCE STRENGTH again, and the three sources are genuinely different in kind:
		//
		//   1. the goal's OWN typed reference - `TrailingCategory` 51000 is `npc_template.name`, so `TrailingRefId` IS
		//      the target's template id, stated by the client. Direct evidence, and it is the only source that needs no
		//      string comparison at all. It never contradicts the name where both exist: measured over every such goal,
		//      6 agree and 0 disagree (the other 18 name a template no imported map holds - see below).
		//   2. the NAME, when exactly ONE catalog row carries it. 271 goals, and the bulk of the fill.
		//   3. the name plus the goal's OWN MAP, when several rows carry it. `MapId` cannot be a general match key - it
		//      is a marker pin and is NULL on 809 of these goals - but where it IS set it separates same-named
		//      templates cleanly, because they stand on different maps. Goal 7134 "Find the gate key" is the case that
		//      needs it: it names `Saddur`, carries no reference, and both Saddurs are candidates until its `MapId`
		//      4010 picks the Tortage Island one (9500) over the Stygian Galley one (2993). Its sibling goal 3490
		//      resolves by rule 1 to the SAME template, which is the cross-check that says the map reading is right.
		//   4. for a TALK goal only: the name plus the CONVERSATION catalog, when exactly one candidate has a
		//      conversation at all (#707). A `TalkToNpc` goal is completed by greeting the target, and greeting is
		//      driven by `ConversationEntries` - so a candidate with no conversation row cannot complete the goal on
		//      any evidence we hold, whatever retail meant. Where that leaves exactly one candidate standing, the
		//      choice is forced rather than guessed. Goal 4771 "Talk to Alyssa" is the case: two Alyssas (13457 City
		//      of Tortage, 19616 Old Tarantia), no reference and no map on the goal, and only 13457 is ever greeted
		//      in the corpus. It agrees with the quest's own placement - 12994 sits in questline 1308 "[NPE] Horror
		//      Beneath the Surface", whose map is 4569 Tortage Underhalls Day, i.e. the Tortage newbie chain, which
		//      the Old Tarantia Alyssa is nowhere near. ⚠️ Scoped to TALK goals on purpose: a KILL target has no
		//      reason to own a conversation, so the same test there would be an absence-of-evidence coin flip.
		//
		// Anything still ambiguous is left NULL and REPORTED, never guessed: a wrong template is worse than no template,
		// because the name fallback still works and a wrong id silently matches the wrong creature.
		//
		// ⚠️ A template we do not hold cannot be stored: `TargetTemplateId` is an FK, and an id with no row fails the
		// constraint. That is what caps the fill at 278 of 864 - the remaining goals name NPCs on maps that have not
		// been imported yet, INCLUDING 18 whose own 51000 reference we can read perfectly well. Those keep matching by
		// name and resolve themselves as the maps land, which is also why this rule is safe to re-run.
		//
		// BOOTSTRAP-ONLY on its own column: a goal that already has a template id (a hand pick settling an ambiguity,
		// or a prior run) is left alone.
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

			// Which maps each template stands on - the rule-3 discriminator. A template with no spawn contributes
			// nothing, so it can never be picked by map, which is correct: it is not on the goal's map either.
			var mapsOf = (await db.NpcSpawns.Select(s => new { s.NpcTemplateId, s.MapId }).Distinct().ToListAsync())
				.GroupBy(s => s.NpcTemplateId)
				.ToDictionary(g => g.Key, g => g.Select(s => s.MapId).ToHashSet());

			// Rule 4's discriminator: the templates the conversation catalog can actually greet.
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
					// resolves to nothing: this one is a KNOWN answer we cannot store yet, not an unresolvable string.
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

		// Configure one goal's SERVER-trigger fields (find-or-create by GoalId): the type + how it's matched at runtime
		// (Kill/LootFromNpc/TalkToNpc by NAME, Kill also by TrailingRefId template id, CollectItem by TrailingRefId item,
		// ReachArea by marker). All the WIRE/@609 display fields (markers, keys, counts) are left as harvested. `required`
		// is set ONLY when given - pass it for a hand-authored chain goal, omit it to PRESERVE the captured RequiredCount
		// (which is in the byte-exact objective region). Chain the quest separately with Chain().
		// BOOTSTRAP-ONLY (#262): only configures an UN-WIRED goal (still the fresh-harvest default, GoalType `None`), so a
		// re-import never clobbers trigger fields set by hand (the Quest editor) or a prior run. A newly-created goal is
		// un-wired, so a fresh import still bootstraps.
		private static async Task SeedGoal(DatabaseContext db, uint questId, uint goalId, QuestGoalType type, string? target = null, uint? questItemId = null,
			uint? required = null, bool gatesVisibility = false, bool staysDead = false)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null)
				return;

			var goal = quest.Goals.FirstOrDefault(g => g.GoalId == goalId);
			if (goal != null && IsWired(goal))
				return; // already configured (by hand or a prior seed) - don't overwrite

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
			// Editorial, and off by default: a one-time story target hides until its quest is active, and stays dead.
			// See QuestGoalDefinition.GatesTargetVisibility for why neither is ever derived from having a TargetName -
			// and #708 for why they are two calls: a quest-only target on a REPEATABLE quest is the first case that
			// wants the gate WITHOUT the one-time kill, and one column could not say it.
			goal.GatesTargetVisibility = gatesVisibility;
			goal.TargetStaysDead = staysDead;
			if (required is { } r)
				goal.RequiredCount = r;
			if (goal.Sequence == 0)
				goal.Sequence = 1;

			// NB the key item's Type (Quest 113) is NOT forced here - the `items` module types it from data: the chain
			// captures deliver the key via a live DispatchAddItem (0x2020) to the 0x71 quest container, which
			// ItemHashHarvester.ExtractFromAddItem reads as ItemType.Quest. Run the `items` module to (re)type it.

			await db.SaveChangesAsync();
			Console.WriteLine($"Seeded goal: quest {questId} goal {goalId} = {type}{(target != null ? $" '{target}'" : "")}.");
		}

		// Set a quest's chain SUCCESSOR (the QuestFullUpdate the supersession swaps to). nextQuestId null = chain end.
		// BOOTSTRAP-ONLY (#262): only sets an UN-chained quest (NextQuestId still null), so a re-import never clobbers a
		// successor set by hand (the Quest editor) or a prior run. A fresh capture-harvested quest has NextQuestId null,
		// so it still bootstraps; a chain-end seed (null) is a no-op on the already-null quest.
		//
		// ⚠️ Still needed: `NextQuestId` is NOT in the harvest. Measured with every seed absent (#705), all five chained
		// quests came back null - the supersession is visible in a capture as a sequence of events, not as a field.
		private static async Task Chain(DatabaseContext db, uint questId, uint? nextQuestId)
		{
			var quest = await db.QuestDefinitions.FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null || quest.NextQuestId != null)
				return;

			quest.NextQuestId = nextQuestId;
			await db.SaveChangesAsync();
		}

		// A goal is "wired" once something has given it a server TRIGGER - i.e. a Type* rule, a Seed* or the Quest editor
		// has configured it. Bootstrap-only seeds skip a wired goal so a re-import never reverts a hand edit.
		//
		// This is now a single field test, because the un-typed default is a real value: `GoalType.None` means "no
		// trigger" and nothing else (#522). It used to read `GoalType != Kill || TargetName != null || QuestItemId !=
		// null` - three clauses to work around `Kill` doubling as the default, where a hand-seeded KILL goal was only
		// distinguishable from an un-typed row by whether someone had also filled its TargetName.
		private static bool IsWired(QuestGoalDefinition goal)
		{
			return goal.GoalType != QuestGoalType.None;
		}
	}
}
