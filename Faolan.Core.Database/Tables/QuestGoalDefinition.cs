using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One goal of a quest as it appears in the snapshot's @609 objective-detail region (decoded:
	// Captures/scripts/decode_snapshot_609.py, codec QuestObjectiveRegion). Construct, don't replay: the numeric fields +
	// the two text refs (objective cat 50304, trailing cat 51200 remoteformat / 51000 localized) regenerate the
	// per-goal record byte-exact via QuestObjectiveGoal.BuildSingle.
	public class QuestGoalDefinition : BaseEntity
	{
		public uint GoalId { get; set; }

		public uint MapId { get; set; }

		// The objective text (always a <remoteformat category=50304>).
		public uint ObjectiveRefId { get; set; }

		[ForeignKey(nameof(QuestDefinitionId))]
		public virtual QuestDefinition Quest { get; set; } = null!;

		public uint QuestDefinitionId { get; set; }

		// For a LootFromNpc goal: the quest item (the "key") the TargetName NPC's corpse bag must contain, as an
		// Items.Id (#261). 0 = no specific item (the goal still completes on looting the bag - it matches on the source
		// NPC, not contents). When set, the bag-drop guarantees this item so the player can actually SEE and take the key
		// (e.g. Scavenger Tuut's bag -> 319224 "Scavenger's Key"). Data-driven: any quest's key item drops the same way.
		public uint QuestItemId { get; set; }

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

		// Two of the "reserved" header words: 0 for most goals; the beach goal 2 carries a number + a 4-char tag
		// (70099 / 0x36314C56 = "61LV") here. Undecoded, carried per goal.
		public uint Extra5 { get; set; }
		public uint Extra6 { get; set; }
		public uint MarkerX { get; set; }
		public uint MarkerY { get; set; }
		public uint ObjectiveCategory { get; set; }

		// The goal's required amount - how many kills/collects/etc. complete it. Decoded from the QuestFullUpdate /
		// snapshot @609 goal record (the word after goalIdRepeat; values 1/3/4/8/12/64 seen on the wire, #47). 0 = not
		// set, treated as 1 by QuestObjectiveRegion.Build (preserves byte-exact for rows seeded before this field).
		public uint RequiredCount { get; set; }

		public uint Separator { get; set; } // the [1][k] separator's k word (9, 12 in the beach capture)
		public uint Sequence { get; set; } // the per-goal ordinal `N` (1, 2, ...)
		public uint TrailingCategory { get; set; }
	}
}
