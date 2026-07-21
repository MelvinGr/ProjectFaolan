using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// The journal CONTENT of a quest: the text refs + objective marker the server ships so the client's J-panel
	// renders a real title/description/objective. Harvested from captures by the importer (QuestDefinitionExtractor)
	// - construct, don't replay. Text is never literal: each ref is a (RefId, Category) the client resolves against
	// its RDB; KeyText is the deterministic per-(id,category) content hash.
	// CharacterQuest persists a character's STATE (active/progress); this is the shared, character-independent
	// definition looked up by the RDB quest id.
	// QuestId (the RDB quest id) IS the primary key - a natural-key catalog like NpcTemplate/Item/Spell/Map (#350),
	// set from the RDB by the importer and never DB-generated. No synthetic Id (BaseEntity dropped).
	public class QuestDefinition
	{
		// The quest's region/area id (e.g. 1054), carried raw in both the journal and full-update packets.
		public uint AreaId { get; set; }

		// The area this quest is grouped under. Every referenced area has a row: the importer inserts a placeholder
		// (Magic 0) for any area the captures never showed a completed quest in, the same way it does for Maps.
		[ForeignKey(nameof(AreaId))]
		public virtual QuestArea Area { get; set; } = null!;

		// The primary objective's goal id (e.g. 0x1670 = 5744), echoed in QuestGoal and the full-update.
		public uint GoalId { get; set; }

		// The objective's world-marker (map id + x/y) shown on the map; 0 when the quest has no map marker.
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The next stage in a multi-stage quest CHAIN (Gitea #261): completing this quest's goal SUPERSEDES it with
		// NextQuestId (QuestProgressionService sends QuestGoal + QuestIIR 2->4 + QuestFullUpdate(next) and sets the next
		// quest Active). null = no successor (a standalone quest, or the last stage of a chain). The Tortage intro chain
		// is 13479 (kill Tuut) -> 13563 (loot key) -> 13480 (free/talk Casilda) -> 12407 (escort) -> null.
		public uint? NextQuestId { get; set; }

		// The RDB quest id (e.g. 13479) - the primary key (natural key; see the class note).
		[Key]
		public uint QuestId { get; set; }

		// The per-GOAL objective detail (the @609 region of the FullChar snapshot tails) - one row per goal of the
		// quest. A quest can have several goals each with its own world marker + objective text (the beach starter has
		// two: 1448 + 3490). Distinct from TextRefs (the journal header); these drive QuestObjectiveRegion generation
		// so the snapshot's objective region builds from the DB instead of BeachTailsTemplate.bin (#186).
		public virtual ICollection<QuestGoalDefinition> Goals { get; set; } = new List<QuestGoalDefinition>();
		public virtual ICollection<QuestTextRef> TextRefs { get; set; } = new List<QuestTextRef>();

		// True once this quest's Quest_c record scalars are known (seeded, or harvested from a captured journal), i.e.
		// the record can be GENERATED. Previously inferred from `SnapshotState != 0` - but that word turned out to be
		// the quest LEVEL (it collapsed into Level), and plenty of definitions carry a level with no record scalars at
		// all, so the inference would have made them falsely eligible. An explicit flag says what was actually meant.
		public bool HasSnapshotRecord { get; set; }

		// True when the quest's @609 objective region, REBUILT from these rows, reproduces the captured bytes exactly.
		// Set by the importer, which does that comparison per quest at harvest time (ImportOps.VerifyObjectiveRegions).
		//
		// This is what gates a quest into the login snapshot. Shipping an objective region we cannot reproduce desyncs
		// the client and asserts it on statlist.cpp:134, and the region still has undecoded fields - so "we harvested
		// some goals" is not good enough, only "the bytes come out identical" is. The flag flips itself on as decoding
		// improves; no code change is needed to widen coverage.
		public bool ObjectiveRegionVerified { get; set; }

		[MaxLength(128)]
		public string? IndexGoalSteps { get; set; }

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

		// The quest's sequence word in the completed journal (#390). A property of the QUEST, not of its position:
		// measured over the corpus, all 1975 completed quests carry the SAME value everywhere they appear, across
		// characters and areas. It usually equals the 1-based ordinal within the area (73346 of 74392 entries), which
		// is why generating an ordinal reproduced 33 of 81 regions and failed the rest - 182 areas list every quest
		// with 0, and 260 more are mixed.
		//
		// Probably a chapter index within a quest series ("Letter to the King I / II"), but that is unverified, so the
		// name says what it is rather than what it likely means.
		public uint JournalSequence { get; set; }

		// The quest's LEVEL, carried in the QuestFullUpdate (0x63268258) as the u32 after the second questId - a quest
		// PROPERTY, constant across its lifecycle. (Earlier read as a "state/category" enum; the harvest across faolan.db
		// disproved that - the values track level, not state: Tortage starter quests 1/3/4/5, open-world zones 23/24,
		// endgame 80, and two quests in the SAME zone can be 23 and 80. It is never the QuestIIR StateActive value 2.)
		// Working interpretation: the quest's OPTIMAL level - what the J-panel colours the title against (red when the
		// character is under it, green at it, grey when over-levelled). The captured journals fit: Strie L1 -> 1,
		// AssassinL23 L23 -> 21/22/23/30, Hakotep L24 -> 22/23/24/30, DemoL80 L80 -> 70..80, NecroL80 L80 -> 80 across all 27
		// (her active set is all endgame). Note both L23 and L24 hold a quest at 30 - above them, so red. Not proven
		// from captures (the colour mapping is client-side), but nothing contradicts it.
		//
		// Harvested from TWO packets for the same quantity: the QuestFullUpdate's level word (QuestDefinitionExtractor,
		// #352) and the Quest_c journal record's word @+0x10. The latter used to live in a separate SnapshotState
		// column; they agree exactly where both were present (10238 -> 1/1, 13479 -> 3/3), so they were collapsed here.
		// 0 = never seen; the sender falls back to WorldQuest.DefaultQuestLevel.
		public uint Level { get; set; }
		public uint MarkerX { get; set; } // TODO: Vector2?
		public uint MarkerY { get; set; }

		// --- Snapshot-tails GENERATION data (#186): values that live ONLY in the FullChar snapshot's quest region
		// (BeachTailsTemplate.bin), not the QuestFullUpdate/Journal packets, so they are seeded (not harvested). They
		// let the snapshot's Quest_c record + quest index generate from the DB with no literals.

		// A level-ADJACENT value: across the captured journals it runs level..level+5 (NecroL80 L80 -> 80-83,
		// Hakotep L24 -> 19-30). Its exact meaning is undecoded, hence the name. Was called SnapshotN, which was
		// doubly wrong - it is not `n` (that is SnapshotUnknown14, a different word at a different offset).
		public uint SnapshotUnknown118 { get; set; }

		// The Quest_c record's `n` word (@+0x14) - the per-quest goal count. NOT the 0x412 the beach template happens
		// to carry: captured journals vary (NecroL80's records run 2, 10, ...). QuestcRecord.BuildSingle used to emit the
		// beach constant for every quest, which is fine while the only generated journal IS the beach one, and wrong
		// the moment a real character's journal is regenerated - so it is stored per quest. Beach 10238 keeps 0x412,
		// so its golden stays byte-exact.
		public uint SnapshotUnknown14 { get; set; }
		public uint SnapshotUnknown28 { get; set; }
		public uint SnapshotUnknownEc { get; set; }
		public uint SnapshotUnknownF0 { get; set; }

		public ulong SnapshotUnknownF8 { get; set; }
	}

	// The server-side trigger that advances a quest goal (Gitea #261). Persisted as an int; ordering is stable.
	public enum QuestGoalType
	{
		Kill = 0, // kill the TargetName NPC
		LootFromNpc = 1, // loot the TargetName NPC's corpse bag (the quest item is RDB content; the bag source is the key)
		TalkToNpc = 2, // talk to (open the greeting of) the TargetName NPC
		Escort = 3, // escort the TargetName NPC to a destination (not yet wired)
		ReachArea = 4, // reach a map location - solved when the player moves within range of the goal's (MapId, MarkerX, MarkerY) point (no TargetName; e.g. Shipwrecked's "Enter the jungle", #262)
		CollectItem = 5 // collect N of the item in TrailingRefId, one per interaction with a quest-pickup world object (QuestItemEffect); e.g. Shipwrecked V's "get 3 stone bricks" (item 193400), #262
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
