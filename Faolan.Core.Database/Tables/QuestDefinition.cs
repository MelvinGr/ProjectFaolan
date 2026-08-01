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

		// Hand-reviewed: the importer must not overwrite this row's fields. Set from the editor, NEVER by an import.
		// See ConversationNode.Verified - same contract, same `--force` escape.
		public bool Verified { get; set; }

		// The per-GOAL objective detail (the @609 region of the FullChar snapshot tails) - one row per goal of the
		// quest. A quest can have several goals each with its own world marker + objective text (the beach starter has
		// two: 1448 + 3490). Distinct from TextRefs (the journal header); these drive QuestObjectiveRegion generation
		// so the snapshot's objective region builds from the DB instead of BeachTailsTemplate.bin (#186).
		public virtual ICollection<QuestGoalDefinition> Goals { get; set; } = [];
		public virtual ICollection<QuestTextRef> TextRefs { get; set; } = [];

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
		//
		// IndexMagic is the AREA's `ACGHash` token, not a per-quest magic: the client reads it through `FUN_005d7190`,
		// which only checks the four bytes are `[0-9A-Z_]` (see docs/re/questc-region-decode.md). Area 241 = "W6QT",
		// 1054 = "RRL3", 1772 = "B0DN", 1834 = "3734" - stable across every character and capture. The QuestJournal
		// carries the same value in its area header, which is where the importer harvests it from (#544); in a
		// multi-quest index each held quest contributes its own block keyed by this (#255). Stored per quest because
		// that is the grain both regions consume it at.
		public uint IndexMagic { get; set; } // 4 bytes string
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

		// The Quest_c record's word @+0x14. A per-quest FLAG WORD, not a count - measured over the 99 quests with a
		// captured record: it takes just five values (2, 10, 18, 26, 1042) and EVERY one of them is ≡ 2 (mod 8), so
		// the low three bits are fixed (010) and the rest toggle. It was documented as "the per-quest goal count",
		// which the corpus disproves outright: 2 appears on records carrying 1, 2, 3 and 6 goals, 10 on 1, 2, 4 and 7,
		// and the beach quest's 1042 on 1, 2, 3 and 12. Constant per quest across every character and capture.
		public uint SnapshotUnknown14 { get; set; }

		// @+0x28, @+0xf0 and @+0xf8: three per-quest magnitudes that SCALE WITH THE QUEST'S LEVEL, which is what says
		// they are content values (a reward, a cost, a duration) rather than flags or offsets. Across the captured
		// records: level 21-24 quests carry thousands (F0 2800-6300), level 70 carries 15421/34417, level 76 87227,
		// level 77 102300, level 80 up to 170500.
		//
		// Two structural facts hold across the corpus and are the lead worth pulling on next:
		//   * @+0x28 and @+0xf0 are near-mutually-exclusive - a quest carries one or the other, never both, with a
		//     single exception (13479 carries 80 in both);
		//   * @+0xf8 is non-zero only where @+0xf0 is, and runs ~10-37% of it - so F0/F8 read as a pair.
		// Named for their offsets until something pins WHICH quantity: nothing in the captures labels them.
		public uint SnapshotUnknown28 { get; set; }

		// @+0xec: 1 in every one of the 99 captured records - no quest varies it. Kept as a column rather than a
		// literal so a capture that ever disagrees shows up as data, not as a wrong constant baked into the builder.
		public uint SnapshotUnknownEc { get; set; }

		// See the @+0x28 note above for the magnitude. F0 additionally selects WHICH SPELLING the quest's @609
		// objective region uses for its trailing text: the beach starter is the one quest captured with two variants,
		// and its goal 1448 carries `<remoteformat …></remoteformat>` in all 15 captures whose record has F0=150 and
		// the 21-byte-shorter `<localized … flags="" …>` in all 5 with F0=175 (every other scalar identical). The
		// importer's region verification groups on this so it compares like with like (#389).
		public uint SnapshotUnknownF0 { get; set; }

		public ulong SnapshotUnknownF8 { get; set; }
	}

	// The server-side trigger that advances a quest goal (Gitea #261). Persisted as an int; ordering is stable.
	// The goal's own kind tag, carried on the wire as `QuestGoalDefinition.Separator` - the VERB the objective asks
	// for. Distinct from QuestGoalType below, which is OUR server-trigger wiring: this one comes from the client's
	// data and is the evidence that wiring should be derived from.
	//
	// Decoded by cross-referencing the goals whose semantics were independently established from captures. The
	// clincher is quest 12371 (Shipwrecked V), whose two goals reference the SAME item id with the SAME count and
	// still differ: "collect 3 quarry bricks" is 23 and "deliver 3 to Turach" is 20. So the tag describes the goal,
	// not its target.
	//
	// CONFIRMED against a second, independent source: the goal's own OBJECTIVE TEXT, which is readable now that the
	// encrypted LDB categories are (#520). `--report ldb-quest-text` prints the tag beside the text for all 168 goals,
	// and that cross-check is what each value below cites - the tag is no longer inferred from the target reference.
	// It also CORRECTED three readings that had been taken from the targets rather than the verb; see 5, 9 and 22.
	//
	// ⚠️ Values seen in the corpus only - this is not the client's full enum, and the gaps are unmapped rather than
	// absent. `Separator` keeps its wire-derived name on the row because it is a captured field; this enum is the
	// meaning, not a rename.
	public enum QuestGoalKind : uint
	{
		// ⚠️ THERE IS NO KIND 0. This enum used to declare `Untargeted = 0`, "no typed target reference", with 9 goals
		// spanning speak/talk/free/get/give/return/unlock - a spread explained away as "this one is about the
		// reference, not the verb". It was the UN-HARVESTED DEFAULT (#548): those goals are exactly the ones the
		// corpus only ever full-updates, and the full-update path seeded `Separator` not at all. The wire never sends
		// 0 - across all 158 snapshot goals and all 111 full-update goals the tag takes 5/9/10/12/20/22/23/24/25 and
		// nothing else - and once the full-update's tag is lifted, not one shipped row carries 0. The nine scattered
		// into Generic (Laranga, Turach, Casilda, Saddur), Collect (Scavenger Tuut's bag) and Deliver (Tina), where
		// their verbs read as the rest of those kinds do.

		// CRAFT the named item: all 9 goals read "Make <item>" - Violet Cureall, Ambrosial Gold, Quicksilver Spirits,
		// Khat Blackbeer, Darkwinter Wine, Bitterbrisk, Visionwine, Musewater, Ale of Auspice (quests 13141/13143/13145).
		// ⚠️ Was `AcquireUnique`, "acquire ONE of a named/unique item". That read the ITEM NAMES and not the verb: the
		// text says Make, so these are recipe objectives, not gather-one objectives.
		Craft = 5,

		// REGION-SCOPED: the target is a bounded-area reference (TrailingCategory 51200), so the trigger volume is
		// recoverable. Only 3 goals carry one, and ⚠️ only ONE of the three is a reach - "Enter the jungle" (1448).
		// The others are "Overcome the Emperor's Manifested Nightmares" (11712, count 3) and "Locate the Red-Hand
		// Leader" (14073). So the region SCOPES the objective ("3 of something in there") as often as it IS the
		// objective, and this tag does not by itself make a goal a ReachArea.
		RegionScoped = 9,

		// Kill: the target is an npc_template (51000) or a monster category (51100). The cleanest value in the set -
		// all 30 goals are kill verbs (kill, beat, cleanse, destroy, slaughter).
		Kill = 12,

		// DELIVER the item to someone - it leaves the player rather than arriving. All 13 goals read deliver/give/bring.
		// ⚠️ The ROW carries the item but never the recipient; the recipient is named in the objective TEXT, which is
		// how these are wired (ImportOps.TypeDeliverGoalsFromRecipient). It is not unrecoverable, just not on the row.
		Deliver = 20,

		// The GENERIC kind - by far the largest (60 goals) and the one with no single verb: vanquish, talk, use, find,
		// speak, ask, report, enter, escort, extinguish, investigate, kill, lead, order, replace, win, destroy, get.
		// ⚠️ Was `ReachPoint`, "marker coordinates with no region reference". The marker part is true and the "reach"
		// part is not - a goal lands here when nothing more specific applies, and it happens to carry a map pin.
		Generic = 22,

		// COLLECT a quantity - the bulk of the gather objectives (44 goals: get, collect, gather, recover, remove,
		// search, steal), and the only kind carrying large counts (Poisonous Gland x20, Shadeleaf x10, and a x50).
		Collect = 23,

		// USE a named object. One goal: "Use the Vial to Capture Gas" (11252).
		Use = 10,

		// INVESTIGATE a place/object. One goal: "Investigate the Ancient Gateway" (8278).
		Investigate = 24,

		// DESTROY a world object, and it takes a COUNT: "Destroy Bowl of Set" (x7 separate goals in quest 13473),
		// "Destroy the Wolves' Saddles" (count 2), "Destroy the Wolves' Weapons" (count 5). Distinct from Kill, whose
		// target is an NPC template.
		DestroyObject = 25
	}

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
