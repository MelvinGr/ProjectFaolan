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
		// The quest LINE this quest is a step of (e.g. 1054), carried raw in both the journal and full-update packets.
		// It is the client's `Quest.main_quest_template_id` (RDB 1000104), which is what identifies our QuestLine as
		// the client's MainQuest.
		public uint QuestLineId { get; set; }

		// The line this quest is grouped under. Every referenced line has a row: the importer inserts a placeholder
		// (Magic 0) for any line the captures never showed a completed quest in, the same way it does for Maps.
		[ForeignKey(nameof(QuestLineId))]
		public virtual QuestLine Line { get; set; } = null!;

		// The primary objective's goal id (e.g. 0x1670 = 5744), echoed in QuestGoal and the full-update.
		public uint GoalId { get; set; }

		// The objective's world-marker (map id + x/y) shown on the map. NULL when the quest has no map marker - which
		// is every quest that comes from the client's catalog rather than a capture, since the catalog carries no map.
		//
		// ⚠️ Was a NOT NULL FK with "0 = none" in the comment and no row ever holding 0, because the importer SKIPPED
		// any quest whose map it could not resolve ("map N not in the Maps catalog") - a silent drop that read as
		// "the wire didn't carry it". Nullable is both the honest model and what lets a mapless quest exist at all.
		public uint? MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map? Map { get; set; }

		// The next stage in a multi-stage quest CHAIN (Gitea #261): completing this quest's goal SUPERSEDES it with
		// NextQuestId (QuestProgressionService sends QuestGoal + QuestIIR 2->4 + QuestFullUpdate(next) and sets the next
		// quest Active). null = no successor (a standalone quest, or the last stage of a chain). The Tortage intro chain
		// is 13479 (kill Tuut) -> 13563 (loot key) -> 13480 (free/talk Casilda) -> 12407 (escort) -> null.
		public uint? NextQuestId { get; set; }

		// The RDB quest id (e.g. 13479) - the primary key (natural key; see the class note).
		[Key]
		public uint QuestId { get; set; }

		// The designers' name for this STEP, in plaintext, from the client's quest catalog (RDB 1000104,
		// `Quest.name`; see docs/rdb/quest-catalog-1000104.md) - "The Slaver", "The Scavenger's Key". Distinct from
		// the journal TITLE the client renders, which is a text ref resolved against an LDB page and needs a captured
		// decryption key; this one is free and covers all 5042 catalog quests. Null for a quest the catalog omits.
		[MaxLength(256)]
		public string? Name { get; set; }

		// Hand-reviewed: the importer must not overwrite this row's fields. Set from the editor, NEVER by an import.
		// See ConversationNode.Verified - same contract, same `--force` escape.
		public bool Verified { get; set; }

		// The per-GOAL objective detail (the @609 region of the FullChar snapshot tails) - one row per goal of the
		// quest. A quest can have several goals each with its own world marker + objective text (the beach starter has
		// two: 1448 + 3490). Distinct from TextRefs (the journal header); these drive QuestObjectiveRegion generation
		// so the snapshot's objective region builds from the DB instead of BeachTailsTemplate.bin (#186).
		public virtual ICollection<QuestGoalDefinition> Goals { get; set; } = [];
		public virtual ICollection<QuestTextRef> TextRefs { get; set; } = [];

		// True once this quest's Quest_c record scalars are known, i.e. the record can be GENERATED - either HARVESTED
		// from a captured journal (82 quests) or DERIVED for a quest that had everything else
		// (ImportOps.SeedDerivedSnapshotRecords, 17 quests). Previously inferred from `SnapshotState != 0` - but that
		// word turned out to be the quest LEVEL (it collapsed into Level), and plenty of definitions carry a level
		// with no record scalars at all, so the inference would have made them falsely eligible. It cannot be inferred
		// from the Snapshot* columns either: every one of them is legitimately 0 on some captured record.
		//
		// ⚠️ NOT redundant with ObjectiveRegionVerified, even though the two currently agree on all 5048 rows (99 set,
		// 4949 clear). They are independent facts about different halves of the snapshot portion, set by different
		// processes: this one says we can build the RECORD, that one says the @609 REGION provably rebuilds. The
		// agreement is a property of today's data - verification stands at 99 of 99 - not of the model. The moment a
		// capture lands whose region does NOT rebuild, the pair separates into (record yes, region no), and excluding
		// that quest is the entire reason ObjectiveRegionVerified exists: an unreproducible region takes the whole
		// login down (statlist.cpp:134), while a missing record just means the quest cannot be offered.
		public bool HasSnapshotRecord { get; set; }

		// True when the quest's @609 objective region, REBUILT from these rows, reproduces the captured bytes exactly.
		// Set by the importer, which does that comparison per quest at harvest time (ImportOps.VerifyObjectiveRegions).
		//
		// This is what gates a quest into the login snapshot. Shipping an objective region we cannot reproduce desyncs
		// the client and asserts it on statlist.cpp:134, and the region still has undecoded fields - so "we harvested
		// some goals" is not good enough, only "the bytes come out identical" is. The flag flips itself on as decoding
		// improves; no code change is needed to widen coverage.
		public bool ObjectiveRegionVerified { get; set; }

		// The LINE's quest catalog - every step of this quest's line, ordered by JournalSequence - as CSV. NOT this
		// quest's goals and NOT its chain, both of which the old name "IndexGoalSteps" invited: quest 10238's list is
		// line 241's eight quests at sequences 1-8, while its own goals are [1448,3490]; 13479's is line 1054's four.
		// A fresh character with nothing completed carries the whole list, so it is static line data.
		//
		// ⚠️ Vestigial, and deliberately kept only as a FALLBACK: QuestIndexRegions.ForQuests prefers the catalog the
		// caller loads from the DB (the line's quests, ordered by sequence) and reads this only when none was passed,
		// which today is the two hand-seeded quests. Do not harvest it for more quests - the DB already derives it.
		[MaxLength(128)]
		public string? IndexQuestLineQuestIds { get; set; }

		// Quest index region: the area table's marker + three lead words + trailer word. Beach 10238: marker 0x7E2,
		// fields 1/7/0, trailer 0x156E7, and area 241's 8 quests as the table body (see IndexQuestLineQuestIds - the body is
		// the AREA CATALOG, not "the client's 1..n goal index", which is what this said until the list was checked
		// against the area's own rows).
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
		// doubly wrong - it is not `n` (that is SnapshotQuestLineFlags, a different word at a different offset).
		public uint SnapshotUnknown118 { get; set; }

		// The Quest_c record's word @+0x14 - a BITFIELD, and a property of the quest LINE rather than the quest.
		// Across the 99 quests with a captured record it takes five values (2, 10, 18, 26, 1042), every one ≡ 2
		// (mod 8): bits 0-2 are fixed `010` and bits 3, 4 and 10 toggle. Definitively NOT the goal count it was
		// documented as - 2 appears on records carrying 1, 2, 3 and 6 goals.
		//
		// Its SCOPE is settled: constant per quest across 2626 captured records, AND constant across every quest of
		// the same line in all 90 lines that have one - zero conflicts. So it is the line's, inherited by each step.
		//
		// **bit 4 (0x10) = the quest may NOT be SHARED.** Proven in-client: sweeping this column through all five
		// observed values on a rendered quest and relogging, the journal's detail pane greys out its `Share` button
		// for exactly the three values with bit 4 set (18, 26, 1042) and enables it for the two without (2, 10) -
		// pixel-identical within each class, so bits 3 and 10 do not affect it. The corpus fits: bit 4 is set on the
		// Destiny steps, the Alchemist tier-4 progression and the Khitai/faction content (37 of 82), and clear on
		// ordinary side quests (45).
		//
		// **bits 3 (0x08) and 10 (0x400): no effect on the journal panel.** The same sweep rendered 2 vs 10, and 18
		// vs 26 vs 1042, PIXEL-IDENTICALLY - so whatever they drive is not this UI. Earlier readings of bit 10 as a
		// "progression line" marker and of bit 4 as "expansion content" were both correlations without a mechanism;
		// the first is untested elsewhere and the second is now superseded. Gitea #570.
		public uint SnapshotQuestLineFlags { get; set; }

		// @+0x28 - the quest's EXPERIENCE reward. This is the number the client's journal renders as "XP:" in a
		// quest's detail pane, established IN-CLIENT rather than inferred: driving quest 13227's column to 5553, 0 and
		// 7777 in turn and relogging produced "XP: 5553", "XP: 0" and "XP: 7777", with nothing else changed.
		//
		// ⚠️ It was called SnapshotQuestLineReward, and @+0xf0/@+0xf8 were called SnapshotXpReward/SnapshotCashReward,
		// on the strength of the client catalog's `xp_reward`/`cash_reward` pair and a magnitude coincidence. The
		// in-client test overturned that: with @+0xf0=8888 and @+0xf8=999999 the panel showed "XP: 7777" (this field)
		// and left "Money:" blank. A field-name match in a neighbouring data file is not evidence about the wire.
		//
		// Its positional law still holds and is still unexplained: it is populated on the LAST step of a quest line
		// (25 of 26 captured cases) and @+0xf0/@+0xf8 never are. It scales with level - level 23 -> 900..4981, level 30
		// -> 5553, level 70 -> 15421, level 80 -> 69337..138674, the level-80 values being exactly {1, 4/3, 5/3, 2} x
		// 69337.
		public uint SnapshotXpReward { get; set; }

		// The Quest_c record's @+0x2c - the first quest of the SERIES this one belongs to, which is the quest's own id
		// for a standalone quest (84 of the 99 captured records) and an earlier step of the same chain for the rest:
		// 10915 and 12371 both carry 10238, the beach starter, and both appear in ITS IndexQuestLineQuestIds list; 12068 carries
		// 12067; 13986 carries 13984. Harvested, never derived - only the HEAD carries IndexQuestLineQuestIds, so a member has
		// nothing locally that names its series, and NextQuestId gives successors rather than the start (Gitea #566).
		// 0 = never captured; the builder then falls back to the quest's own id, which is right for a standalone quest
		// and is what we emitted for everything before this column existed.
		public uint SnapshotChainHeadQuestId { get; set; }

		// @+0xec: 1 in every one of the 99 captured records - no quest varies it. Kept as a column rather than a
		// literal so a capture that ever disagrees shows up as data, not as a wrong constant baked into the builder.
		public uint SnapshotUnknownEc { get; set; }

		// @+0xf0 and @+0xf8 - two per-quest magnitudes that are NOT the journal's XP or Money. Setting them to 8888
		// and 999999 on a rendered quest changed neither label (the XP came from @+0x28 and "Money:" stayed blank), so
		// whatever they are, the quest detail pane does not show them.
		//
		// What is still true about them, and unexplained: the client's quest catalog (RDB 1000104) gives a quest line
		// exactly one `xp_reward`/`cash_reward` pair, and the magnitudes coincide - the `[Thunder River]` lines carry
		// `xp_reward = 45800`, which is exactly the captured @+0xf0 of quests 13895 and 14427, and the catalog's
		// low-level cash/xp ratios (715/60, 1000/157, 455/36 -> 0.08-0.16) match the wire's @+0xf8/@+0xf0 (150/15,
		// 370/44, 550/66 -> 0.10-0.12). A LINE-level reward the step pane never renders would fit both observations,
		// but that is a guess and the last guess here was wrong - so they keep their offsets as names.
		//
		// ⚠️ @+0xf8 tracks @+0xf0 through a level-dependent factor (~0.267 around level 23 rising to ~1.10 at 80) but
		// 99 samples do not close the law - do NOT derive one from the other.
		//
		// @+0xf0 additionally selects WHICH SPELLING the quest's @609 objective region uses for its trailing text: the
		// beach starter is the one quest captured with two variants, and its goal 1448 carries
		// `<remoteformat …></remoteformat>` in all 15 captures whose record has 150 and the 21-byte-shorter
		// `<localized … flags="" …>` in all 5 with 175. The importer's region verification groups on this (#389).
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
		QuestLineTitle = 5, // category 50300 - the quest LINE's title (shared header)
		RegionName = 6, // category 52000, id 4 - region name (shared header)
		MapName = 7 // category 52000, id = map id - map name (shared header)
	}
}
