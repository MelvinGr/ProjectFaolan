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
		// Hakotep L24 -> 19-30). Was called SnapshotN, which was doubly wrong - it is not `n` (that is
		// QuestLineFlags, a different word at a different offset).
		//
		// ⭐ **It is the level the quest's REWARDS are scaled to** - measured, and the one thing about it that is now
		// pinned. Both reward pairs the record carries (`MoneyReward`/`XpReward` and `FieldF8`/`FieldF0`) have a
		// money:xp ratio that is a function of THIS field and not of `Level`: grouped by `Field118` the within-group
		// spread is **0.0032**, grouped by `Level` it is **0.0702** - 22x looser, and the two disagree on 20 of the 30
		// rows that carry a pair. See FieldF0 below for what that buys.
		public uint Field118 { get; set; }

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
		// for exactly the three values with bit 4 set (18, 26, 1042) and enables it for the two without (2, 10).
		// ⚠️ That sweep ALSO reported the classes as pixel-identical internally, which was read as "bits 3 and 10
		// affect nothing" - wrong for bit 10, whose effect a GM account suppresses (see below). The corpus fits: bit 4 is set on the
		// Destiny steps, the Alchemist tier-4 progression and the Khitai/faction content (37 of 82), and clear on
		// ordinary side quests (45).
		//
		// **bit 10 (0x400) = the quest may NOT be DELETED.** The exact sibling of bit 4, one widget along: the journal's
		// detail pane has two action buttons, and the client enables each by NEGATING its bit -
		// `enabled = ~(flags >> 4) & 1` drives Share, `~(flags >> 10) & 1` drives Delete.
		//
		// ⚠️ It is overridden for GAME MASTERS, which is why an in-client sweep could not see it and this sat unnamed
		// through two wrong guesses. The client's branch is
		//     if (flags & 0x400)  enabled = (GmLevel != 0);   // stat 0x0D7
		//     else                enabled = 1;
		// so on a GM account the button is force-enabled whatever the bit says - and the account the sweep ran on is
		// `Administrator`. The earlier note that 18/26/1042 render "pixel-identically" was true and misleading: the
		// difference was real and being suppressed by the observer's own GM level.
		//
		// [in-client] Proven by A/B on ONE quest with a single variable changed. Quest 10238 "The Slaver" on a
		// NON-GM account: flags 1042 (bit 10 set) -> Delete greyed; flags 18 (bit 10 clear, bit 4 left set) -> Delete
		// enabled. Same character, same quest, same solo state. The corpus agrees with the meaning - bit 10 is the
		// Tortage Destiny storyline (The Slaver, The Quarry, The City of Tortage, Nadini, Casilda, The Scavenger) plus
		// the Alchemist progression, i.e. exactly the quests retail refuses to let you abandon.
		//
		// **bit 3 (0x08): the CLIENT NEVER READS IT** (#570, closed). Not "unnamed pending a better sweep" - there
		// is no consumer anywhere in the image. A bit test has two spellings here and a scan for one is blind to the
		// other: the mask form (`TEST byte ptr [reg+0x14], 8`, `DumpQuestLineFlagBits.java`) and the SHIFT form
		// (`SHR reg,3; TEST CL,1`, `FindBitShiftAtOffset.java`), which is the one bits 4 and 10 are written in.
		// Over the whole image the shift form gives 33 sites for bit 4 and 12 for bit 10, both including the known
		// consumer `FUN_01617260` (the journal panel) - and 12 for bit 3, of which ELEVEN are `SAR reg,3`
		// divide-by-8 pointer arithmetic in container code and the twelfth is a protobuf serializer testing its own
		// has-bits. The mask form adds only stack locals and C++ iostream internals. Two positive controls, one
		// clean negative.
		//
		// So it is authoring metadata the record carries and the client ignores - like `FieldF0`/`FieldF8` (#802),
		// and unlike its two neighbours in the same word. Our sender echoes the captured value per line, which is
		// all it can do and all retail's own client would notice. It is 6 quests: 12381, 12583, 12889, 13473,
		// 16004, 16183.
		//
		// RULED OUT along the way, so nothing re-walks them if a SERVER-side meaning is ever wanted. Each is a
		// candidate that FITS all six and still fails, because it also fits most of the quests that do NOT carry it:
		//   * "the line pays no reward" - true of all 6 (XpReward and MoneyReward both 0), and ALSO of 69 of the
		//     other 93 flagged quests. Necessary, nowhere near sufficient. This is the observation the ticket
		//     recorded; measuring the other side of it is what kills it.
		//   * a ZONE or dev tag - the 6 spread across four different bracket tags in their line names
		//     ([Conall's_Valley], [Khopshef_Province] x2, [Kheshatta_Kalanthes], [Northern Grasslands] x2) with no
		//     marker in common.
		//   * goal COUNT or goal KIND - a tendency, not a rule. The 6 average 3.8 goals against 1.5, and 5 of 6
		//     carry a count-style goal (Collect 23 / DestroyObject 25) against 26% - but 12889 is a single Kill goal
		//     and breaks both.
		//   * the line's TIER - both 0 and 1 appear among the six.
		//   * PARALLEL objectives - "several distinct sub-goals that must all be done, all at Sequence 1" fits four
		//     of the six beautifully (12583's four severed heads, 16004's seven faction documents, 13473's seven
		//     Bowls of Set) and is refuted by its own denominator: 358 quests have >= 4 goals all at one sequence
		//     and only THREE of them carry bit 3.
		//   * carrying a goal of TYPE 5 - the six hold 12 of the catalog's 42 type-5 goals, a 29% share out of
		//     6 quests in 5,049, which reads like a rule and is not: 27 quests carry a type-5 goal and only three
		//     of them are bit-3. The concentration is because these six are goal-HEAVY, which the line above
		//     already covers.
		//
		// ⚠️ Worth knowing while looking at bit 4: every bracket tag that carries it is Khitai/RoTG content
		// (`RoTG_Storyline`, Northern Grasslands, Warmonk Monastery, Chosain_Province, Mines_of_Chosain,
		// Kang_Pagoda, Kara Korum, Paikang, Alchemist_4) and every tag without it is base game. That is 12 of the 14
		// Northern Grasslands lines; the 2 that lack it are exactly the counterexamples that (correctly) ruled
		// "expansion" out as the DEFINITION. So expansion is an ~86% correlate of "not shareable" rather than the
		// thing itself - which is what you would expect of a design flag expansion content nearly always sets.
		public uint QuestLineFlags { get; set; }

		// @+0x20 - the quest's MONEY reward, the twin of XpReward below (u64 money at 0x20, u32 xp at 0x28,
		// adjacent in the struct). Stored RAW as the wire carries it, which is in TIN - 100 tin = 1 copper, 100
		// copper = 1 silver. The client's journal prints it on the `Money:` line, so the harvested 214 of quest 13507
		// renders "2C 14T", 111200 renders "11S 12C" and 250000 renders "25S".
		//
		// [in-client] Established the same way as the XP field - driven to a value and read back off the panel.
		// It was modelled as `QuestcRecord.Accept`, "likely acceptTime", and emitted as a CONSTANT 0, which threw the
		// value away: 21 of the 99 captured quests carry a non-zero one, constant per quest across every character and
		// capture. The set is a subset of the quests carrying an XP reward - some quests pay experience only.
		public ulong MoneyReward { get; set; }

		// @+0x28 - the quest's EXPERIENCE reward. This is the number the client's journal renders as "XP:" in a
		// quest's detail pane, established IN-CLIENT rather than inferred: driving quest 13227's column to 5553, 0 and
		// 7777 in turn and relogging produced "XP: 5553", "XP: 0" and "XP: 7777", with nothing else changed.
		//
		// ⚠️ It was called SnapshotQuestLineReward, and @+0xf0/@+0xf8 were called XpReward/SnapshotCashReward,
		// on the strength of the client catalog's `xp_reward`/`cash_reward` pair and a magnitude coincidence. The
		// in-client test overturned that: with @+0xf0=8888 and @+0xf8=999999 the panel showed "XP: 7777" (this field)
		// and left "Money:" blank. A field-name match in a neighbouring data file is not evidence about the wire.
		//
		// Its positional law still holds and is still unexplained: it is populated on the LAST step of a quest line
		// (25 of 26 captured cases) and @+0xf0/@+0xf8 never are. It scales with level - level 23 -> 900..4981, level 30
		// -> 5553, level 70 -> 15421, level 80 -> 69337..138674, the level-80 values being exactly {1, 4/3, 5/3, 2} x
		// 69337.
		public uint XpReward { get; set; }

		// The Quest_c record's @+0x2c - the first quest of the SERIES this one belongs to, which is the quest's own id
		// for a standalone quest (84 of the 99 captured records) and an earlier step of the same chain for the rest:
		// 10915 and 12371 both carry 10238, the beach starter, and both appear in ITS IndexQuestLineQuestIds list; 12068 carries
		// 12067; 13986 carries 13984. Harvested, never derived - only the HEAD carries IndexQuestLineQuestIds, so a member has
		// nothing locally that names its series, and NextQuestId gives successors rather than the start (Gitea #566).
		// 0 = never captured; the builder then falls back to the quest's own id, which is right for a standalone quest
		// and is what we emitted for everything before this column existed.
		public uint ChainHeadQuestId { get; set; }

		// @+0xec: 1 in every one of the captured records - no quest varies it. Kept as a column rather than a
		// literal so a capture that ever disagrees shows up as data, not as a wrong constant baked into the builder.
		//
		// ⚠️ **It is therefore USELESS AS A CORRELATE, and looks exactly like a useful one.** Because it is constant
		// where it is present and 0 where it is not, `FieldEc == 1` is EXACTLY `HasSnapshotRecord` - zero mismatches
		// over all 5,049 rows. Any cross-tab against it is really a cross-tab against "was this quest captured", which
		// returns a perfect-looking correlation with anything else that is only known from a snapshot. #570 nearly
		// banked one: all six quests carrying `QuestLineFlags` bit 3 have `FieldEc == 1`, which says nothing at all,
		// because every row with a non-zero `QuestLineFlags` does.
		public uint FieldEc { get; set; }

		// @+0xf0 and @+0xf8 - two per-quest magnitudes that are NOT the journal's XP or Money. Setting them to 8888
		// and 999999 on a rendered quest changed neither label (the XP came from @+0x28 and "Money:" stayed blank), so
		// whatever they are, the quest detail pane does not show them.
		//
		// ⚠️ **The catalog COINCIDENCE that suggested a line-level reward is now explained away** (#759,
		// `--report rdb-quest-catalog`). The client's quest catalog (RDB 1000104) does give a quest line exactly one
		// `xp_reward`/`cash_reward` pair, and `45800` really is in it - but it is on THREE `[Thunder River]` lines
		// (1961 *Voyage of the Dead*, 1963 *Turning Point*, 1971 *Pools of Blood*), none of which is the line either
		// quest 13895 or 14427 sits on. 45800 is that ZONE's per-line figure appearing on several lines, and those two
		// quests are Thunder River content: same design budget, different rows. A magnitude that repeats is not an
		// identity.
		//
		// The catalog cannot settle it either way, and the numbers say why rather than leaving it hanging: its reward
		// field is populated on **26 of 2,181** lines, our 31 carriers sit on 31 other lines, and the two sets are
		// DISJOINT - which is what chance predicts (31 x 26 / 2181 = 0.4 expected overlap). So 0/31 agreement is an
		// underpowered test, not a refutation. The whole populated set is printed by that report so the next reader
		// does not re-derive it.
		//
		// ⭐ **What the same pass DID establish is positional, and it is the complement of `XpReward`'s law.** Over the
		// 14 carriers whose line is fully ordered by a captured `JournalSequence`, the carrier is the FIRST step of its
		// line **12** times, the LAST **0** times, and a middle step twice (lines 1260 and 1893, both 3-step). So
		// `XpReward` sits at the END of a line and this pair at the START - two rewards computed the same way, at
		// opposite ends, only one of them displayed.
		//
		// And it is NOT repeated on the line's other steps: of the sibling steps a snapshot actually captured, 0 carry
		// the carrier's value and 4 carry 0 (lines 241 and 1054). That is the literal "constant across a line's member
		// steps" test, and the answer is no - which makes it single-step like `XpReward` rather than a line attribute
		// stamped on every step.
		//
		// The low-level cash/xp ratio agreement stands (catalog 715/60, 1000/157, 455/36 -> 0.08-0.16 against the
		// wire's 150/15, 370/44, 550/66 -> 0.10-0.12), and it is a statement about the FORMULA both are generated by,
		// not about the fields being the same field. They keep their offsets as names.
		//
		// ⚠️ @+0xf8 tracks @+0xf0 through a level-dependent factor (~0.267 around level 23 rising to ~1.10 at 80) but
		// 99 samples do not close the law - do NOT derive one from the other.
		//
		// ⭐ That factor is now known to be **the same function that relates `MoneyReward` to `XpReward`**, keyed on
		// `Field118`. The two pairs are populated on DISJOINT rows (only quest 13479 carries both), so they had never
		// been compared; where a `Field118` group happens to contain both, they agree to four decimals:
		//
		//     F118=20   F8/F0 0.2674, 0.2669   |  Mon/Xp 0.2674
		//     F118=21   F8/F0 0.2919           |  Mon/Xp 0.2918
		//     F118=23   F8/F0 0.3328 (x7)      |  Mon/Xp 0.3328, 0.3324
		//     F118=80   F8/F0 0.1002e1 (x5)    |  Mon/Xp 1.0997-1.1000 (x11)
		//
		// So @+0xf0/@+0xf8 are a money+xp pair of the SAME KIND as the journal's, generated by the same formula - and
		// it rules out their being unrelated magnitudes.
		//
		// ⭐ And the disjointness is the finding, not a footnote to it (#802). Measured by `--report rdb-quest-catalog`'s
		// `TwoPairExclusion` over the 100 captured quests / 95 captured lines:
		//
		//     per QUEST  @+0xf0 only 30, @+0x28 only 20, BOTH 1   (chance predicts 6.5 overlaps)
		//     per LINE   @+0xf0 only 30, @+0x28 only 20, BOTH 1
		//     the one overlap, quest 13479: @+0xf0 80 == @+0x28 80  (@+0xf8 7 against @+0x20 0)
		//
		// ⛔ The per-LINE row is what kills "two rewards at opposite ends of one line": that reading predicts BOTH on
		// nearly every reward-bearing line, and 1 of 51 is the opposite of that. A line carries ONE reward, in one
		// slot pair or the other - and the positional law then follows from which slot a line uses rather than
		// explaining anything. On the single line that carries both, the XP agrees exactly (a capture-derived value:
		// quest 13479's record is gated byte-exact by `QuestcRegionTests`).
		//
		// ⛔ And the "advertised reward" candidate's own test is answered: `QuestGiverList.MainQuestReward` is the
		// ITEM reward list (imported as `QuestLineReward`, its `type` the item's `SearchCategory` on 1,635 of 1,635),
		// carrying no xp or money amount at all - so it cannot be where an advertised money+xp pair surfaces.
		//
		// ⛔ And the client settles the rest of it by NOT READING THEM (#802, closed). Three functions in the whole
		// image touch `Quest_c+0xf0`/`+0xf8` - the constructor, the journal record's stream reader and its stream
		// WRITER - so the client stores the pair and hands it straight back, which is exactly why 8888/999999 in
		// these two changed no label anywhere. ⚠️ That is a statement about these fields, not about the record: the
		// neighbouring `@+0x114`/`@+0x118` do have a consumer (`FUN_0091f6f0`, the journal's level-band/colour
		// function - see QuestcRecordCodec). So which slot a quest's reward sits in has no observable consequence
		// for the client, and our sender's job is to echo what was captured per quest, which is what this column is.
		//
		// @+0xf0 additionally selects WHICH SPELLING the quest's @609 objective region uses for its trailing text: the
		// beach starter is the one quest captured with two variants, and its goal 1448 carries
		// `<remoteformat …></remoteformat>` in all 15 captures whose record has 150 and the 21-byte-shorter
		// `<localized … flags="" …>` in all 5 with 175. The importer's region verification groups on this (#389).
		public uint FieldF0 { get; set; }

		public ulong FieldF8 { get; set; }
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

	// OUR server-trigger wiring for a goal - which live event advances it. Distinct from QuestGoalKind above, which is
	// the CLIENT's verb; this is what we can actually fire on, and the two only line up where a trigger exists.
	public enum QuestGoalType
	{
		// NO SERVER TRIGGER: the goal is stored, rendered and counted, and nothing advances it.
		//
		// ⚠️ 0 USED TO BE `Kill`, and that was the same trap #548 removed from `Separator` - a real value doubling as
		// the un-harvested default. It read as "kill the TargetName NPC" while actually meaning "nobody has typed this
		// goal yet", and 7,148 catalog goals sat on it: no verb, no target, no trigger, all of them nominally kill
		// goals. Every guard in `QuestProgress.MatchingKillGoal` exists because of that - the PARTIAL exclusion, then
		// the zero-template-id guard (#522), each one catching another way a default-typed row false-fired on a kill.
		//
		// Renaming 0 changes no stored value: it states what 0 always meant. The rows that genuinely ARE kill goals get
		// there from their own verb (ImportOps.TypeKillGoalsFromKind, `QuestGoalKind.Kill`), which is why `Kill` is now
		// 6 - it costs one column on ~30 rows instead of rewriting 7,148 that were never kills.
		//
		// It is also the honest type for a goal whose verb we cannot yet trigger: Craft (#148 tradeskills), Use
		// (item-use-on-target) and DestroyObject (a destructible world object we do not hold) all land here rather than
		// being bent into the nearest trigger that would fire early (#522).
		None = 0,
		LootFromNpc = 1, // loot the TargetName NPC's corpse bag (the quest item is RDB content; the bag source is the key)
		TalkToNpc = 2, // talk to (open the greeting of) the TargetName NPC
		Escort = 3, // escort the TargetName NPC to a destination
		ReachArea = 4, // reach a map location - solved when the player moves within range of the goal's (MapId, MarkerX, MarkerY) point (no TargetName; e.g. Shipwrecked's "Enter the jungle", #262)
		CollectItem = 5, // collect N of the item in TrailingRefId, one per interaction with a quest-pickup world object (QuestItemEffect); e.g. Shipwrecked V's "get 3 stone bricks" (item 193400), #262
		Kill = 6, // kill the NPC in TrailingRefId (a 51000 npc_template reference), or the TargetName NPC

		/// <summary>
		///     USE the item in <see cref="QuestGoalDefinition.TrailingRefId" /> (an LDB 50200 item reference) -
		///     solved by right-clicking it out of a bag, which is the one thing the goal's own row states (#522).
		/// </summary>
		/// <remarks>
		///     ⚠️ Deliberately NOT a <see cref="CollectItem" />. The kind's verb is `Use`, and the corpus' single
		///     goal - *"Use the Vial to Capture Gas"* (11252, item 4349109 `Po-Sha Potency Vial`) - is one the player
		///     is HANDED and then has to use, so counting its arrival would complete the objective the moment the
		///     quest giver passed it over. The trigger is the use itself.
		///     <para>
		///         The item's own on-use effect is unaffected: the goal ticks off the same `UseInventoryItem` choke
		///         point that applies the consumable's spell, after it, so an item that is both a consumable and a
		///         quest objective does both.
		///     </para>
		/// </remarks>
		UseItem = 7,

		/// <summary>
		///     USE the world object in <see cref="QuestGoalDefinition.TargetWorldObjectId" /> - the door, gate or
		///     lever the objective is about. Solved by the same `UseDynel` BCC a zone-line door takes, not by
		///     standing near it (#262/#527).
		/// </summary>
		/// <remarks>
		///     ⚠️ Deliberately NOT a <see cref="ReachArea" />, which is what "Unlock the Acheronian gate" (12369/3492)
		///     was wired as while this type did not exist. The gate object stands ON the goal's marker, so a reach
		///     completed the objective the moment the player walked up to it - before looting the key, and without
		///     ever touching the gate. Retail's own order settles it: the player targets the gate, sends the use, and
		///     only THEN does the goal tick.
		///     <para>
		///         A goal of this type may also name a <see cref="QuestGoalDefinition.QuestItemId" />, which the
		///         player must be CARRYING for the use to count - the key half of "unlock". The item is not consumed;
		///         see <c>QuestProgress.RequiredHeldItemId</c>.
		///     </para>
		/// </remarks>
		UseWorldObject = 8
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
