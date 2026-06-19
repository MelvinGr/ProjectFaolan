using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// The journal CONTENT of a quest: the text refs + objective marker the server ships so the client's J-panel
	// renders a real title/description/objective. Harvested from captures by the importer (QuestDefinitionExtractor)
	// - construct, don't replay. Text is never literal: each ref is a (RefId, Category) the client resolves against
	// its RDB; KeyText is the deterministic per-(id,category) content hash (see docs/quest-log-handover.md).
	// CharacterQuest persists a character's STATE (active/progress); this is the shared, character-independent
	// definition looked up by the RDB quest id.
	public class QuestDefinition : BaseEntity
	{
		// The RDB quest id (e.g. 13479) - the stable lookup key; the synthetic Id is the row PK.
		public uint QuestId { get; set; }

		// The quest's region/area id (e.g. 1054), carried raw in both the journal and full-update packets.
		public uint AreaId { get; set; }

		// The primary objective's goal id (e.g. 0x1670 = 5744), echoed in QuestGoal and the full-update.
		public uint GoalId { get; set; }

		// The objective's world-marker (map id + x/y) shown on the map; 0 when the quest has no map marker.
		public uint MapId { get; set; }
		public uint MarkerX { get; set; }
		public uint MarkerY { get; set; }

		// --- Snapshot-tails GENERATION data (#186): values that live ONLY in the FullChar snapshot's quest region
		// (BeachTailsTemplate.bin), not the QuestFullUpdate/Journal packets, so they are seeded (not harvested). They
		// let the snapshot's Quest_c record + quest index generate from the DB with no literals.

		// Quest_c (journal) record scalars (still semantically undecoded; carried per quest). Beach 10238 = 4/1/0x96/15.
		public uint SnapshotN { get; set; }
		public uint SnapshotFieldEc { get; set; }
		public uint SnapshotFieldF0 { get; set; }
		public ulong SnapshotFieldF8 { get; set; }

		// The Quest_c record's State word (@20) and Field28 (@28) - per-quest CONSTANTS (verified byte-identical across
		// a quest's active + completed login captures, #255). Beach 10238 = state 1 / field28 0; Casilda 13479 = state 3
		// / field28 80. Drive the multi-quest journal region (BuildQuestPortion) so every held quest's record generates.
		public uint SnapshotState { get; set; }
		public uint SnapshotField28 { get; set; }

		// Quest index region: the W6QT goal-id table's marker + three lead words + trailer word, and the goal-step ids
		// (CSV, the client's 1..n goal index). Beach 10238: marker 0x7E2, fields 1/7/0, trailer 0x156E7, 8 steps.
		public uint IndexMarker { get; set; }
		public uint IndexTableField1 { get; set; }
		public uint IndexTableField2 { get; set; }
		public uint IndexTableField3 { get; set; }
		public uint IndexTrailerWord { get; set; }
		public string IndexGoalSteps { get; set; }

		public virtual ICollection<QuestTextRef> TextRefs { get; set; } = new List<QuestTextRef>();

		// The per-GOAL objective detail (the @609 region of the FullChar snapshot tails) - one row per goal of the
		// quest. A quest can have several goals each with its own world marker + objective text (the beach starter has
		// two: 1448 + 3490). Distinct from TextRefs (the journal header); these drive QuestObjectiveRegion generation
		// so the snapshot's objective region builds from the DB instead of BeachTailsTemplate.bin (#186).
		public virtual ICollection<QuestGoalDefinition> Goals { get; set; } = new List<QuestGoalDefinition>();
	}

	// One goal of a quest as it appears in the snapshot's @609 objective-detail region (decoded:
	// Captures/scripts/decode_snapshot_609.py, codec QuestObjectiveRegion). Construct, don't replay: the numeric fields +
	// the two text refs (objective cat 50304, trailing cat 51200 remoteformat / 51000 localized) regenerate the
	// per-goal record byte-exact via QuestObjectiveGoal.BuildSingle.
	public class QuestGoalDefinition : BaseEntity
	{
		[ForeignKey(nameof(QuestDefinitionId))]
		public virtual QuestDefinition Quest { get; set; }

		public uint QuestDefinitionId { get; set; }

		public uint GoalId { get; set; }
		public uint Sequence { get; set; } // the per-goal ordinal `N` (1, 2, ...)

		// The goal's required amount - how many kills/collects/etc. complete it. Decoded from the QuestFullUpdate /
		// snapshot @609 goal record (the word after goalIdRepeat; values 1/3/4/8/12/64 seen on the wire, #47). 0 = not
		// set, treated as 1 by QuestObjectiveRegion.Build (preserves byte-exact for rows seeded before this field).
		public uint RequiredCount { get; set; }

		// The NPC whose death advances this goal (a kill objective). SERVER-SIDE ONLY - NOT on the wire: the goal
		// record carries the count but not the target (it's in the RDB-resolved objective text). Sourced per quest by
		// capture correlation (the NPC hit at the QuestGoal-complete frame) + domain knowledge (#47, e.g. Casilda's
		// quest 13479 = kill "Scavenger Tuut"). CombatService.AdvanceKillQuests matches a kill on this exact name.
		// Null/empty = not a kill goal (talk/collect/reach) or target not yet sourced.
		public string TargetName { get; set; }

		public uint Separator { get; set; } // the [1][k] separator's k word (9, 12 in the beach capture)
		public uint SubId { get; set; }      // the trailing record's id echoed in the header (= TrailingRefId)
		public uint MapId { get; set; }
		public uint MarkerX { get; set; }
		public uint MarkerY { get; set; }

		// Two of the "reserved" header words: 0 for most goals; the beach goal 2 carries a number + a 4-char tag
		// (70099 / 0x36314C56 = "61LV") here. Undecoded, carried per goal.
		public uint Extra5 { get; set; }
		public uint Extra6 { get; set; }

		// The objective text (always a <remoteformat category=50304>).
		public uint ObjectiveRefId { get; set; }
		public uint ObjectiveCategory { get; set; }
		public string ObjectiveKey { get; set; }

		// The trailing text record: a <remoteformat category=51200> or a <localized category=51000 flags=..>.
		public uint TrailingRefId { get; set; }
		public uint TrailingCategory { get; set; }
		public string TrailingKey { get; set; }
		public bool TrailingLocalized { get; set; }
		public string TrailingFlags { get; set; }
	}

	// One text slot of a quest definition: a <remoteformat>/<localized> reference the client resolves by
	// (RefId, Category) against its RDB.
	public class QuestTextRef : BaseEntity
	{
		[ForeignKey(nameof(QuestDefinitionId))]
		public virtual QuestDefinition Quest { get; set; }

		public uint QuestDefinitionId { get; set; }

		public QuestTextSlot Slot { get; set; }

		public uint RefId { get; set; }
		public uint Category { get; set; }

		// The deterministic content hash from the capture (pre-escaped, e.g. contains &#36;). Render-critical:
		// the client validates it, a wrong key yields blank text.
		public string KeyText { get; set; }

		// True for <localized> slots (which carry Flags and have no closing tag); false for <remoteformat>.
		public bool IsLocalized { get; set; }

		// <localized flags="...">; null/empty for remoteformat slots.
		public string Flags { get; set; }
	}

	public enum QuestTextSlot
	{
		Title = 0,          // category 50301 - quest title line (journal + full-update)
		Summary = 1,        // category 50302 - quest summary (full-update)
		ListSubLine = 2,    // category 50303 - journal list sub-line
		Objective = 3,      // category 50304 - objective text (full-update)
		ObjectiveLocalized = 4, // <localized> objective detail (full-update)
		AreaTitle = 5,      // category 50300 - region/journal title (shared header)
		RegionName = 6,     // category 52000, id 4 - region name (shared header)
		MapName = 7         // category 52000, id = map id - map name (shared header)
	}
}
