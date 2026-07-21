using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One goal of a quest as it appears in the snapshot's @609 objective-detail region (decoded:
	// tools/capture/decode_snapshot_609.py, codec QuestObjectiveRegion). Construct, don't replay: the numeric fields +
	// the two text refs (objective cat 50304, trailing cat 51200 remoteformat / 51000 localized) regenerate the
	// per-goal record byte-exact via QuestObjectiveGoal.BuildSingle.
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

		// For a LootFromNpc goal: the quest item (the "key") the TargetName NPC's corpse bag must contain, as an
		// Items.Id (#261). NULL = no specific item (the goal still completes on looting the bag - it matches on the
		// source NPC, not contents). When set, the bag-drop guarantees this item so the player can actually SEE and take
		// the key (e.g. Scavenger Tuut's bag -> 319224 "Scavenger's Key"). Data-driven: any quest's key item drops the same way.
		public uint? QuestItemId { get; set; }

		[ForeignKey(nameof(QuestItemId))]
		public virtual Item? QuestItem { get; set; }

		public uint SubId { get; set; } // the trailing record's id echoed in the header (= TrailingRefId)

		// The trailing text record: a <remoteformat category=51200> or a <localized category=51000 flags=..>.
		public uint TrailingRefId { get; set; }

		public bool TrailingLocalized { get; set; }

		// What server-side ACTION advances/completes this goal (Gitea #261). The wire output of a completion is
		// identical for every type (QuestProgressionService); the type only selects which trigger matches:
		//   Kill       - a kill of the NPC named TargetName (QuestProgressionService.AdvanceKillGoals).
		//   LootFromNpc- looting the corpse bag of the NPC named TargetName (LootService); the actual key item is a
		//                quest-specific RDB item we don't materialise, so the bag's SOURCE NPC name is the match key.
		//   TalkToNpc  - opening the greeting of the NPC named TargetName (ConversationService).
		//   Escort     - escorting the NPC named TargetName to a destination (not yet wired; Gitea sub-ticket).
		// Default Kill keeps the existing single-goal rows (seeded before this field) as kill goals.
		public QuestGoalType GoalType { get; set; } = QuestGoalType.Kill;

		[MaxLength(128)]
		public string? ObjectiveKey { get; set; }

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

		[MaxLength(64)]
		public string? TrailingFlags { get; set; }

		[MaxLength(128)]
		public string? TrailingKey { get; set; }

		[MaxLength(128)]
		public string? TrailingToken { get; set; }

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

		public uint Separator { get; set; } // the [1][k] separator's k word (9, 12 in the beach capture)
		public uint Sequence { get; set; } // the per-goal ordinal `N` (1, 2, ...)
		public uint TrailingCategory { get; set; }

		// Two of the "reserved" header words: 0 for most goals; the beach goal 2 carries a number + a 4-char tag
		// (70099 / 0x36314C56 = "61LV") here. Undecoded, carried per goal.
		// The reserved words' two (70099, random-4CC-token) pairs, at Reserved[1..2] and Reserved[5..6]. Stored, not
		// derived: the tags are random per entry.
		// The word after the marker block - "Zero1" in the codec until it turned out to be nonzero on 24 of the 158
		// captured goals. Stable per goal across every capture, so it is quest data.
		public uint Unknown1 { get; set; }

		// The second word after the marker block; "Zero2" in the codec, but nonzero on 318 captured goals.
		public uint Unknown2 { get; set; }

		// The marker coordinates, null exactly when MapId is - the three travel as one block on the wire.
		public uint? MarkerX { get; set; }
		public uint? MarkerY { get; set; }
	}
}
