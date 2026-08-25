# Quest_c region (0x737A6DF9 FullChar snapshot) — deep decode (2026-07-05)

The Quest_c region is the quest JOURNAL at the front of the snapshot tails. `QuestcRegionCodec` models it and
round-trips the **beach create template** byte-exact, but the beach template is the *simplest* possible journal
(fresh char: full quests, empty objective lists, no progress). This doc records the FULL structure recovered
from the client reader (Ghidra) + validated against NecroL80's lvl80 login (27 quests, real progress), so the
generator can eventually emit arbitrary journals.

## Readers (Ghidra `AgeOfConan.exe`, project `tools/ghidra/output`)

- `FUN_0091f5c0` — **List-A driver**: reads `u32 count = 0x3F1*(records+1)`, then loops `records` times, each
  iteration reading one `u32` (the record's listKey) + calling the per-record reader.
- `FUN_0091df50` — **per-record reader**. Read primitives: `FUN_0048f900`=ReadU32, `FUN_0048f840`=ReadString
  (`[u16 len][utf8]`), `FUN_0048f720`=ReadU64, `FUN_00cc0540`=Read4×U32. Post-obj4 calls are `FUN_009202d0`
  (std::sort introsort) + vector-push, NOT stream reads.

## `ACGHash` — the 4-character token, and why it is never a magic

`FUN_005d7190` is the client's **ACGHash** stream reader, and it is what every "4-character magic" in the quest
packets actually goes through. It reads one `u32`, checks each of the four bytes against `[0-9A-Z_]`, and on a
mismatch logs `Read invalid hash "<...>" from stream` under the `ACGHash` debug channel. It compares against
**nothing** — there is no expected value, because the value is an identity token, not a marker.

That names three things this repo had decoded separately as random tokens:

- the `QuestJournalIIR` word read as a `"RRL3"` **record-list magic** — really the AREA's token (#544);
- `QuestObjectiveGoal.Reserved`'s tokens, incl. the `WTHD` block below (#546);
- the quest index's per-block `magic` (`QuestDefinition.IndexMagic`), which is the same area token again — a
  quest's index block and its journal block carry one value, keyed by `AreaId`.

⚠️ So: **a four-byte field of printable uppercase-alphanumerics in a quest packet is an ACGHash.** Asserting the
one value the first decoded capture carried is the same mistake as asserting a `0x3F1` list frame, and it failed
the same way — 31 of 38 captured journals rejected.

Observed area tokens: 241 → `W6QT`, 1054 → `RRL3`, 1772 → `B0DN`, 1834 → `3734`.

## `QuestJournalIIR` (0x5A2FE45E) — reader `FUN_0091a4e0` (vtable slot 8 of `QuestJournalIIR_c`)

```
[u32 subop][u32 class][u32 playerHandle][u8 0x01]
<area block>                                FUN_00922c90
[u32 currentFlag]                           0 / 1
[ListFrame'd list of <area block>]          FUN_00798c70 — frame-ONLY, empty in all 38 captures
[u32 currentQuestId]                        0 = none
[ListFrame'd list of strings]               FUN_004d55b0 — the named quest's title + sub-line
[u32 currentOrdinal]                        0 = none
```

with the area block (`FUN_00922c90`)

```
[u32 areaId][string areaTitle][string mapName][string regionName]
[u32 acgHash][u32 unknown=0][u32 count]
count x [u32 ordinal][u32 questId][ListFrame'd list of 2 strings: title, sub-line]
```

Two notes the corpus alone could not settle. **The trailing five fields are not a "tracked-quest pointer".** They
had been read as `[u32 trackedQuestId][list A][list B]` with each list `[frame][count]`, which only fits a
one-quest journal: in every larger one, list A's "count" slot holds a quest id and list B's frame holds `0xBD3`.
The reader shows five independent fields instead, and both captured shapes then fall out exactly — `(0, empty, 0,
empty, 0)` for a single-quest journal and `(1, empty, questId, 2 strings, entryCount)` naming the LAST entry for
every larger one. **And a string list here is frame-ONLY** — one `(count+1) * 0x3F1` word and then the strings,
with no separate count word, unlike the record lists which carry both. That is why the per-entry `0xBD3` read as a
"ChapterTag": it is `ForCount(2)`, framing the entry's title + sub-line.

## Record grammar (validated byte-exact on NecroL80 rec0, snapshot offset 11024)

```
[listKey=questId]
u32 ver(=1)
u32 questId          (+0xc)
u32 level            (+0x10)  the quest LEVEL (see below); modelled as QuestcRecord.Level
u32 field114         (+0x114)  the level band's TOLERANCE (see below)
u32 field118         (+0x118)  the level the quest is FOR (see below)
u32 lineFlags        (+0x14)  a flag word of the quest LINE - NOT a goal count (see below)
string title, summary, area, region1, region2   # cats 50301/50302/50300/52000/52000
u32 fieldEc          (+0xec)
u32 areaId           (+0xcc)
u32 field2c          (+0x2c)           # beach codec MISLABELS this "questId2" and ASSERTS ==questId (beach-only!)
u32 field11c         (+0x11c)
u64 money            (+0x20)  the quest's MONEY reward, in TIN (see below)
u32 xpReward         (+0x28)  the quest's EXPERIENCE reward (see below)
objlist obj1, obj2                     # each: [u32 count][count × ObjRec]
u32 fieldF0          (+0xf0)  NOT the journal's XP (see below)
u64 fieldF8          (+0xf8)  NOT the journal's Money (see below)
objlist obj3, obj4
progress-sublist                       # <-- the piece the beach template never exercises
```

## ⭐ Decoded: the four `objlist`s are the quest's REWARD ITEMS (#464)

`ObjRec` = **16 u32** on the wire, and they were carried as *"16 raw u32s, unvalidated"* for months for one
reason: the beach starter — the only journal record a golden covers — carries **none of them**. Over the whole
corpus they are populated on **7,347** elements across 3,741 records (`--report quest-rewards`), and the element
is one **reward item**:

```
[u32 instance][u32 questId]
[u32 1000620][u32 templateId][u32 key1..key4]     # ACG template1 + its per-template TEA key
[u32 1000620][u32 templateId][u32 key1..key4]     # ACG template2 + its key
[u32 level][u32 quantity]
```

Four independent things pin it, and no two of them share an assumption:

1. ⭐⭐ **Decryption, with a control.** All **101 of 101** distinct templates open with the key the wire states
   beside them — each decrypting to a body that declares one of the catalog types and its own id — and all
   **101 of 101** FAIL with a bogus key. The same test that pinned `keyN ↔ templateN` for `ProtoAcgItem` (#752).
2. **The client's own field grouping.** The reader does not store the sixteen words in order: it reads
   `w2, w3` and stores them at `[7], [6]`, then `Read4×U32` into `[2..5]` — i.e. it assembles `(type, id, key)`
   into ONE six-word reference object, twice. The "scramble" the note below used to describe IS the carve.
3. **The echo.** `w1` equals the owning record's own `questId` on 7,347 of 7,347.
4. **`1000620`** is the client's item-template catalog (the RDBH variant of 1000020, `rdb-type-catalog.md`), and
   it is the type word on both halves of every element.

The two trailing words are named from what they are **attached to**, not from their values: `level` is constant
per TEMPLATE (101/101), so it belongs to the item; `quantity` is constant per (quest, template) (161/161) but not
per template, so it belongs to the reward.

⚠️ What the four lists mean **relative to each other** — guaranteed vs choice vs class-specific — is *not*
decoded. The wire order is `[obj1][obj2][fieldF0][fieldF8][obj3][obj4]` and nothing in the corpus separates them,
so `QuestcRecord` keeps their positional names.

⭐ They are also a CONTENT source: 67 of the 101 templates are **not** in our `Items`, and each arrives with the
key that would admit it (an item enters the catalog when a capture yields its `Data1..4`). Open work tracked in
Gitea.

Reading them as 16 raw u32 in wire order was byte-exact and still is; `QuestRewardItem` is the typed form.

### ⭐⭐ What the FOUR lists mean, and the journal's own rule for showing them (#893)

Two sources that share no assumption settle it:

**The client's journal renderer.** `FUN_01617260` (the QuestJournal view) fills ONE reward panel from a
`Quest_c`, and which pair it reads is a branch on a single test:

```
line   = MainQuest_c for quest->lineId  (FUN_008107d0)
bVar9  = line.entries[last].questId == quest->questId      // +0x1c..+0x20: (sequence, questId) pairs
if (bVar9)  show XP @+0x28, money @+0x20, obj1 -> reward view, obj2 -> OPTIONAL reward view
else        show XP @+0xf0, money @+0xf8, obj3 -> reward view, obj4 -> OPTIONAL reward view
```

`FUN_016181f0` does the filling: the first list it is handed goes to the reward view, the second to the
*optional* view, and the `RewardPlusOne` marker shows iff that second list is non-empty (`MainQuestRewardView` /
`MainQuestOptionalRewardView` / `MainQuestRewardPlusOne`, plus the `MoreThan3Dock` layout switch). So within each
pair the first list is **guaranteed** and the second is **choose-one**.

⚠️ `bVar9`'s list is the **server-sent** area list — `MainQuest_c::WriteToStream` (`FUN_00921db0`) serialises its
`+0x1c` vector as 8-byte `(sequence, questId)` pairs, i.e. the index region's per-area block — and that list
equals the line's whole catalog on 3,300 of 3,741 captured blocks (never shorter). So in practice "last entry" is
the line's last step.

**The client's quest catalog.** RDB 1000104's `MainQuest.reward` / `optional_reward` (imported as
`QuestLineReward`) are the LINE's reward, and its own test lines are named *"Three NonOptional rewards"* / *"1
NonOptional + 2 Optional rewards"*. Over the corpus:

| | records | |
|---|---:|---|
| `obj3`/`obj4` populated | 1,811 | **1,553 equal the line's catalog lists exactly, 0 differ**, 258 on a line the catalog holds no items for |
| `obj1`/`obj2` populated, last step | 891 | 574 equal the catalog lists (the line reward **folds** into the last step's own set), 317 differ |
| which set a quest populates × last step | 68 quests | set A ⇔ last 11/0, set B ⇔ mid 0/55, both on 2 mid steps (10928, 17403) |

And the one captured completion (quest 10928, #815): retail **offered exactly its obj2** and granted the pick,
while its obj3 — the line's Exponent's Gloves — was not granted, because the line was not finished.

So:

* **`obj1` / `obj2` = THIS quest's own reward** — granted / choose-one — paid at its completion. Server-side data
  the catalog never states (the `Quest` record has no reward field), harvested per quest into
  `QuestRewardOptions` (`Optional` = which list).
* **`obj3` / `obj4` = the LINE's reward**, previewed on every step but the last, and it is the client catalog's
  own list — buildable for all 1,410 catalog lines. Two words the catalog lacks are harvested per (line,
  template): the **quantity** (constant per pair on 125/125; the three templates that vary across lines are
  currency tokens) and the **wire order** (`WirePos` — mostly the catalog's reversed, but 5 of 12 lines break
  that and no id/type/name sort fits, so it is retail's row order). The element's `level` is the item's own
  (`Items.Level`, 5,248/5,248).
* **No list is class-specific**: 16 quests captured under more than one class carry identical lists.

Built by `QuestRewardLists.ForQuest` and gated by `QuestRewardListsCorpusTests` — every captured record's four
lists rebuild **element-exact** from the seed (1,791 records). Residue, in `--report quest-reward-lists`: 17732 /
17733 are last steps whose obj2 is EMPTY against a 4–5-item catalog optional list (a class filter does not
explain it — 3238's list holds cloth for a necromancer), and line 3780's list is on the wire and not in the
catalog (its rows are inserted from the wire).

⛔ **Correction to #802.** *"Three functions in the whole image touch `Quest_c+0xf0`/`+0xf8` — the constructor,
the reader and the writer"* is wrong: `FUN_01617260` reads both, as the XP/money the journal shows on a
non-last step. #802's in-client sweep (8888 / 999999 changed no label) was run on a last-step quest, where that
branch never renders. The XP words follow the same split as the item lists: `+0x28`/`+0x20` are the quest's
own on the last step, `+0xf0`/`+0xf8` the line's on the others — and they are level-scaled on the wire (line
386's catalog XP is 715; 10830's records carry 715 in `+0x28` and 630 in `+0xf0`).


## ~~progress-sublist (per record, after obj4) — the real blocker~~ — RESOLVED 2026-07-20 (#384)

> **This section's conclusion was wrong.** What follows below is kept for the record; read this first.
>
> There is no "progress sub-list" and no opaque server block. The thing after `obj4` is the quest's own **@609
> objective region** — `QuestObjectiveRegion`, a codec this repo already had — and it is **interleaved per
> quest record**, not appended once after the whole list. `FullCharSnapshotSubClass` had always *written* it
> that way; only the reader was wrong, treating it as a separate top-level region.
>
> That parses the beach template fine because the beach journal holds exactly **one** quest, where "after the
> record" and "after the last record" are the same offset — the same single-element blind spot as the
> [`0x7E2` list-frame trap](../protocol/README.md). On any real journal the reader desynced at record 2 and
> threw `Quest_c record: marker 0x2 != 0x412`.
>
> The `WTHD` "magic" was a **mis-segmentation**: those bytes are `QuestObjectiveGoal.Reserved[5..6]`, the pair
> this repo already documented as "70099 / a 4-char tag". It has zero xrefs in `AgeOfConan.exe` not because it
> is opaque server data, but because **it is not a constant at all** — it is a random 4-character token,
> distinct in every entry (NecroL80's journal: 39 anchors, 39 distinct tokens — `WTHD`, `1LJL`, `UFWN`, `HIB4`,
> `C9PY`, …), the same flavour of random key the `<remoteformat key="...">` attributes carry.
>
> Verified by `ProgressedJournalSnapshotTests` across **all 141 captured snapshots** (beach L1,
> L23, L24, and both L80 journals), and gated in the suite by `ProgressedJournalSnapshotTests` — 141 bodies,
> 85 of them multi-quest, tails byte-exact.

## progress-sublist (per record, after obj4) — the original (incorrect) reading

`[u32 frame = 0x3F1*(k+1)][k × progress-entry]`. rec0 (quest 13907): `frame=0x7E2 ⇒ k=1`. Full byte map of the
one entry (offsets from NecroL80's snapshot):

```
+0  u32 flag        = 1
+4  u32 goalCount    = 12 (0xC)
+8  u32 playerHandle = 0x0802E5D4          # the character's own handle
+12 u32 questId      = 13907
+16 u32 goalId       = 6451
+20 u32              = 1
+24 u32 goalId       = 6451
+28 u32              = 1
+32 5× u32 = 0
+52 u32 id           = 70099 (0x111D3)
+56 'WTHD' (0x57544844) block: [WTHD][0][3042][171][171][0][0]   # server data, see below
+84 u32 strlen16?    = 100 ; then a remoteformat string id=6451 category=50304 (the GOAL description)
    then a second string: [u16 len=81] <localized id="8235" category="51000" flags="m" …>
    then ~7 more u32 fields (0x34A1.. flags/timestamps) before the next quest record
```

### Why this can't be fully generated from client RE

The **`WTHD` block is opaque server data**: the magic `0x57544844` is referenced NOWHERE in the client
(`FindConst` = 0 hits in either byte order), so there is no client reader to decompile for its internal
field semantics — it is stored/echoed as a blob. This is the same class of blocker as the server-held RDB
keys: the client never *interprets* these bytes, so their meaning isn't recoverable from `AgeOfConan.exe`.
Records are also VARIABLE (rec1 quest 6451 has empty header strings + is itself a goal-style entry), so a
faithful arbitrary-journal generator needs either a retail server-side spec or a large corpus of progressed
captures to infer the WTHD/progress fields empirically.

## What the beach template mislabelled

A progressed journal round-trips, and getting there was a naming correction rather than a decode: the structure
was already fully known, just attributed to the wrong owner.

- The fields the beach capture named for its own values are now `Field114`, `Field118`, `N` and `Field2c`, and
  the two beach-only asserts (`!=0x412`, `questId2!=questId`) are gone. Read/write order never changed, so every
  beach golden stayed byte-exact. What was called `Marker` is `QuestcRecord.Unknown14` — the value the beach
  template happens to carry in the goal-count slot, never something to assert against.
- The objective region is **per record** (`QuestcRecord.Objectives`), read and written with the record.
  `FullCharSnapshotSubClass` therefore holds no top-level `QuestObjectives` and does not re-parse that region at
  the boundary.

⚠️ The lesson is the recurring one: **a grammar pinned only by the single-element/degenerate capture is not
pinned**, and a "constant" that appears once per record is worth checking against a second capture before
calling it a magic.

## Static RE pass on the record's unnamed fields (2026-08-01)

What the client can and cannot tell us about `@+0x14`, `@+0x28`, `@+0xf0`, `@+0xf8` and `@+0x11c`. The short
answer: the STRUCTURE is confirmed from both sides, and the NAMES are not in the binary — anyone continuing
this should trace a consumer, not hunt for another string.

**The class.** RTTI is intact: the type descriptor `.?AVQuest_c@@` is at `021cf360`, its complete-object
locator at `020dc260`, and the vtable at `01fdfd18` — only three virtuals (`FUN_0091d090`, `FUN_0091ce50`,
`FUN_0091ce60`). Its source file is `quest.cpp`; the record's string pool sits right after the vtable.

**Reader and writer agree with our codec.** `FUN_0091df50` reads the record and `FUN_0091d260` writes it back
(the byte-swap-then-emit pattern), and between them they touch exactly the offsets `QuestcRecordCodec` models,
in the order it reads them — including `@+0x20`/`@+0x24` and `@+0xf8`/`@+0xfc` as the two u64 pairs. The reader
is NOT virtual (no pointer to it exists anywhere in the image) and has exactly two callers, `FUN_0091f5c0` and
`FUN_00917e20`, both thin parse-loop wrappers that never touch the fields.

**Why the names are not recoverable by string anchor.** The writer is label-free — it emits scalars with no
per-field assert, unlike e.g. `DispatchSetQuestMarker`, whose asserts name `i_cNPC` and `i_eMarker`. The quest
module's string pool names plenty of METHODS and one member, but nothing on this record:

| string | what it gives |
|---|---|
| `QuestHolder_t::UpdateCurrentProgress() m_pcCurrentQuest is NULL.` | the holder class + its member |
| `QuestGoal_c::SetParent()` / `SetSolvedTimes()` / `GetQuestLevel()` | goal API; "SolvedTimes" is the goal's progress count — and it is the goal record's `reserved[0]`, see [ldb-string-index.md](../rdb/ldb-string-index.md) (#550) |
| `QuestGoal_c::GetQuestLevel() … Returning Stat value` / `Returning Quest value` | the goal's level resolves from a STAT first, the quest second |
| `MainQuest_c::WriteToStream` | `mainquest.cpp` is a separate class from `quest.cpp` |
| `QuestRewardNotifyIIR_c`, `QuestRewardSelectionIIR_c` | rewards have their OWN IIRs |
| `QuestJournalIIR_c::ReadSubClass()`, `PatrolIIR_c`, `RDBQuestGivers` | neighbouring subsystems |

Two of those bear on the open question directly. Rewards being delivered by their own IIRs is evidence
AGAINST reading the level-scaled `@+0x28`/`@+0xf0`/`@+0xf8` as a reward carried on the journal record — it is
still the best fit for their magnitudes, but the client does not obviously need them there. And
`GetQuestLevel()` returning "the Stat value" or "the Quest value" says a goal's level has two sources, which is
the kind of level-adjacent pairing `@+0x118` (level..level+5) already hints at.

## ⭐ The LIVE goal reader is pinned, and it names two "reserved" words (#464, 2026-08-19)

The snapshot-side note above ("pinning the exact reader needs a dynamic trace") does not bind the LIVE path:
`QuestFullUpdateIIR::ReadSubClass` is **`FUN_00917e20`** (vtable slot 8; self-named by its own
`"QuestFullUpdateIIR::ReadSubClass() Wrong IIR_t version %d"` log and its `iir\questfullupdateiir.cpp` assert).
It allocates a `Quest_c` (0x120 bytes, ctor `FUN_0091cf10`) and hands the stream to **`FUN_0091df50`** — so the
live full-update body IS one `Quest_c` record, goals included, and the goal grammar can be read statically.

⛔ **CORRECTED (#464, 2026-08-25).** This section used to continue:

> Per goal the reader fills an 18-word (0x48-byte) struct from the record's last 16 u32, in this wire→offset map
> (`QuestObjectiveGoalCodec`'s field names): `n -> +0x00`, `goalIdRepeat -> +0x04`, `requiredCount -> +0x18`,
> `r0 -> +0x1C`, `r1..r4 -> +0x08..+0x14`, `r5 -> +0x30`, `r6 -> +0x34`,
> `subId/mapId/markerX/markerY -> +0x20..+0x2C`, `unknown1 -> +0x38`, `unknown2 -> +0x3C`
>
> — and concluded that `reserved[5]`/`[6]` are the goal list's SORT KEYS (the client `std::sort`s on `+0x30`
> then `+0x34`), and that `unknown1`/`unknown2` now had object offsets for a consumer hunt.

**That 16-word struct is not the @609 goal record.** It is the `Quest_c` record's **objlist** element — the
quest's reward item, decoded above as `QuestRewardItem`, whose last two words are the item's `level` and the
reward's `quantity`. Three things separate the two:

* the objlist is framed by a **plain u32 count** the client clamps to 100; the @609 region is ListFramed as
  `(goals + 1) * 0x3F1`;
* an objlist element is 16 words carrying **no text**; the @609 goal record is 21 words **plus two text records**;
* `FUN_0091df50` reads the four objlists as part of the `Quest_c` record, and the @609 region *follows* it.

So the four rounds of offset scans below were hunting a consumer for the wrong struct's fields, and the sort at
the end of the reader orders the `QuestGoal_c` objects at `+0xAC`/`+0xBC` — not anything from this region.

⭐ What is pinned, and is the honest starting point for the next attempt:

* `Quest_c` allocates **four** POD vectors (`+0x100`, `+0x104`, `+0x10C`, `+0x110`), one per objlist, plus **two**
  vectors of refcounted `QuestGoal_c*` at `+0xAC..+0xB0` and `+0xBC..+0xC0`.
* The `QuestGoal_c` objects **exist before the record is read** — `FUN_0091df50` only post-processes them
  (`QuestGoal_c::SetParent` = `FUN_009260c0`, then `std::sort` with comparator `FUN_0091ce70`) — so they are built
  from the client's own quest template, not from the wire.
* ⭐ **The waypoint UI reads those objects, never the wire PODs.** `WaypointQuestGoal` (`FUN_013a26c0`,
  `GUI\MapWaypointHandler.cpp`, keyed `m_cWaypoints.find( goalID )`) walks `+0xAC..+0xB0` and reads `+0x38`/`+0x3C`
  (required vs solved — the pair `CoreQuestHolder::QuestGoalEvent` names), `+0x5c` playfield, `+0x60/+0x64/+0x68`
  position, `+0x6c` flag, `+0x20` parent, `+0x80` name. Its waypoint types come from
  `Gui/Default/Waypoints/PF%u.xml` (`ParseWPType`, `FUN_0139e6f0`).

## ⭐ The @609 goal record's reader and its tail's consumer (#464, 2026-08-25)

The route the last paragraph pointed at — the semantic one — is the one that paid, and it needed no scan:
`QuestGoal_c`'s own stream reader **`FUN_00925dd0`** (already named above, but never read for its OFFSETS) lands
the 21 words on the object **from `+0x24` upward in wire order**: `instance +0x24`, `questId +0x28`,
`goalId +0x2c`, `n +0x30`, `goalIdRepeat +0x34`, `requiredCount +0x38`, `solvedTimes +0x3c`, the six reserved
words `+0x40..+0x54`, `subId +0x58`, `mapId +0x5c`, `markerX +0x60`, `markerY +0x64`, `unknown1 +0x68`. Then
it reads the TAIL — and the tail is not what the codec had:

```
was:   [u32 unknown2][u32 objectiveLen][objective][u16 len][text]
is:    [u8 -> +0x6c][u8 -> +0x6d][u32 -> +0x70][u16 len][objective][u16 len][text]
```

Two bools (`FUN_0048f6d0`), a u32, then the ordinary u16-framed `ReadString` twice. The old carve round-tripped
every captured body only because the u32 is 0 on all of them and no objective is 64 KiB long; the "flag word
`0x01000000`" was the first bool — exactly the "two adjacent BYTES with zero padding" the corpus had predicted.
`QuestObjectiveGoalCodec` now reads `[u8 hideMapMarker][u8 tailFlag][u32 tailWord]` and the objective as a
u16-framed string; `QuestGoalDefinition.Unknown2` became `HideMapMarker` (migration `QuestGoalHideMapMarker`,
which squashes the stored words to the bool).

⭐ **And the offsets are exactly what `WaypointQuestGoal` (`FUN_013a26c0`) reads** — it is the consumer the
register was hunting, already listed above as "`+0x5c` playfield, `+0x60/+0x64/+0x68` position, `+0x6c` flag":

* for every goal whose `+0x3c` (solved) ≠ `+0x38` (required) it takes `+0x5c` as the goal's playfield;
* if that is the current playfield **and the byte at `+0x6c` is 0**, it places the marker at the vector
  `((float)+0x60, 0, (float)+0x64)` — markerX/markerY on the ground plane — and hands `(float)+0x68` to the
  waypoint object as a **separate scalar** (`CustomWP_c` slot `[10]`, set in `FUN_0139f520` beside the position
  at `[6..8]`), so `unknown1` is marker GEOMETRY, not a coordinate;
* if the byte at `+0x6c` is **set**, it places nothing for a same-playfield goal — the **"no map marker"** flag,
  which is what its 17 carriers (dungeon objectives with degenerate `(1,1)` markers) always looked like;
* if the goal is on ANOTHER playfield it asks `FUN_0146a9f0(currentPlayfield, goalPlayfield)` where that
  playfield is reachable from here and marks that, whatever the flag says.

What is still open on this record: what the waypoint does with slot `[10]` — its reader was not found in the
`0139e000..013ab000` waypoint module by string, so `Unknown1` keeps its name with "radius" as the reading its
round 3..170 map-unit values, marker-only presence and Collect/Kill-only kinds support — and the two zero tail
fields (`+0x6d`, `+0x70`), which have no non-zero sample. Open work tracked in Gitea #464.

**Exhausted anchors, so don't repeat them** — all four were run against the WRONG struct's offsets per the
correction above (the vtable, the reader's callers, `[reg + 0xf0]` at 778 functions, `OFFS=24,38 NEAR=12` at
3,322 of 139,441 functions, `OFFS=10c,38` at 488), and the semantic route above superseded them.

## What the unnamed fields ARE (2026-08-01, from the client's quest catalog)

The static pass above ended with "the names are not in the binary — trace a consumer". They were not in the
binary; they were in the client's **data**. Cross-referencing the captured records against RDB type 1000104
([quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)) named three of the five and pinned the scope of
a fourth. The probe is 2626 captured records over 141 snapshots, covering 99 distinct quests.

**The control that made this tractable: every one of these words is DEFINITION data.** Per quest, across all
2626 records and every captured character, `@+0x14`, `@+0x28`, `@+0x2c`, `@+0xec`, `@+0x114`, `@+0x118` and
`@+0x11c` never vary; `@+0xf0`/`@+0xf8` vary on exactly one quest (10238, the known two-spelling variant) and
`@+0x10` on three. 97 of the 99 quests appear in more than one capture, so this is not a single-source
artefact. A definition field has a definition-side source, which is what pointed at the catalog.

### `@+0x10` — the quest LEVEL, not a state

It equals the DB `Level` on 96 of the 99 captured quests (12373, 17402 and 17403 differ by 1–2, i.e. the row
took its level from a different capture). Both writers already fed it a level. It was modelled as
`QuestcRecord.State` until this pass, which made `QuestcRegionTests` assert `Equal(1u, q.State) // active` on
the beach starter — passing only because that quest happens to be **level 1**. Distinct from
`QuestCodec.State` (`StateActive`/`StateCompleted`), a real state on a different message.

### `@+0x28` — the quest's EXPERIENCE reward (settled IN-CLIENT)

The client's journal renders a quest's detail pane with an `XP:` and a `Money:` line, and `XP:` is this field.

[V1: Faolan.ClientLauncher/Program.cs — driven live against a local server: quest 13227's column was set to
5553, then 0, then 7777, relogging between each, and the panel read back `XP: 5553`, `XP: 0`, `XP: 7777` with
nothing else changed. In the same runs `@+0xf0 = 8888` and `@+0xf8 = 999999` produced no change to either
label — `Money:` stayed blank.]

Its positional law still holds and is still unexplained: `@+0x28` is populated on the LAST step of a quest
line (25 of 26 captured cases) and `@+0xf0`/`@+0xf8` never are. It scales with level — level 23 → 900..4981,
level 30 → 5553, level 70 → 15421, level 80 → 69337..138674, the level-80 values being exactly
{1, 4/3, 5/3, 2} × 69337.

### `@+0x20` — the quest's MONEY reward (settled IN-CLIENT)

The twin of `@+0x28`: a u64 money at 0x20 and a u32 XP at 0x28, adjacent in the struct, and the two lines the
journal's detail pane prints as `Money:` and `XP:`.

[V1: Faolan.ClientLauncher/Program.cs — driving it live on a rendered quest, 111200 rendered `11S 12C` and
250000 rendered `25S`; the harvested 214 of quest 13507 renders `2C 14T`. So the unit is TIN, with 100 tin =
1 copper and 100 copper = 1 silver.]

⚠️ It was modelled as `Accept`, "likely acceptTime", and **emitted as a constant 0** — so every captured value
was thrown away. 21 of the 99 captured quests carry one, constant per quest across every character and
capture, and that set is a SUBSET of the quests carrying an XP reward: some quests pay experience only.

At level 80 the harvested pairs run money ≈ **1.10 ×** xp (152546/138674, 127115/115562, 101684/92449). That
is the same ratio `@+0xf8`/`@+0xf0` shows at level 80, which is the strongest hint yet that those two are the
same (xp, money) pair at a different scope — see below — though still not proof.

### `@+0xf0` / `@+0xf8` — NOT the journal's XP or Money

⚠️ **An earlier version of this document named these two `XpReward` and `CashReward`.** That was inferred from
the client catalog's `xp_reward`/`cash_reward` pair plus a magnitude coincidence, and the in-client test above
disproved it: with both set to large distinctive values the quest pane showed the XP from `@+0x28` and left
`Money:` blank. **A field-name match in a neighbouring data file is not evidence about the wire.**

#### ⛔ And they are not the LINE's reward either (#759)

The line-reward reading was the standing hypothesis and it is now **falsified three times over**, by
`--report rdb-quest-catalog`'s `LineRewardCheck`, which joins every carrier to the catalog's `MainQuest`:

1. **The populations are disjoint.** All 31 carriers sit on a line whose catalog reward is `0`, and all 26 lines
   the catalog *does* pay have no carrier at all. `@+0xf0 == xp_reward` on **0 of 31**; `@+0xf8 == cash_reward`
   on **0 of 31**.
2. **It is not constant across the line.** Of the carriers' captured siblings, 0 repeat the value and 4 carry a
   different one — a line-scoped field cannot do that.
3. **The supporting coincidence was a magnitude collision.** `[Thunder River]`'s `xp_reward = 45800` was read as
   matching the captured `@+0xf0` of quests 13895 and 14427 — but 13895 is on line 1734
   `[King_Conans_Throneroom] Storming the Gate` and 14427 on line 1976 `[ACG_Oasis_of_Zaara] Sacrilegious
   Rites`. Neither is a Thunder River line, and the three that are (1961/1963/1971) carry no `@+0xf0`. 45800 is
   simply what a level-70 XP reward is worth, so it recurs across unrelated content.

#### ⭐ What survives: the two pairs are positionally COMPLEMENTARY

The carrier is the step at the **start** of its line: over the 16 carriers whose line spans more than one
captured sequence, **14 sit at its lowest and 0 at its highest** (the strict fully-ordered measure agrees —
first 12, last 0, middle 2). `XpReward`/`MoneyReward` are the other way round, on the **last** step of a line
(25 of 26).

#### ⛔ …but "one pair at each END of a line" is refuted by the LINE-level census (#802)

That reading predicts a reward-bearing line carrying **both** pairs, one at each end. It does not:
`--report rdb-quest-catalog`'s `TwoPairExclusion` measures, over the 100 captured quests / 95 captured lines,

```
per QUEST  @+0xf0 only 30, @+0x28 only 20, BOTH 1   (chance predicts 6.5 overlaps)
per LINE   @+0xf0 only 30, @+0x28 only 20, BOTH 1
the one overlap, quest 13479: @+0xf0 80 == @+0x28 80   (@+0xf8 7 against @+0x20 0)
```

A line carries **one** reward, in one slot pair or the other — and on the single line that carries both, the XP
values are **equal** (a captured value: quest 13479's record is gated byte-exact by `QuestcRegionTests`). The
positional law then describes *which slot a line uses*, not two rewards at two ends.

⛔ The advertised-reward candidate's own named test is also answered, negatively:
`QuestGiverList.MainQuestReward` is the **item** reward list — imported as `QuestLineReward`, its `type` being
the item's `SearchCategory` on 1,635 of 1,635 resolvable rows — and it carries no xp or money amount at all, so
an advertised money+xp pair cannot surface there.

⚠️ Two client-side routes are also closed, so don't re-walk them: there is **no `com.GameInterface.Quest*`
namespace** among the client's Lua-bound UI registries (Guild, Factions, Inventory, DialogIF, Lore,
PvPMinigame, … — quests are not exposed by name), and a co-occurrence scan for functions touching Quest_c's own
members together (`[+0xf0]` ∧ `[+0xf8]` ∧ `[+0x11c]` ∧ `[+0x118]`, and again with `[+0xec]`) leaves exactly
three: the constructor `FUN_0091cf10`, the reader `FUN_0091df50` and the writer `FUN_0091d260`.

⭐ **And that three-function result is a POSITIVE answer once it has a control** (#802). Read on its own it could
mean "the client reaches Quest_c's fields some way this scan cannot see"; it does not, because a *sibling* field
on the same class comes out the other way. Scanning reads of `[reg + 0x118]` inside the quest module returns a
FOURTH function — `FUN_0091f6f0`, a live consumer — while the same scan for `+0xf0` and `+0xf8` returns only the
three. So the client stores this pair and hands it straight back, and nothing it draws or computes depends on
**which slot** a quest's reward sits in. Which is also why the in-client 8888/999999 sweep moved no label
anywhere, rather than only in the step pane.

The two keep their offsets as names. Gitea #802 is closed on that: our sender's job is to echo what the corpus
captured per quest, which is what the column holds. What would reopen it is a capture holding ONE quest with
both slots populated and a rendered difference — nothing short of that has a consequence.

### `@+0x114` / `@+0x118` — the quest's LEVEL BAND, and the journal's difficulty colour

`FUN_0091f6f0(quest /* EDX */, characterLevel /* ECX */)` is the one consumer either field has, and it is a
five-way band:

```
H   = quest.@+0x118 != 0 ? quest.@+0x118 : quest.@+0x10   // the record's own Level is the fallback
L   = quest.@+0x114
low = H - L - 1 ;  top = H + L + 3
level in [low, top]        -> 2      on level
level > top                -> 1 while within 3 more, else 0     (slightly / far above)
level < low                -> 3 while within 1 less, else 4     (slightly / far below)
```

so `@+0x118` is **the level the quest is FOR** and `@+0x114` its **tolerance**, and the return is the journal's
quest colour. Its caller is the journal panel at `FUN_012ce1c0`. This is an independent client-side confirmation
of `@+0x118`, which the corpus had only reached by measurement (it is the field the money:xp ratio is a function
of — see `QuestDefinition.Field118`); it also names `@+0x114`, which had none.

⚠️ `@+0xf8` tracks `@+0xf0` through a level-dependent factor (~0.267 at level 23 rising to ~1.10 at 80), but 99
samples do not close the law. Do not derive one from the other.

`@+0xf0` additionally selects WHICH SPELLING the quest's @609 objective region uses for its trailing text —
see the `ObjectiveRegionVerified` note on `QuestDefinition`.

### `@+0x14` — a bitfield of the quest LINE; bit 4 = NOT SHAREABLE, bit 10 = NOT DELETABLE

Five values across the corpus (2, 10, 18, 26, 1042), every one ≡ 2 (mod 8): bits 0–2 fixed `010`, bits 3, 4
and 10 toggling. Definitively **not** a goal count — 2 appears on records carrying 1, 2, 3 and 6 goals.

[V1: Faolan.ClientLauncher/Program.cs — its SCOPE is the line, not the quest: the value is constant across
every quest of the same line in all **90** lines that have a captured quest, with zero conflicts, and constant
per quest across 2626 captured records.]

**bit 4 (0x10) = the quest may not be SHARED.**

[V1: Faolan.ClientLauncher/Program.cs — swept live: one rendered quest's column was driven through all five
observed values, relogging each time, and the journal's detail pane greys out its `Share` button for exactly
the three values with bit 4 set (18, 26, 1042) and enables it for the two without (2, 10). The captures within
each class are PIXEL-IDENTICAL, which isolates bit 4 — bit 3 differs between 2 and 10 and between 18 and 26,
and changes nothing.]

The corpus fits the meaning: bit 4 is set on the Destiny steps, the Alchemist tier-4 progression and the
Khitai/faction content (37 of the 82 eligible quests), and clear on ordinary side quests (45) — personal-story,
crafting-progression and faction content being non-shareable is exactly the expected rule.

**bit 10 (0x400) = the quest may not be DELETED.**

The exact sibling of bit 4, one widget along. The detail pane's two action buttons are enabled by NEGATING
their bit — the client computes `enabled = ~(flags >> 4) & 1` for Share and `~(flags >> 10) & 1` for Delete.

[V1: `tools/ghidra/DumpQuestLineFlagBits.java` + a live read-watchpoint — a `rwatch` on a running client's
`Quest_c @+0x14` caught two PCs, both inside `FUN_01617260`, the panel refresh. Its Share branch is
`FUN_00ae4170(~(byte)(*(uint *)(param_3 + 0x14) >> 4) & 1)` on the widget at `+0x52c`, and the bit-10 branch is
the same shape on `+0x528`.]

⚠️ **It is overridden for GAME MASTERS**, which is why the in-client sweep could not see it:

```c
if (flags & 0x400)  enabled = (GmLevel != 0);   // stat 0x0D7
else                enabled = 1;
```

On a GM account the button is force-enabled whatever the bit says, and the account every sweep ran on is
`Administrator`. The earlier note that 18/26/1042 render "pixel-identically" was true and actively misleading —
the difference was real and being suppressed by the observer's own GM level. **A null result from an in-client
sweep is only as good as the account it was run on.**

[V1: `Faolan.ClientLauncher/Program.cs` — proven by A/B on ONE quest with a single variable changed. Quest 10238
"The Slaver" on a NON-GM account: flags 1042 (bit 10 set) → Delete greyed; flags 18 (bit 10 clear, bit 4 left
set) → Delete enabled. Same character, same quest, same solo state.]

The corpus agrees: bit 10 is the Tortage Destiny storyline (The Slaver, The Quarry, The City of Tortage, Nadini,
Casilda, The Scavenger) plus the Alchemist progression — exactly the quests retail refuses to let you abandon.
The old "player-progression lines" reading was the same set seen without its mechanism.

⚠️ **Share is also disabled when you are not in a group**, independently of bit 4. A solo observation of a greyed
Share button says nothing about the bit; only an A/B on the bit does.

**bit 3 (0x08) — the client never reads it.** Not "unnamed pending a better sweep": there is no consumer
anywhere in the image, and the scan that says so carries its own controls.

A bit test has two spellings in this binary and a scan for one is blind to the other — the mask form
(`TEST byte ptr [reg+0x14], 8`, `DumpQuestLineFlagBits.java`) and the SHIFT form (`SHR reg,3; TEST CL,1`,
`FindBitShiftAtOffset.java`), which is the one bits 4 and 10 are actually written in. Run over the whole image:

| bit | shift-form sites | includes the known consumer? |
|---|---|---|
| 4 (Share) | 33 | ✅ `FUN_01617260`, the journal panel |
| 10 (Delete) | 12 | ✅ `FUN_01617260`, plus `FUN_012ccd10` in the same journal module |
| **3** | 12 | ❌ — 11 are `SAR reg,3` **divide-by-8 pointer arithmetic** in container code, and the 12th
(`FUN_0137ec50`) is a protobuf serializer testing its own `has`-bits at `+0x84` |

and the mask form adds nothing: its `[reg+0x14], 8` hits are stack locals (`[ESP/EBP + 0x14]`) or the C++
iostream/locale internals around `0x0183xxxx`. So bit 3 is authoring metadata the record carries and the client
ignores — like `@+0xf0`/`@+0xf8`, and unlike its two neighbours in the same word. Six quests carry it: 12381,
12583, 12889, 13473, 16004, 16183.

⚠️ The live-breakpoint route the ticket kept in reserve is now unnecessary *and* weaker: a watchpoint only sees
the branches that session executes, while the scan covers every site in the image.

⚠️ One earlier reading is superseded. Bit 4 was guessed as "expansion content", which had counterexamples
(17402/17403 are in Khitai with it clear); it is sharing, and those are simply shareable Khitai quests.

⚠️ Do not read the quest TREE pane in a sweep like this. The client persists its expanded/collapsed and scroll
state across relogs, so the left pane drifts between runs independently of the data — it produced three
apparent "classes" that map to no bit at all. Only the detail pane is deterministic.

### `@+0x11c` — 1 on exactly the quests absent from the client's quest-giver catalog

1 for 19314, 19364, 19365, 19522 and 19620 (World Bosses, Anniversary 2014, Halloween 2015, Onslaught), 0 for
the other 94.

A second, independent signal picks out the same set: those are exactly the captured quests that have **no
entry in RDB 1000104**, the client's quest-giver catalog (the sixth such quest, 19363, has no captured journal
record at all, so the wire never stated a `@+0x11c` for it). Among the 99 quests we can see both sides of, the
two agree perfectly.

That suggests the flag marks a quest reached some way OTHER than a quest giver — script-granted, event, meta,
raid-finder — which fits what those five are. ⚠️ It is a HYPOTHESIS: n = 5, and the catalog omits 952 of the
client's 5920 quests for reasons that are themselves not established (see
[quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)), so the correlation may be inherited from
whatever really drives that omission rather than being the field's meaning.

⚠️ An earlier version of this note explained the same observation as "content newer than the RDB". That was
wrong — the client is the same build the captures came from, and its own LDB carries the text for those
quests.

## Synthesising a Quest_c record — the snapshot ceiling, 82 → 99

`SnapshotTails.IsSnapshotEligible` wants five things: the record scalars, a non-empty goal list, a verified
objective region, the line's 4CC, and the five header text refs. Seventeen quests had **everything but the
record** — no capture ever carried one for them — so they sat out of the login snapshot.

`ImportOps.SeedDerivedSnapshotRecords` derives one, running after `VerifyObjectiveRegions` because that
verdict is its gate. It re-tests every other arm of `IsSnapshotEligible`, so it can never flag a quest the
gate would reject. What it fills:

| field | derived from |
|---|---|
| `@+0x14` flags | the line — a captured sibling states it for the whole line; else 2, the modal value and bit-4-clear (shareable) |
| `@+0x118` | the quest's `Level` (captured values run level..level+5; the level is that range's low end) |
| `@+0xec` | 1 — constant in all 99 captured records |
| `@+0x28`, `@+0xf0`, `@+0xf8` | left 0 |
| `@+0x2c` chain head | left 0; `QuestcRecords.ForQuest` already falls back to the quest's own id |

⚠️ This SYNTHESISES wire data, which the gate's own comment warns against. What makes it defensible is that
the record's **shape** is fixed and only its **values** are invented, and each invented value was proven
harmless in-client before being shipped: `@+0x14` renders without complaint at all five observed values (only
the `Share` button moves, on bit 4), `@+0x28` is the printed XP so 0 renders "XP: 0", and `@+0xf0`/`@+0xf8`
changed nothing visible at any value tried.

[V1: Faolan.ClientLauncher/Program.cs — verified on a live login: character Hakotep holds 21 snapshot-eligible
active quests of which **5 are derived records**, and the client's journal header reads `Quests (21/45)` — the
exact count — with the derived quests rendered and **no `statlist.cpp:134` assert** in the client log. The
hazard the gate guards against is real for the objective REGION; the record scalars are safe to derive.]

⚠️ The cost is cosmetic and visible: a derived quest shows **`XP: 0`**, because `@+0x28` is not recoverable for
a quest no capture carried. It is wrong on screen, not wrong on the wire.

**This does not generalise far.** The remaining 4940 quests are not blocked by the record — they have no goal
data at all (only 108 quests have wire goals, and only 99 a captured @609 region to verify against). The limit
is capture coverage of the objective region, not decode.
