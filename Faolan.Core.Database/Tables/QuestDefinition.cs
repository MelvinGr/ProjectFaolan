using System.ComponentModel.DataAnnotations;
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
		// The per-GOAL objective detail (the @609 region of the FullChar snapshot tails) - one row per goal of the
		// quest. A quest can have several goals each with its own world marker + objective text (the beach starter has
		// two: 1448 + 3490). Distinct from TextRefs (the journal header); these drive QuestObjectiveRegion generation
		// so the snapshot's objective region builds from the DB instead of BeachTailsTemplate.bin (#186).
		public virtual ICollection<QuestGoalDefinition> Goals { get; set; } = new List<QuestGoalDefinition>();
		public virtual ICollection<QuestTextRef> TextRefs { get; set; } = new List<QuestTextRef>();

		[MaxLength(128)]
		public string IndexGoalSteps { get; set; }

		// The quest's region/area id (e.g. 1054), carried raw in both the journal and full-update packets.
		public uint AreaId { get; set; }

		// The primary objective's goal id (e.g. 0x1670 = 5744), echoed in QuestGoal and the full-update.
		public uint GoalId { get; set; }

		// Quest index region: the W6QT goal-id table's marker + three lead words + trailer word, and the goal-step ids
		// (CSV, the client's 1..n goal index). Beach 10238: marker 0x7E2, fields 1/7/0, trailer 0x156E7, 8 steps.
		// IndexMagic = the per-quest goal-table NAME word (beach = 0x57365154 "W6QT"; Casilda 13479 = 0x52524C33
		// "RRL3"); in a multi-quest index each held quest contributes its own block keyed by this (#255).
		public uint IndexMagic { get; set; }
		public uint IndexMarker { get; set; }
		public uint IndexTableField1 { get; set; }
		public uint IndexTableField2 { get; set; }
		public uint IndexTableField3 { get; set; }
		public uint IndexTrailerWord { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		// The objective's world-marker (map id + x/y) shown on the map; 0 when the quest has no map marker.
		public uint MapId { get; set; }
		public uint MarkerX { get; set; }
		public uint MarkerY { get; set; }

		// The next stage in a multi-stage quest CHAIN (Gitea #261): completing this quest's goal SUPERSEDES it with
		// NextQuestId (QuestProgressionService sends QuestGoal + QuestIIR 2->4 + QuestFullUpdate(next) and sets the next
		// quest Active). 0 = no successor (a standalone quest, or the last stage of a chain). The Tortage intro chain is
		// 13479 (kill Tuut) -> 13563 (loot key) -> 13480 (free/talk Casilda) -> 12407 (escort) -> 0.
		public uint NextQuestId { get; set; }

		// The RDB quest id (e.g. 13479) - the stable lookup key; the synthetic Id is the row PK.
		public uint QuestId { get; set; }
		public uint SnapshotField28 { get; set; }
		public uint SnapshotFieldEc { get; set; }
		public uint SnapshotFieldF0 { get; set; }

		// --- Snapshot-tails GENERATION data (#186): values that live ONLY in the FullChar snapshot's quest region
		// (BeachTailsTemplate.bin), not the QuestFullUpdate/Journal packets, so they are seeded (not harvested). They
		// let the snapshot's Quest_c record + quest index generate from the DB with no literals.

		// Quest_c (journal) record scalars (still semantically undecoded; carried per quest). Beach 10238 = 4/1/0x96/15.
		public uint SnapshotN { get; set; }

		// The Quest_c record's State word (@20) and Field28 (@28) - per-quest CONSTANTS (verified byte-identical across
		// a quest's active + completed login captures, #255). Beach 10238 = state 1 / field28 0; Casilda 13479 = state 3
		// / field28 80. Drive the multi-quest journal region (BuildQuestPortion) so every held quest's record generates.
		public uint SnapshotState { get; set; }
		public ulong SnapshotFieldF8 { get; set; }
	}

	// The server-side trigger that advances a quest goal (Gitea #261). Persisted as an int; ordering is stable.
	public enum QuestGoalType
	{
		Kill = 0, // kill the TargetName NPC
		LootFromNpc = 1, // loot the TargetName NPC's corpse bag (the quest item is RDB content; the bag source is the key)
		TalkToNpc = 2, // talk to (open the greeting of) the TargetName NPC
		Escort = 3 // escort the TargetName NPC to a destination (not yet wired)
	}

	public enum QuestTextSlot
	{
		Title = 0, // category 50301 - quest title line (journal + full-update)
		Summary = 1, // category 50302 - quest summary (full-update)
		ListSubLine = 2, // category 50303 - journal list sub-line
		Objective = 3, // category 50304 - objective text (full-update)
		ObjectiveLocalized = 4, // <localized> objective detail (full-update)
		AreaTitle = 5, // category 50300 - region/journal title (shared header)
		RegionName = 6, // category 52000, id 4 - region name (shared header)
		MapName = 7 // category 52000, id = map id - map name (shared header)
	}
}
