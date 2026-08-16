using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	// One goal of a quest as it appears in the snapshot's @609 objective-detail region (decoded:
	// tools/capture/decode_snapshot_609.py, codec QuestObjectiveRegion). Construct, don't replay: the numeric fields +
	// the two text refs regenerate the per-goal record byte-exact via QuestObjectiveGoal.BuildSingle.
	//
	// The two refs are TYPED LDB references, and the client's own category table names them (see
	// docs/rdb/ldb-string-index.md): the OBJECTIVE ref is always category 50304 `quest_goal_templates.goal_name` -
	// the goal's display text, which is ENCRYPTED and so unreadable (#520) - while the TRAILING ref names the goal's
	// TARGET and is plaintext, its category saying what kind of thing it points at: 50200 `item.name`, 51000
	// `npc_template.name`, 51100 `categorytree.description`, 51200 `bounded_areas.bounded_area`.
	public class QuestGoalDefinition
	{
		// The goal's wire objective id - globally unique, so it is the PK (no synthetic Id / BaseEntity dropped). It is
		// what CharacterQuestGoal.GoalId FKs, and what QuestGoal echoes on the wire.
		// ⚠️ DatabaseGenerated(None) is load-bearing. Without it EF's convention treats an integer [Key] as
		// store-generated, so adding a goal that already carries its wire id is taken to be an EXISTING row: EF marks
		// it Modified and emits an UPDATE, which affects 0 rows and throws DbUpdateConcurrencyException mid-import.
		// The id comes from the wire and is never generated here.
		[Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
		public uint GoalId { get; set; }

		// The designers' name for the goal, in plaintext, from the client's quest catalog (RDB 1000104,
		// `QuestGoal.name`; see docs/rdb/quest-catalog-1000104.md) - "Kill the Scavenger", "Enter the jungle".
		// It is the SAME sentence the encrypted LDB category 50304 carries, but free: no page, no captured key. That
		// makes it a useful cross-check on `--report ldb-quest-text` as well as content in its own right, and it
		// covers all 7301 catalog goals against the 168 the corpus decrypts. Null for a goal the catalog omits.
		[MaxLength(256)]
		public string? Name { get; set; }

		// The goal's phase within its quest - `QuestGoal.phase`, populated on every catalog row. 1-based, and it is
		// the catalog's ordering of a quest's goals (the beach starter's "Enter the jungle" is phase 1 and "Kill
		// slavemaster Saddur" phase 2). 0 for a goal the catalog omits.
		//
		// ⚠️ THIS is the play order, not `Sequence` (#527) - see `QuestProgress.InPlayOrder`, which is how the runtime
		// must ask for it. The two disagree on exactly one shipped quest, 12369 "The Acheronian Gate", and the corpus
		// backs the phase: retail's `Advanced` hand-off is only sent on a goal that has a SUCCESSOR, and g7134 "Find
		// the gate key" (phase 1, `Sequence` 2) is the one that takes it. It is also the field that orders the 7,147
		// goals which come from the catalog and were never on the wire, so carry no `Sequence` at all.
		public uint Phase { get; set; }

		// The map the goal's objective/marker sits on (all goal maps are in the Maps catalog).
		// NULL when the goal has no world marker. 449 of the 2850 captured @609 goals are like this, and mapId==0
		// coincides with markerX==markerY==0 in EVERY one of them (zero counter-examples), so it is the wire's way of
		// saying "no marker" rather than a missing value. Modelled nullable rather than 0 because the column is an FK:
		// a 0 points at no Maps row and fails the constraint outright.
		public uint? MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map? Map { get; set; }

		// The objective text (always a <remoteformat category=50304>).
		public uint ObjectiveRefId { get; set; }

		[ForeignKey(nameof(QuestDefinitionId))]
		public virtual QuestDefinition Quest { get; set; } = null!;

		public uint QuestDefinitionId { get; set; }

		// The Items.Id of the QUEST ITEM this goal is about - read per GoalType, and it is the goal's own SERVER-side
		// statement of which item, independent of the wire's trailing reference.
		//
		//   LootFromNpc  the item the TargetName NPC's corpse bag must contain (#261). NULL = no specific item (the
		//                goal still completes on looting the bag - it matches on the source NPC, not contents). When
		//                set, the bag-drop guarantees it so the player can SEE and take it (Scavenger Tuut's bag ->
		//                319224 "Scavenger's Key").
		//   TalkToNpc    the item HANDED OVER (#704). A hand-in is triggered by the talk, so it is typed TalkToNpc, and
		//                this is what makes it a DELIVERY rather than an ordinary talk goal - see
		//                QuestProgress.IsDelivery, which reads exactly that pair. It is what the player must be
		//                carrying (CanHandIn) and what is taken when the hand-in goes through.
		//
		// Why the second reading had to exist: a captured delivery names its item in the WIRE record
		// (`Separator` = Deliver + a 50200 trailing reference), and the 7,147 goals that come from the client's quest
		// CATALOG carry no kind tag and no reference at all - only the objective sentence. Resolving that sentence
		// ("Give the Crate of Steel to Turach" -> Items 31055) has to land somewhere, and inventing a wire field would
		// be a lie about what the capture showed. This column is server-side, so it is the honest home.
		//
		// Data-driven either way: any quest's item drops, or is handed over, the same way.
		public uint? QuestItemId { get; set; }

		[ForeignKey(nameof(QuestItemId))]
		public virtual Item? QuestItem { get; set; }

		// The WORLD OBJECT this goal is about - the door, gate or lever a `UseWorldObject` goal is solved by USING
		// (#527). SERVER-SIDE ONLY, like `TargetName` and `TargetTemplateId` beside it, and for the same reason: the
		// goal record names its target nowhere on the wire.
		//
		// ⚠️ It could NOT reuse `TrailingRefId`, which is where an Escort goal keeps its escortee and a UseItem goal
		// its item. That field is part of the byte-exact @609 record, and goal 3492's is 0 in the capture - writing a
		// world-object id into it would put bytes on the wire retail never sent, to carry a fact retail never puts
		// there. A server-side column is the honest home for a server-side resolution.
		//
		// Points at `WorldObject.Id`, the importer's own derived key, so it survives a re-harvest (see DerivedKey).
		public uint? TargetWorldObjectId { get; set; }

		[ForeignKey(nameof(TargetWorldObjectId))]
		public virtual WorldObject? TargetWorldObject { get; set; }

		public uint SubId { get; set; } // the trailing record's id echoed in the header (= TrailingRefId)

		// The trailing text record: a <remoteformat category=51200> or a <localized category=51000 flags=..>.
		public uint TrailingRefId { get; set; }

		// What server-side ACTION advances/completes this goal (Gitea #261). The wire output of a completion is
		// identical for every type (QuestProgressionService); the type only selects which trigger matches:
		//   Kill       - a kill of the NPC named TargetName (QuestProgressionService.AdvanceKillGoals).
		//   LootFromNpc- looting the corpse bag of the NPC named TargetName (LootService); the actual quest item is a
		//                quest-specific RDB item we don't materialise, so the bag's SOURCE NPC name is the match key.
		//   TalkToNpc  - opening the greeting of the NPC named TargetName (ConversationService).
		//   Escort     - escorting the NPC in TrailingRefId to a destination (#646).
		//
		// The default is `None` - NO trigger - and that is load-bearing (#522). It used to be `Kill`, so an un-typed row
		// claimed to be a kill goal: 7,148 catalog goals with no verb and no target sat there, and every guard in
		// QuestProgress.MatchingKillGoal was added to stop one of them false-firing. A goal becomes a kill goal by
		// carrying the CLIENT's kill verb (`Separator` = QuestGoalKind.Kill), not by nobody having typed it.
		public QuestGoalType GoalType { get; set; } = QuestGoalType.None;

		// TRUE when this row came from MID-SESSION traffic (a QuestFullUpdate) instead of a login snapshot's Quest_c
		// record. So the row exists to be DISPLAYED, and must not be EVALUATED: it has no TARGET.
		//
		// ⚠️ CORRECTED in #548. This used to say the mid-session packet carries "never the verb (`Separator`), the
		// target, or the required count". It carries the verb and the count for EVERY goal, and both are harvested
		// now - measured against the snapshot journal, the source thought to hold them alone: of the 34 goals the
		// corpus shows in BOTH, all 34 agree on both fields. What a full-update genuinely does not carry is the
		// TARGET, and it never did: `TargetName` is not on the wire in either source (see its own comment) - it is
		// sourced by capture correlation and domain knowledge.
		//
		// ⚠️ Still load-bearing, for the narrower reason: with no `TargetName` a Kill/LootFromNpc/TalkToNpc goal has
		// nothing for a trigger to match, so nothing can ever advance it, and AllGoalsComplete requires EVERY goal of
		// a quest - which would leave its quest permanently incompletable. QuestProgress.AllGoalsComplete skips these
		// for exactly that reason (#525 follow-up).
		//
		// Why they are worth having anyway: without them a two-goal quest silently looks like a one-goal quest. The
		// wire proves 12369 (7134 -> 3492) and 10287 (400 -> 7363) each have two, and both missing goals appear in
		// the corpus ONLY mid-session, because their quests were completed between login snapshots.
		public bool Partial { get; set; }

		public bool TrailingLocalized { get; set; }

		// The per-string LDB DECRYPTION KEY for the objective text (LdbKey) - stored as the 128-bit TEA key, the same
		// ProtoDecryptionKey shape an item template's key uses, rather than as the 20-character wire string. The
		// string is a pure base-85 rendering of these four words and regenerates from them exactly (LdbKey.ToWire),
		// so nothing is lost; it is never a key or a lookup here, only payload written into the outgoing attribute.
		// An all-zero key means the capture carried none - see ProtoDecryptionKey.IsEmpty.
		public virtual ProtoDecryptionKey ObjectiveKey { get; set; } = new ProtoDecryptionKey();

		// A text record addresses its string by numeric id OR by a name TOKEN (`token="Noname"`). Non-null selects the
		// token form; the RefId is then unused. Modelling only the id form rendered `id="0"` where the wire had a
		// token - 8 bytes short, and everything after it desynced.
		//
		// ⚠️ NULL on all 7,317 shipped rows, which is NOT evidence the column is dead (#646). The token form is proven
		// live on the SIBLING slot - `TrailingToken` is set on 52 rows, harvested from the same records by the same
		// code - so the alternative is real in the grammar and this slot simply never takes it in the corpus we have.
		// Dropping it would re-introduce the desync above the first time one does.
		[MaxLength(128)]
		public string? ObjectiveToken { get; set; }

		// The NPC this goal is ABOUT, interpreted per GoalType: the NPC to kill (Kill), whose corpse bag to loot
		// (LootFromNpc), or to talk to (TalkToNpc / Escort). SERVER-SIDE ONLY - NOT on the wire: the goal record carries
		// the count but not the target (it's in the RDB-resolved objective text). Sourced per quest by capture
		// correlation (the NPC at the QuestGoal-complete frame) + domain knowledge (#47/#261, e.g. Casilda's chain
		// 13479 = kill "Scavenger Tuut", 13563 = loot "Scavenger Tuut"'s bag, 13480 = talk "Casilda").
		// Null/empty = target not yet sourced.
		[MaxLength(128)]
		public string? TargetName { get; set; }

		// The NPC TEMPLATE this goal is about - the same target as `TargetName`, resolved to the stable id (#521).
		// RESOLVED, not harvested: only 6 goals carry a `51000` reference we can read it off directly, and the other
		// 272 get it by looking their name up in the catalog (ImportOps.ResolveGoalTargetTemplates). It adds no
		// information
		// - it FREEZES a lookup, which is the point: the ambiguity is settled once, at import, with the goal's own
		// evidence to hand, instead of being re-guessed by every matcher from a string.
		//
		// Prefer this over `TargetName` wherever a trigger matches a live entity: a template is exactly one kind of
		// NPC, while a display name is ambiguous in the world - `Saddur` is TWO templates (2993 on the Stygian Galley,
		// 9500 on Tortage Island) and `Halen van Dummy` is 27. That is not hypothetical: keying the quest-gate map on
		// the name hid the GALLEY Saddur from every player not holding a Tortage Island quest, because goal 3490 flags
		// the beach one. Same move `ConversationNode` made in #646, for the same reason.
		//
		// ⚠️ NULL on 586 of the 864 goals that name a target, and that is NOT a resolution failure to fix by relaxing
		// the rule - the world data is simply INCOMPLETE. Those goals name NPCs (`Genzio`, `Captain Sakumbe`,
		// `Mithrelle`) on maps that have not been imported yet, so there is no `NpcTemplates` row to point at and an FK
		// cannot hold a dangling id. They keep matching by NAME and light up on their own as those maps land.
		//
		// So `TargetName` STAYS, and is not deprecated by this: it is the fallback for those 586, the only key a
		// "kill N of <group>" goal can use (a group has no single template), and the human-readable label in reports.
		// The pair is exactly the arrangement `MatchingKillGoal` already had via `TrailingRefId` - id first, name
		// second - now available to the talk and loot paths too.
		public uint? TargetTemplateId { get; set; }

		[ForeignKey(nameof(TargetTemplateId))]
		public virtual NpcTemplate? TargetTemplate { get; set; }

		// TRUE when this goal's target is streamed ONLY to a player holding this quest active
		// (DatabaseRepository.GetQuestGatedNpcMap -> WorldEntity.GateQuestIds, #261). Saddur and Scavenger Tuut are
		// the two, and retail solo-instances both.
		//
		// ⚠️ This used to be IMPLIED by "a Kill goal that has a TargetName", which held for the only two such goals ever
		// wired and is false in general (#521). Two reasons it had to become a column:
		//   - the gate keys on the NAME, so it hides EVERY same-named NPC from every player not on that quest;
		//   - it forced NoRespawn, so a goal asking for N kills is capped at however many spawns the world holds.
		// Quest 12057 asks for 9 Vanir Polluter kills and the world holds 8 spawns, so the implied rule turned the
		// mechanical TargetName fill this issue asks for into a PERMANENTLY uncompletable quest - a goal stuck at 8/9
		// with nothing left alive. Whether a kill target is a unique story NPC or a population mob is an editorial call
		// the client's data does not state anywhere we can read, so it is a hand-set column, never a rule.
		public bool GatesTargetVisibility { get; set; }

		// TRUE when killing this goal's target is a ONE-TIME STORY event, so the NPC does not come back
		// (-> WorldEntity.NoRespawn). Both of the two flagged goals want this AND the visibility gate above, which is
		// why one column used to answer both (#708) - but they are independent properties of an NPC and the weld
		// cannot express the first case that wants only one:
		//   - a REPEATABLE or retryable quest whose target is quest-only: gated, but it has to respawn;
		//   - a story kill target that everyone can see: stays dead, but not hidden.
		// Under the weld, taking the gate forced NoRespawn and dropping it made the NPC visible to everyone; neither
		// combination could be written down. Split with both set on the existing two, so behaviour is unchanged.
		//
		// ⚠️ Being a story kill target is why the gate is safe here and is NOT the same statement: `GetQuestGatedNpcMap`
		// reveals a gated NPC to EVERY quest that targets it (#574 - killing Tuut completes the only quest that was
		// revealing him, so his looted-for key became unobtainable), while this flag is read per target with no such
		// widening. Editorial, like its neighbour: the client's data states neither anywhere we can read.
		public bool TargetStaysDead { get; set; }

		// A PRECONDITION the client states in the goal's own objective text, decoded with it (#520). Two shapes exist in
		// the corpus and both are recorded even though only one can be enforced yet:
		//
		//   "Talk to Sharak (level 30)"                                     -> RequiredLevel 30
		//   "…to Itsing When You Have Become an Acolyte (Rank 1)"           -> RequiredRankTitle "Acolyte", RequiredRank 1
		//
		// ⚠️ These are recorded RATHER THAN acted on at wiring time deliberately. A goal with an unmet precondition must
		// not advance - a bare talk trigger would complete it EARLY, a silent chain bug - so the goal is wired normally
		// and the gate is enforced at the TRIGGER (QuestProgress.PreconditionMet), which is the one place that knows the
		// live player. That means the day faction ranks exist (#310), only the rank branch of that predicate changes;
		// nothing has to be re-harvested and no goal has to be re-wired.
		public uint? RequiredLevel { get; set; }

		// The faction TITLE the client names ("Acolyte", "Pickpocket", "Thug"), verbatim. A string rather than an FK
		// because the faction system does not exist yet (#310) - when it does, this is what resolves to it.
		[MaxLength(64)]
		public string? RequiredRankTitle { get; set; }

		public uint? RequiredRank { get; set; }

		[MaxLength(64)]
		public string? TrailingFlags { get; set; }

		// The trailing text record's LDB decryption key; see ObjectiveKey.
		public virtual ProtoDecryptionKey TrailingKey { get; set; } = new ProtoDecryptionKey();

		[MaxLength(128)]
		public string? TrailingToken { get; set; }

		// The goal record's reserved words [1..6] - three optional (marker, ACGHash-token) slots at [1..2]/[3..4]/[5..6],
		// which refer to the goal's own target and NPC (#546; QuestObjectiveGoal.Reserved carries the measured shape).
		// Stored rather than derived because the token resolves to nothing we hold - the client never dereferences it
		// either (#547).
		//
		// ⚠️ The numbering is the WIRE's, and it starts at 1 because reserved[0] IS NOT DEFINITION DATA: it is
		// `QuestGoal_c::SolvedTimes`, the goal's per-character PROGRESS counter (#550). It used to be harvested here as
		// `Extra0`, which shipped one retail character's half-finished objectives to every player. It now comes from
		// the live character through QuestObjectiveRegions.ForQuest's `solvedTimesOf`.
		//
		// Harvested from BOTH wire sources - the snapshot journal (ObjectiveGoalExtractor) and the live QuestFullUpdate
		// (QuestDefinitionExtractor, #537) - which agree on every goal seen by both.
		public uint Extra1 { get; set; }
		public uint Extra2 { get; set; }
		public uint Extra3 { get; set; }
		public uint Extra4 { get; set; }
		public uint Extra5 { get; set; }
		public uint Extra6 { get; set; }

		// The word echoing the goal id - EITHER the goal id or 0, stable per goal but not derivable from it (1567 of
		// the 2850 captured goals carry 0, 1283 carry the id).
		public uint GoalIdEcho { get; set; }
		public uint ObjectiveCategory { get; set; }

		// The goal's position in the @609 objective region. NOT derivable from Sequence: several quests carry two
		// goals both with N=1 (12068, 12057), so ordering by Sequence is ambiguous and produced the goals in the
		// wrong order - quest 12057's wire order is [3542,2949] while Sequence gives [2949,3542].
		public uint RegionOrder { get; set; }

		// The goal's required amount - how many kills/collects/etc. complete it. Decoded from the QuestFullUpdate /
		// snapshot @609 goal record (the word after goalIdRepeat; values 1/3/4/8/12/64 seen on the wire, #47). 0 = not
		// set, treated as 1 by QuestObjectiveRegion.Build (preserves byte-exact for rows seeded before this field).
		public uint RequiredCount { get; set; }

		// The [1][k] separator's k word - and it is the goal's KIND TAG, not a framing artefact: see QuestGoalKind for
		// the decode and its evidence. Kept under its wire-derived name because it is a captured field.
		//
		// NULL = this goal has never been seen on the wire, so it has no kind - which is every goal that comes from
		// the client's quest catalog (RDB 1000104) rather than a capture. Nullable rather than 0 because THERE IS NO
		// KIND 0 (see QuestGoalKind): 0 was the un-harvested default that #548 removed, and reusing it for "never
		// captured" would resurrect exactly the ambiguity that bug had. `SeedContentTests
		// .NoGoalCarriesTheUnharvestedKindZero` still fails on a literal 0, so a real harvest miss is still caught.
		public uint? Separator { get; set; }
		// The per-goal ordinal `N` the wire carries (1, 2, ...) - a CAPTURED VALUE, written straight back out by
		// QuestObjectiveRegions and byte-exact verified.
		//
		// ⚠️ It is NOT the play order, and using it as one is a live trap (#527). Five shipped quests carry `N` = 1 on
		// EVERY goal - 14020, 16004, 16183, 16888, 17350 - because their objectives are PARALLEL ("collect all seven
		// faction documents", "kill all three", "use all five stones on the standing stone") and retail has no order
		// to state. Renumbering them 1..n to make them sortable rewrites bytes retail sent, and
		// `CompletedQuestJournalGenerationTests` fails on it at once. The play order is `Phase`; ask for it through
		// `QuestProgress.InPlayOrder`.
		public uint Sequence { get; set; }
		public uint TrailingCategory { get; set; }

		// The word after the marker block - "Zero1" in the codec until it turned out to be nonzero on 24 of the
		// captured goals. Stable per goal across every capture, so it is quest data. (The two comment blocks that used
		// to sit above this one described the Extra* words; they are now on those properties.)
		//
		// ⭐ **It only ever appears on COLLECT and KILL goals.** Measured over the 171 goals that carry a `Separator`
		// (the captured ones - scoping to those matters, since an unharvested row reads 0 and would make the test
		// vacuous):
		//
		//     sep 23 Collect   44 goals, 14 carry it
		//     sep 12 Kill      30 goals, 10 carry it
		//     the other 7 kinds (22, 20, 25, 5, 9, 24, 10)   97 goals, ZERO carry it
		//
		// Those two kinds are exactly the ones where a quest item comes off a mob, which makes a per-goal DROP RATE
		// the obvious reading - and we model no such thing. ⚠️ Unproven, and the magnitudes do not settle it: the
		// values are 3, 10, 20, 25, 40, 50 (x11), 60, 80, 90, 100, 105, 130, 150, 170, so a plain percentage would
		// have four goals above 100%. Tenths of a percent (0.3%..17%) fits the ladder, a per-kill weight allowing
		// multiple drops fits too. It does NOT track RequiredCount (count 64 and count 1 both carry 50).
		// [V0: what QuestGoalDefinition.Unknown1 scales on a Collect/Kill goal]
		public uint Unknown1 { get; set; }

		// The second word after the marker block; "Zero2" in the codec. The client writes it as the BYTE 0x01
		// followed by three zero bytes, so every set value reads 0x01000000 as a word - see QuestObjectiveGoalCodec.
		//
		// It is a flag, and what it flags looks like EXPANSION content: all 17 goals that carry it sit on quests
		// 15378-17774 and are Khitai/RoTG by name (Xibaluku, the Jade Artifact, Kian Lai, Lao-che, Kun Whu, Po-Sha,
		// Zhu-la, Balbet-Zhun, Blood Ravine, the Emperor's Manifestation, the Pods). ⚠️ Necessary, not sufficient -
		// only 17 of the 3,464 goals on quests >= 15000 carry it, so "expansion" cannot BE the definition, exactly as
		// with QuestDefinition.QuestLineFlags bit 4. [V0: what QuestGoalDefinition.Unknown2 flags]
		public uint Unknown2 { get; set; }

		// The marker coordinates, null exactly when MapId is - the three travel as one block on the wire.
		//
		// ⚠️ The marker is a MAP PIN, not a trigger. For a ReachArea goal the real volume is `Region` below, and the
		// two are not the same shape: goal 1448's marker (416, 701) sits inside `Trappers Haven`, a 96 x 82 region,
		// while the marker-radius fallback tests a 40-unit circle centred on the pin.
		public uint? MarkerX { get; set; }
		public uint? MarkerY { get; set; }

		// The client's trigger volume for this goal (#521), resolved from the typed reference the goal already
		// carries: `TrailingCategory` 51200 is `bounded_areas.bounded_area`, so LDB[51200][TrailingRefId] is the
		// region's NAME and that names a region in the playfield's RDB 1000053 record. Verified end to end on the
		// beach starter - goal 1448 -> LDB[51200][180] = "Trappers Haven" -> a 7-vertex region on map 4010 that
		// contains the goal's own marker.
		//
		// Null for every goal that carries no 51200 reference (only 3 in the whole catalog do), and those keep the
		// marker-radius test. SetNull so dropping a region degrades the goal rather than deleting it.
		//
		// ⚠️ A 51200 reference does NOT by itself make a goal a ReachArea - goal 11712 has RequiredCount 3, so its
		// region scopes the objective ("3 of something in there") instead of being it. Settling that needs the
		// goal's own objective text, which is in ENCRYPTED LDB category 50304 (#520). So this column is the
		// geometry only; GoalType stays whatever the wiring says.
		public uint? RegionId { get; set; }

		[ForeignKey(nameof(RegionId))]
		public virtual MapRegion? Region { get; set; }
	}
}
