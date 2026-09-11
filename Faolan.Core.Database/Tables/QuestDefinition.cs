using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     The journal content of a quest: the text refs + objective marker the server ships so the client's J-panel
	///     renders a real title/description/objective. Harvested from captures by the importer
	///     (<c>QuestDefinitionExtractor</c>). Text is never literal: each ref is a (RefId, Category) the client resolves
	///     against its RDB; KeyText is the deterministic per-(id, category) content hash.
	/// </summary>
	/// <remarks>
	///     <see cref="CharacterQuest" /> persists a character's state (active/progress); this is the shared,
	///     character-independent definition looked up by the RDB quest id. <see cref="QuestId" /> (the RDB quest id) is
	///     the primary key - a natural-key catalog like NpcTemplate/Item/Spell/Map, set from the RDB by the importer and
	///     never DB-generated.
	/// </remarks>
	public class QuestDefinition
	{
		/// <summary>
		///     The quest line this quest is a step of (e.g. 1054), carried raw in both the journal and full-update
		///     packets. It is the client's <c>Quest.main_quest_template_id</c> (RDB 1000104), which is what identifies
		///     our <see cref="QuestLine" /> as the client's MainQuest.
		/// </summary>
		public uint QuestLineId { get; set; }

		/// <summary>
		///     The line this quest is grouped under. Every referenced line has a row: the importer inserts a placeholder
		///     (Magic 0) for any line the captures never showed a completed quest in, the same way it does for Maps.
		/// </summary>
		[ForeignKey(nameof(QuestLineId))]
		public virtual QuestLine Line { get; set; } = null!;

		/// <summary>
		///     The primary objective's goal id (e.g. 0x1670 = 5744), echoed in QuestGoal and the full-update.
		/// </summary>
		public uint GoalId { get; set; }

		/// <summary>
		///     The objective's world-marker (map id + x/y) shown on the map. Null when the quest has no map marker -
		///     which is every quest that comes from the client's catalog rather than a capture, since the catalog
		///     carries no map. Nullable so that a mapless quest can exist at all; the importer must never drop a quest
		///     whose map it cannot resolve.
		/// </summary>
		public uint? MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map? Map { get; set; }

		/// <summary>
		///     The next stage in a multi-stage quest chain: completing this quest's goal supersedes it with
		///     <see cref="NextQuestId" /> (<c>QuestProgressionService</c> sends QuestGoal + QuestIIR 2-&gt;4 +
		///     QuestFullUpdate(next) and sets the next quest Active). Null = no successor (a standalone quest, or the
		///     last stage of a chain). The Tortage intro chain is 13479 (kill Tuut) -&gt; 13563 (loot key) -&gt; 13480
		///     (free/talk Casilda) -&gt; 12407 (escort) -&gt; null.
		/// </summary>
		public uint? NextQuestId { get; set; }

		/// <summary>
		///     The RDB quest id (e.g. 13479) - the natural primary key.
		/// </summary>
		[Key]
		public uint QuestId { get; set; }

		/// <summary>
		///     The designers' name for this step, in plaintext, from the client's quest catalog (RDB 1000104,
		///     <c>Quest.name</c>; see <c>docs/rdb/quest-catalog-1000104.md</c>) - "The Slaver", "The Scavenger's Key".
		///     Distinct from the journal title the client renders, which is a text ref resolved against an LDB page and
		///     needs a captured decryption key; this one is free and covers all 5042 catalog quests. Null for a quest
		///     the catalog omits.
		/// </summary>
		[MaxLength(256)]
		public string? Name { get; set; }

		/// <summary>
		///     Hand-reviewed: the importer must not overwrite this row's fields. Set from the editor, never by an
		///     import. See <see cref="ConversationNode.Verified" /> - same contract, same <c>--force</c> escape.
		/// </summary>
		public bool Verified { get; set; }

		/// <summary>
		///     The per-goal objective detail (the @609 region of the FullChar snapshot tails) - one row per goal of the
		///     quest. A quest can have several goals each with its own world marker + objective text (the beach starter
		///     has two: 1448 + 3490). Distinct from <see cref="TextRefs" /> (the journal header); these drive
		///     QuestObjectiveRegion generation, so the snapshot's objective region builds from the DB.
		/// </summary>
		public virtual ICollection<QuestGoalDefinition> Goals { get; set; } = [];
		public virtual ICollection<QuestTextRef> TextRefs { get; set; } = [];

		/// <summary>
		///     This quest's own reward set - the journal record's obj1 (granted) / obj2 (choose-one) lists, harvested.
		///     The line's reward, previewed on every non-last step, is <c>Line.Rewards</c> from the client catalog.
		/// </summary>
		public virtual ICollection<QuestRewardOption> Rewards { get; set; } = [];

		/// <summary>
		///     True once this quest's Quest_c record scalars are known, i.e. the record can be generated - either
		///     harvested from a captured journal (82 quests) or derived for a quest that had everything else
		///     (<c>ImportOps.SeedDerivedSnapshotRecords</c>, 17 quests).
		/// </summary>
		/// <remarks>
		///     It cannot be inferred from the Snapshot* columns: every one of them is legitimately 0 on some captured
		///     record, and <see cref="Level" /> is carried by plenty of definitions with no record scalars at all.
		///     <para>
		///         Not redundant with <see cref="ObjectiveRegionVerified" />, even where the two agree on every shipped
		///         row. They are independent facts about different halves of the snapshot portion, set by different
		///         processes: this one says the record can be built, that one says the @609 region provably rebuilds.
		///         A capture whose region does not rebuild separates the pair into (record yes, region no), and
		///         excluding that quest is the entire reason <see cref="ObjectiveRegionVerified" /> exists: an
		///         unreproducible region takes the whole login down (statlist.cpp:134), while a missing record just
		///         means the quest cannot be offered.
		///     </para>
		/// </remarks>
		public bool HasSnapshotRecord { get; set; }

		/// <summary>
		///     True when the quest's @609 objective region, rebuilt from these rows, reproduces the captured bytes
		///     exactly. Set by the importer, which does that comparison per quest at harvest time
		///     (<c>ImportOps.VerifyObjectiveRegions</c>).
		/// </summary>
		/// <remarks>
		///     This is what gates a quest into the login snapshot. Shipping an objective region we cannot reproduce
		///     desyncs the client and asserts it on statlist.cpp:134, and the region still has undecoded fields - so
		///     "some goals were harvested" is not good enough, only "the bytes come out identical" is. The flag flips
		///     itself on as decoding improves; no code change is needed to widen coverage.
		/// </remarks>
		public bool ObjectiveRegionVerified { get; set; }

		/// <summary>
		///     The line's quest catalog - every step of this quest's line, ordered by <see cref="JournalSequence" /> -
		///     as CSV. Not this quest's goals and not its chain: quest 10238's list is line 241's eight quests at
		///     sequences 1-8, while its own goals are [1448, 3490]; 13479's is line 1054's four. A fresh character with
		///     nothing completed carries the whole list, so it is static line data.
		/// </summary>
		/// <remarks>
		///     Vestigial, and deliberately kept only as a fallback: <c>QuestIndexRegions.ForQuests</c> prefers the
		///     catalog the caller loads from the DB (the line's quests, ordered by sequence) and reads this only when
		///     none was passed, which is the two hand-seeded quests. Do not harvest it for more quests - the DB already
		///     derives it.
		/// </remarks>
		[MaxLength(128)]
		public string? IndexQuestLineQuestIds { get; set; }

		// Quest index region: the area table's marker + three lead words + trailer word. Beach 10238: marker = the
		// one-element list-frame word, fields 1/7/0, trailer 0x156E7, and area 241's 8 quests as the table body (see
		// IndexQuestLineQuestIds - the body is the area catalog, not a 1..n goal index). The first two "fields" are the
		// block's StepNumber / StepsAfter - the held quest's step in its line and the steps after it, i.e. progress,
		// which is why QuestGoalWiring derives them from JournalSequence and the line's length rather than harvesting
		// them.
		//
		// IndexMagic is the area's `ACGHash` token, not a per-quest magic: the client reads it through `FUN_005d7190`,
		// which only checks the four bytes are `[0-9A-Z_]` (see docs/re/questc-region-decode.md). Area 241 = "W6QT",
		// 1054 = "RRL3", 1772 = "B0DN", 1834 = "3734" - stable across every character and capture. The QuestJournal
		// carries the same value in its area header, which is where the importer harvests it from; in a multi-quest
		// index each held quest contributes its own block keyed by this. Stored per quest because that is the grain
		// both regions consume it at.
		public uint IndexMagic { get; set; } // 4 bytes string
		public uint IndexMarker { get; set; }
		public uint IndexTableField1 { get; set; }
		public uint IndexTableField2 { get; set; }
		public uint IndexTableField3 { get; set; }
		public uint IndexTrailerWord { get; set; }

		/// <summary>
		///     The quest's sequence word in the completed journal. A property of the quest, not of its position:
		///     measured over the corpus, all 1975 completed quests carry the same value everywhere they appear, across
		///     characters and areas. It usually equals the 1-based ordinal within the area (73346 of 74392 entries) but
		///     not always - 182 areas list every quest with 0, and 260 more are mixed - so it is harvested, never
		///     generated.
		/// </summary>
		/// <remarks>
		///     Probably a chapter index within a quest series ("Letter to the King I / II"), but that is unverified, so
		///     the name says what it is rather than what it likely means.
		/// </remarks>
		public uint JournalSequence { get; set; }

		/// <summary>
		///     The quest's level, carried in the QuestFullUpdate as the u32 after the second questId - a quest
		///     property, constant across its lifecycle. The values track level, not state: Tortage starter quests
		///     1/3/4/5, open-world zones 23/24, endgame 80, and two quests in the same zone can be 23 and 80. It is
		///     never the QuestIIR StateActive value 2.
		/// </summary>
		/// <remarks>
		///     Working interpretation: the quest's optimal level - what the J-panel colours the title against (red when
		///     the character is under it, green at it, grey when over-levelled). The captured journals fit: an L1
		///     character holds 1, L23 holds 21/22/23/30, L24 holds 22/23/24/30, L80 holds 70..80. Not proven from
		///     captures (the colour mapping is client-side), but nothing contradicts it.
		///     <para>
		///         Harvested from two packets for the same quantity: the QuestFullUpdate's level word
		///         (<c>QuestDefinitionExtractor</c>) and the Quest_c journal record's word @+0x10; they agree exactly
		///         where both are present (10238 -&gt; 1/1, 13479 -&gt; 3/3). 0 = never seen; the sender falls back to
		///         <c>WorldQuest.DefaultQuestLevel</c>.
		///     </para>
		/// </remarks>
		public uint Level { get; set; }
		public uint MarkerX { get; set; }
		public uint MarkerY { get; set; }

		// --- Snapshot-tails generation data: values that live only in the FullChar snapshot's quest region, not the
		// QuestFullUpdate/Journal packets. They let the snapshot's Quest_c record + quest index generate from the DB
		// with no literals.

		/// <summary>
		///     The Quest_c record's word @+0x118: the level the quest's difficulty band is centred on, named for what
		///     its one client consumer does with it. The journal's band function
		///     <c>FUN_0091f6f0(quest, characterLevel)</c> centres the band here (falling back to <see cref="Level" />
		///     @+0x10 when this is 0), widens it by the record's @+0x114 tolerance, and returns the quest's colour -
		///     on-level, above, far above, below, far below. A level-adjacent value: across the captured journals it
		///     runs level..level+5 (L80 -&gt; 80-83, L24 -&gt; 19-30).
		/// </summary>
		/// <remarks>
		///     It is also the level the quest's rewards are scaled to. Both reward pairs the record carries
		///     (<see cref="MoneyReward" />/<see cref="XpReward" /> and <see cref="FieldF8" />/<see cref="FieldF0" />)
		///     have a money:xp ratio that is a function of this field and not of <see cref="Level" />: grouped by
		///     BandLevel the within-group spread is 0.0032, grouped by Level it is 0.0702, and the two disagree on 20
		///     of the 30 rows that carry a pair.
		/// </remarks>
		public uint BandLevel { get; set; }

		/// <summary>
		///     The Quest_c record's word @+0x14 - a bitfield, and a property of the quest line rather than the quest:
		///     constant per quest across 2626 captured records, and constant across every quest of the same line in
		///     all 90 lines that have one. Across the 99 quests with a captured record it takes five values (2, 10, 18,
		///     26, 1042), every one ≡ 2 (mod 8): bits 0-2 are fixed <c>010</c> and bits 3, 4 and 10 toggle. It is not
		///     a goal count - 2 appears on records carrying 1, 2, 3 and 6 goals.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Bit 4 (0x10) = the quest may not be shared. The journal's detail pane enables its Share button as
		///         <c>~(flags &gt;&gt; 4) &amp; 1</c> (consumer <c>FUN_01617260</c>); proven in-client by sweeping the
		///         column through all five observed values. The corpus fits: set on the Destiny steps, the Alchemist
		///         tier-4 progression and the Khitai/faction content (37 of 82), clear on ordinary side quests (45).
		///         Every bracket tag that carries it is Khitai/RoTG content and every tag without it is base game, but
		///         2 of the 14 Northern Grasslands lines lack it, so expansion is an ~86% correlate of "not shareable"
		///         rather than the definition.
		///     </para>
		///     <para>
		///         Bit 10 (0x400) = the quest may not be deleted. The sibling of bit 4, one widget along:
		///         <c>~(flags &gt;&gt; 10) &amp; 1</c> drives the Delete button. It is overridden for game masters:
		///         <code>
		///         if (flags &amp; 0x400)  enabled = (GmLevel != 0);   // stat 0x0D7
		///         else                enabled = 1;
		///         </code>
		///         so on a GM account the button is force-enabled whatever the bit says, and an in-client check of it
		///         must run on a non-GM account. Proven by A/B on one quest (10238 "The Slaver", non-GM account): flags
		///         1042 -&gt; Delete greyed, flags 18 -&gt; Delete enabled. The corpus agrees: bit 10 is the Tortage
		///         Destiny storyline plus the Alchemist progression, i.e. the quests retail refuses to let you abandon.
		///     </para>
		///     <para>
		///         Bit 3 (0x08): the client never reads it. A bit test has two spellings and a scan for one is blind to
		///         the other: the mask form (<c>TEST byte ptr [reg+0x14], 8</c>, <c>DumpQuestLineFlagBits.java</c>) and
		///         the shift form (<c>SHR reg,3; TEST CL,1</c>, <c>FindBitShiftAtOffset.java</c>), which is the one
		///         bits 4 and 10 are written in. Over the whole image the shift form gives 33 sites for bit 4 and 12
		///         for bit 10, both including <c>FUN_01617260</c> - and 12 for bit 3, of which eleven are
		///         <c>SAR reg,3</c> pointer arithmetic in container code and the twelfth a protobuf serializer testing
		///         its own has-bits; the mask form adds only stack locals and iostream internals. So it is authoring
		///         metadata the record carries and the client ignores, like <see cref="FieldF0" />/<see cref="FieldF8" />.
		///         Our sender echoes the captured value per line. It is 6 quests: 12381, 12583, 12889, 13473, 16004,
		///         16183. No server-side meaning fits: reward-less lines, zone tags, goal count or kind, line tier,
		///         parallel objectives and type-5 goals each fit the six and also fit most of the quests without it.
		///     </para>
		/// </remarks>
		public uint QuestLineFlags { get; set; }

		/// <summary>
		///     @+0x20 - the quest's money reward, the twin of <see cref="XpReward" /> (u64 money at 0x20, u32 xp at
		///     0x28, adjacent in the struct). Stored raw as the wire carries it, which is in tin - 100 tin = 1 copper,
		///     100 copper = 1 silver. The client's journal prints it on the <c>Money:</c> line (established in-client by
		///     driving the value and reading the panel): quest 13507's 214 renders "2C 14T", 111200 renders "11S 12C"
		///     and 250000 renders "25S". 21 of the 99 captured quests carry a non-zero one, constant per quest across
		///     every character and capture; the set is a subset of the quests carrying an XP reward.
		/// </summary>
		public ulong MoneyReward { get; set; }

		/// <summary>
		///     @+0x28 - the quest's experience reward: the number the client's journal renders as "XP:" in a quest's
		///     detail pane, established in-client by driving quest 13227's column to 5553, 0 and 7777 and reading
		///     "XP: 5553", "XP: 0" and "XP: 7777" back. Populated on the last step of a quest line (25 of 26 captured
		///     cases); see <see cref="FieldF0" /> for the other steps. It scales with level - level 23 -&gt; 900..4981,
		///     level 30 -&gt; 5553, level 70 -&gt; 15421, level 80 -&gt; 69337..138674, the level-80 values being exactly
		///     {1, 4/3, 5/3, 2} x 69337.
		/// </summary>
		public uint XpReward { get; set; }

		/// <summary>
		///     The Quest_c record's @+0x2c - the first quest of the series this one belongs to, which is the quest's
		///     own id for a standalone quest (84 of the 99 captured records) and an earlier step of the same chain for
		///     the rest: 10915 and 12371 both carry 10238, the beach starter, and both appear in its
		///     <see cref="IndexQuestLineQuestIds" /> list; 12068 carries 12067; 13986 carries 13984. Harvested, never
		///     derived - only the head carries <see cref="IndexQuestLineQuestIds" />, so a member has nothing locally
		///     that names its series, and <see cref="NextQuestId" /> gives successors rather than the start. 0 = never
		///     captured; the builder then falls back to the quest's own id, which is right for a standalone quest.
		/// </summary>
		public uint ChainHeadQuestId { get; set; }

		/// <summary>
		///     @+0xec: 1 in every one of the captured records - no quest varies it. Kept as a column rather than a
		///     literal so a capture that ever disagrees shows up as data, not as a wrong constant baked into the builder.
		/// </summary>
		/// <remarks>
		///     Because it is constant where present and 0 where not, <c>FieldEc == 1</c> is exactly
		///     <see cref="HasSnapshotRecord" /> - zero mismatches over all rows. Any cross-tab against it is really a
		///     cross-tab against "was this quest captured", so it is useless as a correlate and looks exactly like a
		///     useful one.
		/// </remarks>
		public uint FieldEc { get; set; }

		/// <summary>
		///     @+0xf0 and @+0xf8 - the line's xp and money reward, previewed on every step of a line except the last.
		///     The journal renderer <c>FUN_01617260</c> reads these two as the XP/money it shows whenever the quest is
		///     not the last entry of its line's list, and <see cref="XpReward" />/<see cref="MoneyReward" /> (+0x28 /
		///     +0x20) when it is. So the two pairs are one thing in two slots - the same rule as the four item lists,
		///     obj1/obj2 vs obj3/obj4 (<c>QuestRewardLists</c>). Our sender echoes what was captured per quest.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The pairs are populated on disjoint rows: per quest and per line, @+0xf0 only 30, @+0x28 only 20,
		///         both 1 (quest 13479, where the XP agrees exactly: 80 == 80; its record is gated byte-exact by
		///         <c>QuestcRegionTests</c>). A line carries one reward, in one slot pair or the other. Positionally,
		///         over the 14 carriers whose line is fully ordered by a captured <see cref="JournalSequence" />, the
		///         carrier is the first step 12 times, the last 0 times, and a middle step twice (lines 1260 and 1893,
		///         both 3-step); sibling steps carry 0, not the carrier's value.
		///     </para>
		///     <para>
		///         @+0xf8 tracks @+0xf0 through the same level-keyed factor that relates <see cref="MoneyReward" /> to
		///         <see cref="XpReward" /> (keyed on <see cref="BandLevel" />: 0.267 at 20, 0.292 at 21, 0.333 at 23,
		///         1.10 at 80, agreeing to four decimals where a band group contains both pairs), but 99 samples do not
		///         close the law - do not derive one from the other.
		///     </para>
		///     <para>
		///         Not the client catalog's per-line <c>xp_reward</c>/<c>cash_reward</c> (RDB 1000104,
		///         <c>--report rdb-quest-catalog</c>): that field is populated on 26 of 2,181 lines, our 31 carriers sit
		///         on 31 other lines, and a magnitude that repeats (45800 on three Thunder River lines) is not an
		///         identity. Nor is <c>QuestGiverList.MainQuestReward</c> a candidate: it is the item reward list
		///         (imported as <see cref="QuestLineReward" />) and carries no xp or money amount.
		///     </para>
		///     <para>
		///         @+0xf0 additionally selects which spelling the quest's @609 objective region uses for its trailing
		///         text: the beach starter's goal 1448 carries <c>&lt;remoteformat …&gt;&lt;/remoteformat&gt;</c> in all
		///         15 captures whose record has 150 and the 21-byte-shorter <c>&lt;localized … flags="" …&gt;</c> in all
		///         5 with 175. The importer's region verification groups on this.
		///     </para>
		/// </remarks>
		public uint FieldF0 { get; set; }

		/// <inheritdoc cref="FieldF0" />
		public ulong FieldF8 { get; set; }
	}

	/// <summary>
	///     The goal's own kind tag, carried on the wire as <see cref="QuestGoalDefinition.Separator" /> - the verb the
	///     objective asks for. Distinct from <see cref="QuestGoalType" />, which is our server-trigger wiring: this
	///     one comes from the client's data and is the evidence that wiring is derived from. Persisted as an int;
	///     ordering is stable.
	/// </summary>
	/// <remarks>
	///     Each value's reading is confirmed against the goal's own objective text (<c>--report ldb-quest-text</c>
	///     prints the tag beside the text for all 168 goals). The tag describes the goal, not its target: quest 12371
	///     (Shipwrecked V) has two goals referencing the same item id with the same count that still differ -
	///     "collect 3 quarry bricks" is 23 and "deliver 3 to Turach" is 20.
	///     <para>
	///         Values seen in the corpus only - this is not the client's full enum, and the gaps are unmapped rather
	///         than absent. There is no kind 0: across all 158 snapshot goals and all 111 full-update goals the tag
	///         takes 5/9/10/12/20/22/23/24/25 and nothing else, so a 0 on a row means the tag was never harvested.
	///         <c>Separator</c> keeps its wire-derived name on the row because it is a captured field; this enum is
	///         the meaning.
	///     </para>
	/// </remarks>
	public enum QuestGoalKind : uint
	{
		// Craft the named item: all 9 goals read "Make <item>" - Violet Cureall, Ambrosial Gold, Quicksilver Spirits,
		// Khat Blackbeer, Darkwinter Wine, Bitterbrisk, Visionwine, Musewater, Ale of Auspice (quests 13141/13143/13145).
		// The item names alone suggest "acquire one"; the verb says these are recipe objectives.
		Craft = 5,

		// Region-scoped: the target is a bounded-area reference (TrailingCategory 51200), so the trigger volume is
		// recoverable. Only 3 goals carry one, and only one of the three is a reach - "Enter the jungle" (1448). The
		// others are "Overcome the Emperor's Manifested Nightmares" (11712, count 3) and "Locate the Red-Hand Leader"
		// (14073). So the region scopes the objective ("3 of something in there") as often as it is the objective, and
		// this tag does not by itself make a goal a ReachArea.
		RegionScoped = 9,

		// Kill: the target is an npc_template (51000) or a monster category (51100). The cleanest value in the set -
		// all 30 goals are kill verbs (kill, beat, cleanse, destroy, slaughter).
		Kill = 12,

		// Deliver the item to someone - it leaves the player rather than arriving. All 13 goals read deliver/give/bring.
		// The row carries the item but never the recipient; the recipient is named in the objective text, which is how
		// these are wired (ImportOps.TypeDeliverGoalsFromRecipient).
		Deliver = 20,

		// The generic kind - by far the largest (60 goals) and the one with no single verb: vanquish, talk, use, find,
		// speak, ask, report, enter, escort, extinguish, investigate, kill, lead, order, replace, win, destroy, get.
		// A goal lands here when nothing more specific applies; it usually carries a map pin, but "reach" is not the
		// verb.
		Generic = 22,

		// Collect a quantity - the bulk of the gather objectives (44 goals: get, collect, gather, recover, remove,
		// search, steal), and the only kind carrying large counts (Poisonous Gland x20, Shadeleaf x10, and a x50).
		Collect = 23,

		// Use a named object. One goal: "Use the Vial to Capture Gas" (11252).
		Use = 10,

		// Investigate a place/object. One goal: "Investigate the Ancient Gateway" (8278).
		Investigate = 24,

		// Destroy a world object, and it takes a count: "Destroy Bowl of Set" (x7 separate goals in quest 13473),
		// "Destroy the Wolves' Saddles" (count 2), "Destroy the Wolves' Weapons" (count 5). Distinct from Kill, whose
		// target is an NPC template.
		DestroyObject = 25
	}

	/// <summary>
	///     Our server-trigger wiring for a goal - which live event advances it. Distinct from
	///     <see cref="QuestGoalKind" />, which is the client's verb; this is what the server can actually fire on, and
	///     the two only line up where a trigger exists.
	/// </summary>
	public enum QuestGoalType
	{
		/// <summary>
		///     No server trigger: the goal is stored, rendered and counted, and nothing advances it. This is the
		///     un-typed default for the catalog's thousands of goals, and the honest type for a goal whose verb cannot
		///     yet be triggered: <see cref="QuestGoalKind.Craft" /> (tradeskills), <see cref="QuestGoalKind.Use" />
		///     (item-use-on-target) and <see cref="QuestGoalKind.DestroyObject" /> (a destructible world object we do
		///     not hold) all land here rather than being bent into the nearest trigger that would fire early.
		/// </summary>
		/// <remarks>
		///     0 must never double as a real trigger: a default-typed row would then false-fire on that trigger's
		///     event with no verb and no target. The rows that genuinely are kill goals get <see cref="Kill" /> from
		///     their own verb (<c>ImportOps.TypeKillGoalsFromKind</c>, <see cref="QuestGoalKind.Kill" />).
		/// </remarks>
		None = 0,
		LootFromNpc = 1, // loot the TargetName NPC's corpse bag (the quest item is RDB content; the bag source is the key)
		TalkToNpc = 2, // talk to (open the greeting of) the TargetName NPC
		Escort = 3, // escort the TargetName NPC to a destination
		ReachArea = 4, // reach a map location - solved when the player moves within range of the goal's (MapId, MarkerX, MarkerY) point (no TargetName; e.g. Shipwrecked's "Enter the jungle")
		CollectItem = 5, // collect N of the item in TrailingRefId, one per interaction with a quest-pickup world object (QuestItemEffect); e.g. Shipwrecked V's "get 3 stone bricks" (item 193400)
		Kill = 6, // kill the NPC in TrailingRefId (a 51000 npc_template reference), or the TargetName NPC

		/// <summary>
		///     Use the item in <see cref="QuestGoalDefinition.TrailingRefId" /> (an LDB 50200 item reference) -
		///     solved by right-clicking it out of a bag, which is the one thing the goal's own row states.
		/// </summary>
		/// <remarks>
		///     Deliberately not a <see cref="CollectItem" />. The kind's verb is <c>Use</c>, and the corpus' single
		///     goal - "Use the Vial to Capture Gas" (11252, item 4349109 <c>Po-Sha Potency Vial</c>) - is one the
		///     player is handed and then has to use, so counting its arrival would complete the objective the moment
		///     the quest giver passed it over. The trigger is the use itself.
		///     <para>
		///         The item's own on-use effect is unaffected: the goal ticks off the same <c>UseInventoryItem</c>
		///         choke point that applies the consumable's spell, after it, so an item that is both a consumable and
		///         a quest objective does both.
		///     </para>
		/// </remarks>
		UseItem = 7,

		/// <summary>
		///     Use the world object in <see cref="QuestGoalDefinition.TargetWorldObjectId" /> - the door, gate or
		///     lever the objective is about. Solved by the same <c>UseDynel</c> BCC a zone-line door takes, not by
		///     standing near it.
		/// </summary>
		/// <remarks>
		///     Deliberately not a <see cref="ReachArea" />: the gate object of "Unlock the Acheronian gate"
		///     (12369/3492) stands on the goal's marker, so a reach would complete the objective the moment the player
		///     walked up to it - before looting the key, and without ever touching the gate. Retail's own order
		///     settles it: the player targets the gate, sends the use, and only then does the goal tick.
		///     <para>
		///         A goal of this type may also name a <see cref="QuestGoalDefinition.QuestItemId" />, which the
		///         player must be carrying for the use to count - the key half of "unlock". The item is not consumed;
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
		QuestLineTitle = 5, // category 50300 - the quest line's title (shared header)
		RegionName = 6, // category 52000, id 4 - region name (shared header)
		MapName = 7 // category 52000, id = map id - map name (shared header)
	}
}
