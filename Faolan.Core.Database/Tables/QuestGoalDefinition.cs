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

		// For a LootFromNpc goal: the QUEST ITEM the TargetName NPC's corpse bag must contain, as an
		// Items.Id (#261). NULL = no specific item (the goal still completes on looting the bag - it matches on the
		// source NPC, not contents). When set, the bag-drop guarantees this item so the player can actually SEE and take
		// it (e.g. Scavenger Tuut's bag -> 319224 "Scavenger's Key"). Data-driven: any quest's item drops the same way.
		public uint? QuestItemId { get; set; }

		[ForeignKey(nameof(QuestItemId))]
		public virtual Item? QuestItem { get; set; }

		public uint SubId { get; set; } // the trailing record's id echoed in the header (= TrailingRefId)

		// The trailing text record: a <remoteformat category=51200> or a <localized category=51000 flags=..>.
		public uint TrailingRefId { get; set; }

		// What server-side ACTION advances/completes this goal (Gitea #261). The wire output of a completion is
		// identical for every type (QuestProgressionService); the type only selects which trigger matches:
		//   Kill       - a kill of the NPC named TargetName (QuestProgressionService.AdvanceKillGoals).
		//   LootFromNpc- looting the corpse bag of the NPC named TargetName (LootService); the actual quest item is a
		//                quest-specific RDB item we don't materialise, so the bag's SOURCE NPC name is the match key.
		//   TalkToNpc  - opening the greeting of the NPC named TargetName (ConversationService).
		//   Escort     - escorting the NPC named TargetName to a destination (not yet wired; Gitea sub-ticket).
		// Default Kill keeps the existing single-goal rows (seeded before this field) as kill goals.
		public QuestGoalType GoalType { get; set; } = QuestGoalType.Kill;

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
		public virtual ProtoDecryptionKey ObjectiveKey { get; set; } = new();

		// A text record addresses its string by numeric id OR by a name TOKEN (`token="Noname"`). Non-null selects the
		// token form; the RefId is then unused. Modelling only the id form rendered `id="0"` where the wire had a
		// token - 8 bytes short, and everything after it desynced.
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

		// TRUE when killing this goal's target is a ONE-TIME STORY event, which makes that NPC quest-GATED: it is
		// streamed only to a player holding this quest active, and it does not respawn once killed
		// (DatabaseRepository.GetQuestGatedNpcMap -> WorldEntity.GateQuestIds / NoRespawn, #261). Saddur and Scavenger
		// Tuut are the two, and retail solo-instances both.
		//
		// ⚠️ This used to be IMPLIED by "a Kill goal that has a TargetName", which held for the only two such goals ever
		// wired and is false in general (#521). Two reasons it had to become a column:
		//   - the gate keys on the NAME, so it hides EVERY same-named NPC from every player not on that quest;
		//   - it forces NoRespawn, so a goal asking for N kills is capped at however many spawns the world holds.
		// Quest 12057 asks for 9 Vanir Polluter kills and the world holds 8 spawns, so the implied rule turned the
		// mechanical TargetName fill this issue asks for into a PERMANENTLY uncompletable quest - a goal stuck at 8/9
		// with nothing left alive. Whether a kill target is a unique story NPC or a population mob is an editorial call
		// the client's data does not state anywhere we can read, so it is a hand-set column, never a rule.
		public bool GatesTargetVisibility { get; set; }

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
		public virtual ProtoDecryptionKey TrailingKey { get; set; } = new();

		[MaxLength(128)]
		public string? TrailingToken { get; set; }

		// The goal record's 7 "reserved" words, verbatim - a lead word then three optional (marker, ACGHash-token)
		// slots at [1..2]/[3..4]/[5..6], which refer to the goal's own target and NPC (#546; QuestObjectiveGoal.Reserved
		// carries the measured shape). Stored rather than derived because the token resolves to nothing we hold - the
		// client never dereferences it either (#547). The lead word [0] is undecoded - Gitea #550.
		//
		// Harvested from BOTH wire sources - the snapshot journal (ObjectiveGoalExtractor) and the live QuestFullUpdate
		// (QuestDefinitionExtractor, #537) - which agree on every goal seen by both.
		public uint Extra0 { get; set; }
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
		public uint Separator { get; set; }
		public uint Sequence { get; set; } // the per-goal ordinal `N` (1, 2, ...)
		public uint TrailingCategory { get; set; }

		// The word after the marker block - "Zero1" in the codec until it turned out to be nonzero on 24 of the 158
		// captured goals. Stable per goal across every capture, so it is quest data. (The two comment blocks that used
		// to sit above this one described the Extra* words; they are now on those properties.)
		public uint Unknown1 { get; set; }

		// The second word after the marker block; "Zero2" in the codec, but nonzero on 318 captured goals.
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
