# Quest_c region (0x737A6DF9 FullChar snapshot) — deep decode

The Quest_c region is the quest journal at the front of the snapshot tails. `QuestcRegionCodec` models it. The
beach create template is the simplest possible journal (one quest, empty objective lists, no progress); this page
records the full structure recovered from the client reader (Ghidra) and validated against progressed journals
(e.g. NecroL80's level-80 login, 27 quests).

## Readers (Ghidra `AgeOfConan.exe`)

- `FUN_0091f5c0` — **List-A driver**: reads `u32 count = 0x3F1*(records+1)`, then loops `records` times, each
  iteration reading one `u32` (the record's listKey) + calling the per-record reader.
- `FUN_0091df50` — **per-record reader**. Read primitives: `FUN_0048f900`=ReadU32, `FUN_0048f840`=ReadString
  (`[u16 len][utf8]`), `FUN_0048f720`=ReadU64, `FUN_00cc0540`=Read4×U32. Post-obj4 calls are `FUN_009202d0`
  (std::sort introsort) + vector-push, not stream reads.

## `ACGHash` — the 4-character token, and why it is never a magic

`FUN_005d7190` is the client's **ACGHash** stream reader, and it is what every "4-character magic" in the quest
packets actually goes through. It reads one `u32`, checks each of the four bytes against `[0-9A-Z_]`, and on a
mismatch logs `Read invalid hash "<...>" from stream` under the `ACGHash` debug channel. It compares against
**nothing** — there is no expected value, because the value is an identity token, not a marker.

Three places carry one:

- the `QuestJournalIIR` area block's token (e.g. `"RRL3"`) — the area's, not a record-list magic;
- `QuestObjectiveGoal.Reserved[5..6]` — a random token distinct in every goal (NecroL80's journal: 39 anchors, 39
  distinct tokens — `WTHD`, `1LJL`, `UFWN`, `HIB4`, `C9PY`, …; none has an xref in `AgeOfConan.exe` because none is
  a constant);
- the quest index's per-block `magic` (`QuestDefinition.IndexMagic`), which is the same area token again — a
  quest's index block and its journal block carry one value, keyed by `AreaId`.

So **a four-byte field of printable uppercase-alphanumerics in a quest packet is an ACGHash.** Asserting the one
value a single capture carries rejects 31 of 38 captured journals.

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

**The trailing five fields are independent, not a "tracked-quest pointer".** A reading as
`[u32 trackedQuestId][list A][list B]` with each list `[frame][count]` only fits a one-quest journal: in every
larger one, list A's "count" slot holds a quest id and list B's frame holds `0xBD3`. The reader shows five
independent fields, and both captured shapes fall out exactly — `(0, empty, 0,
empty, 0)` for a single-quest journal and `(1, empty, questId, 2 strings, entryCount)` naming the last entry for
every larger one. **And a string list here is frame-only** — one `(count+1) * 0x3F1` word and then the strings,
with no separate count word, unlike the record lists which carry both. The per-entry `0xBD3` is `ForCount(2)`,
framing the entry's title + sub-line.

## Record grammar (validated byte-exact on NecroL80 rec0, snapshot offset 11024)

```
[listKey=questId]
u32 ver(=1)
u32 questId          (+0xc)
u32 level            (+0x10)  the quest level                          QuestcRecord.Level
u32 bandTolerance    (+0x114) the level band's tolerance               BandTolerance
u32 bandLevel        (+0x118) the level the quest is for               BandLevel
u32 lineFlags        (+0x14)  a flag word of the quest line            QuestLineFlags
string title, summary, area, region1, region2   # cats 50301/50302/50300/52000/52000
u32 fieldEc          (+0xec)
u32 areaId           (+0xcc)                                           QuestLineId
u32 field2c          (+0x2c)  the chain head; not always == questId   Field2c
u32 field11c         (+0x11c)
u64 money            (+0x20)  the quest's money reward, in tin          MoneyReward
u32 xpReward         (+0x28)  the quest's experience reward            XpReward
objlist obj1, obj2                     # each: [u32 count][count × QuestRewardItem]
u32 fieldF0          (+0xf0)
u64 fieldF8          (+0xf8)
objlist obj3, obj4
@609 objective region                  # QuestObjectiveRegion, interleaved per record
```

**The objective region belongs to each record.** What follows `obj4` is that quest's own @609 objective region
(`QuestObjectiveRegion`, `QuestcRecord.Objectives`), interleaved per quest record — not a separate region after
the whole list, and not an opaque server block. A one-quest journal (the beach template) cannot tell "after the
record" from "after the last record"; a multi-quest journal read the other way desyncs at record 2. Verified by
`ProgressedJournalSnapshotTests` across all 141 captured snapshots (beach L1, L23, L24 and both L80 journals; 85
multi-quest), tails byte-exact.

A grammar pinned only by a single-element capture is not pinned, and a value that appears once per record is
worth checking against a second capture before calling it a magic or asserting it.

## The four `objlist`s are the quest's reward items

`ObjRec` = **16 u32** on the wire. The beach starter carries none; over the whole corpus they are populated on
**7,347** elements across 3,741 records (`--report quest-rewards`), and the element is one **reward item**:

```
[u32 instance][u32 questId]
[u32 1000620][u32 templateId][u32 key1..key4]     # ACG template1 + its per-template TEA key
[u32 1000620][u32 templateId][u32 key1..key4]     # ACG template2 + its key
[u32 level][u32 quantity]
```

Four independent things pin it, and no two of them share an assumption:

1. **Decryption, with a control.** All **101 of 101** distinct templates open with the key the wire states
   beside them — each decrypting to a body that declares one of the catalog types and its own id — and all
   **101 of 101** fail with a bogus key. The same test pins `keyN ↔ templateN` for `ProtoAcgItem`.
2. **The client's own field grouping.** The reader does not store the sixteen words in order: it reads
   `w2, w3` and stores them at `[7], [6]`, then `Read4×U32` into `[2..5]` — i.e. it assembles `(type, id, key)`
   into one six-word reference object, twice.
3. **The echo.** `w1` equals the owning record's own `questId` on 7,347 of 7,347.
4. **`1000620`** is the client's item-template catalog (the RDBH variant of 1000020, `rdb-type-catalog.md`), and
   it is the type word on both halves of every element.

The two trailing words are named from what they are **attached to**, not from their values: `level` is constant
per template (101/101), so it belongs to the item; `quantity` is constant per (quest, template) (161/161) but not
per template, so it belongs to the reward.

They are also a content source: 67 of the 101 templates are **not** in our `Items`, and each arrives with the
key that admits it (an item enters the catalog when a capture yields its `Data1..4`). `QuestRewardItem` is the
typed form; `QuestcRecord` keeps the lists' positional names (`Obj1`..`Obj4`).

### What the four lists mean, and the journal's own rule for showing them

Two sources that share no assumption settle it:

**The client's journal renderer.** `FUN_01617260` (the QuestJournal view) fills one reward panel from a
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

`bVar9`'s list is the **server-sent** area list — `MainQuest_c::WriteToStream` (`FUN_00921db0`) serialises its
`+0x1c` vector as 8-byte `(sequence, questId)` pairs, i.e. the index region's per-area block — and that list
equals the line's whole catalog on 3,300 of 3,741 captured blocks (never shorter). So in practice "last entry" is
the line's last step.

**The client's quest catalog.** RDB 1000104's `MainQuest.reward` / `optional_reward` (imported as
`QuestLineReward`) are the line's reward, and its own test lines are named *"Three NonOptional rewards"* / *"1
NonOptional + 2 Optional rewards"*. Over the corpus:

| | records | |
|---|---:|---|
| `obj3`/`obj4` populated | 1,811 | **1,553 equal the line's catalog lists exactly, 0 differ**, 258 on a line the catalog holds no items for |
| `obj1`/`obj2` populated, last step | 891 | 574 equal the catalog lists (the line reward **folds** into the last step's own set), 317 differ |
| which set a quest populates × last step | 68 quests | set A ⇔ last 11/0, set B ⇔ mid 0/55, both on 2 mid steps (10928, 17403) |

And the one captured completion (quest 10928): retail **offered exactly its obj2** and granted the pick,
while its obj3 — the line's Exponent's Gloves — was not granted, because the line was not finished.

So:

* **`obj1` / `obj2` = this quest's own reward** — granted / choose-one — paid at its completion. Server-side data
  the catalog never states (the `Quest` record has no reward field), harvested per quest into
  `QuestRewardOptions` (`Optional` = which list).
* **`obj3` / `obj4` = the line's reward**, previewed on every step but the last, and it is the client catalog's
  own list — buildable for all 1,410 catalog lines. Two words the catalog lacks are harvested per (line,
  template): the **quantity** (constant per pair on 125/125; the three templates that vary across lines are
  currency tokens) and the **wire order** (`WirePos` — mostly the catalog's reversed, but 5 of 12 lines break
  that and no id/type/name sort fits, so it is retail's row order). The element's `level` is the item's own
  (`Items.Level`, 5,248/5,248).
* **No list is class-specific**: 16 quests captured under more than one class carry identical lists.

Built by `QuestRewardLists.ForQuest` and gated by `QuestRewardListsCorpusTests` — every captured record's four
lists rebuild **element-exact** from the seed (1,791 records). Residue, in `--report quest-reward-lists`: 17732 /
17733 are last steps whose obj2 is empty against a 4–5-item catalog optional list (a class filter does not
explain it — 3238's list holds cloth for a necromancer), and line 3780's list is on the wire and not in the
catalog (its rows are inserted from the wire).

**`+0xf0`/`+0xf8` are the XP/money the journal shows on a non-last step.** `FUN_01617260` reads both. The XP
words follow the same split as the item lists: `+0x28`/`+0x20` are the quest's own on the last step,
`+0xf0`/`+0xf8` the line's on the others — and they are level-scaled on the wire (line 386's catalog XP is 715;
10830's records carry 715 in `+0x28` and 630 in `+0xf0`). An in-client sweep of `+0xf0`/`+0xf8` on a last-step
quest changes no label, because that branch never renders there.


## The record's class, reader and writer

The record's structure is confirmed from both sides; the field names are not in the binary as strings — they come
from consumers and from the client's data (below).

**The class.** RTTI is intact: the type descriptor `.?AVQuest_c@@` is at `021cf360`, its complete-object
locator at `020dc260`, and the vtable at `01fdfd18` — only three virtuals (`FUN_0091d090`, `FUN_0091ce50`,
`FUN_0091ce60`). Its source file is `quest.cpp`; the record's string pool sits right after the vtable.

**Reader and writer agree with our codec.** `FUN_0091df50` reads the record and `FUN_0091d260` writes it back
(the byte-swap-then-emit pattern), and between them they touch exactly the offsets `QuestcRecordCodec` models,
in the order it reads them — including `@+0x20`/`@+0x24` and `@+0xf8`/`@+0xfc` as the two u64 pairs. The reader
is not virtual (no pointer to it exists anywhere in the image) and has exactly two callers, `FUN_0091f5c0` and
`FUN_00917e20`, both thin parse-loop wrappers that never touch the fields.

**Why the names are not recoverable by string anchor.** The writer is label-free — it emits scalars with no
per-field assert, unlike e.g. `DispatchSetQuestMarker`, whose asserts name `i_cNPC` and `i_eMarker`. The quest
module's string pool names plenty of methods and one member, but nothing on this record:

| string | what it gives |
|---|---|
| `QuestHolder_t::UpdateCurrentProgress() m_pcCurrentQuest is NULL.` | the holder class + its member |
| `QuestGoal_c::SetParent()` / `SetSolvedTimes()` / `GetQuestLevel()` | goal API; "SolvedTimes" is the goal's progress count — and it is the goal record's `reserved[0]`, see [ldb-string-index.md](../rdb/ldb-string-index.md) |
| `QuestGoal_c::GetQuestLevel() … Returning Stat value` / `Returning Quest value` | the goal's level resolves from a stat first, the quest second |
| `MainQuest_c::WriteToStream` | `mainquest.cpp` is a separate class from `quest.cpp` |
| `QuestRewardNotifyIIR_c`, `QuestRewardSelectionIIR_c` | rewards have their own IIRs |
| `QuestJournalIIR_c::ReadSubClass()`, `PatrolIIR_c`, `RDBQuestGivers` | neighbouring subsystems |

`GetQuestLevel()` returning "the Stat value" or "the Quest value" says a goal's level has two sources.

## The live full-update is one `Quest_c` record

`QuestFullUpdateIIR::ReadSubClass` is **`FUN_00917e20`** (vtable slot 8; self-named by its own
`"QuestFullUpdateIIR::ReadSubClass() Wrong IIR_t version %d"` log and its `iir\questfullupdateiir.cpp` assert).
It allocates a `Quest_c` (0x120 bytes, ctor `FUN_0091cf10`) and hands the stream to **`FUN_0091df50`** — so the
live full-update body is one `Quest_c` record, goals included, and the goal grammar can be read statically.

**The objlist element and the @609 goal record are different structs** — the 16-word objlist element is the
reward item (`QuestRewardItem`); the goal record is read separately. Three things separate them:

* the objlist is framed by a **plain u32 count** the client clamps to 100; the @609 region is ListFramed as
  `(goals + 1) * 0x3F1`;
* an objlist element is 16 words carrying **no text**; the @609 goal record is 21 words **plus two text records**;
* `FUN_0091df50` reads the four objlists as part of the `Quest_c` record, and the @609 region *follows* it.

The sort at the end of the reader orders the `QuestGoal_c` objects at `+0xAC`/`+0xBC`, not anything from the
objlists. What is pinned:

* `Quest_c` allocates **four** POD vectors (`+0x100`, `+0x104`, `+0x10C`, `+0x110`), one per objlist, plus **two**
  vectors of refcounted `QuestGoal_c*` at `+0xAC..+0xB0` and `+0xBC..+0xC0`.
* The `QuestGoal_c` objects **exist before the record is read** — `FUN_0091df50` only post-processes them
  (`QuestGoal_c::SetParent` = `FUN_009260c0`, then `std::sort` with comparator `FUN_0091ce70`) — so they are built
  from the client's own quest template, not from the wire.
* **The waypoint UI reads those objects, never the wire PODs.** `WaypointQuestGoal` (`FUN_013a26c0`,
  `GUI\MapWaypointHandler.cpp`, keyed `m_cWaypoints.find( goalID )`) walks `+0xAC..+0xB0` and reads `+0x38`/`+0x3C`
  (required vs solved — the pair `CoreQuestHolder::QuestGoalEvent` names), `+0x5c` playfield, `+0x60/+0x64/+0x68`
  position, `+0x6c` flag, `+0x20` parent, `+0x80` name. Its waypoint types come from
  `Gui/Default/Waypoints/PF%u.xml` (`ParseWPType`, `FUN_0139e6f0`).

## The @609 goal record's reader and its tail's consumer

`QuestGoal_c`'s own stream reader **`FUN_00925dd0`** lands
the 21 words on the object **from `+0x24` upward in wire order**: `instance +0x24`, `questId +0x28`,
`goalId +0x2c`, `n +0x30`, `goalIdRepeat +0x34`, `requiredCount +0x38`, `solvedTimes +0x3c`, the six reserved
words `+0x40..+0x54`, `subId +0x58`, `mapId +0x5c`, `markerX +0x60`, `markerY +0x64`, `unknown1 +0x68`. Then
it reads the tail:

```
[u8 -> +0x6c][u8 -> +0x6d][u32 -> +0x70][u16 len][objective][u16 len][text]
```

Two bools (`FUN_0048f6d0`), a u32, then the ordinary u16-framed `ReadString` twice. A
`[u32 flagWord][u32 objectiveLen][objective]…` carve round-trips every captured body too, only because the u32 is
0 on all of them and no objective is 64 KiB long; its "flag word `0x01000000`" is the first bool.
`QuestObjectiveGoalCodec` reads `[u8 hideMapMarker][u8 tailFlag][u32 tailWord]` and the objective as a u16-framed
string (`QuestGoalDefinition.HideMapMarker`).

**The offsets are exactly what `WaypointQuestGoal` (`FUN_013a26c0`) reads** (`+0x5c` playfield,
`+0x60/+0x64/+0x68` position, `+0x6c` flag):

* for every goal whose `+0x3c` (solved) ≠ `+0x38` (required) it takes `+0x5c` as the goal's playfield;
* if that is the current playfield **and the byte at `+0x6c` is 0**, it places the marker at the vector
  `((float)+0x60, 0, (float)+0x64)` — markerX/markerY on the ground plane — and hands `(float)+0x68` to the
  waypoint object as a **separate scalar** (`CustomWP_c` slot `[10]`, set in `FUN_0139f520` beside the position
  at `[6..8]`) — the marker's **radius**, see below;
* if the byte at `+0x6c` is **set**, it places nothing for a same-playfield goal — the **"no map marker"** flag,
  which is what its 17 carriers (dungeon objectives with degenerate `(1,1)` markers) always looked like;
* if the goal is on another playfield it asks `FUN_0146a9f0(currentPlayfield, goalPlayfield)` where that
  playfield is reachable from here and marks that, whatever the flag says.

### Slot `[10]` is the marker's radius — named by the client, twice

The name comes from a **sibling producer**, not from the slot's reader: `DispatchAddWayPoint`'s field list in
[`aoc-client-rpc-methods.json`](aoc-client-rpc-methods.md) is `i_cID, i_nPlayfieldID, i_cPos, i_vRadius, i_nLabel,
i_nToolTip`, and that message fills the same waypoint object the quest goal's `+0x68` scalar lands in. What the
Ghidra read adds is the one thing the catalog cannot carry: `i_v` is documented there as "a vector /
vec3 — sometimes a scalar component; check size", and the reader settles it. **Two independent namings, and they
agree:**

1. **`DispatchAddWayPoint`'s argument list, with sizes.** Its reader `FUN_004c9860` validates field by field and
   names each field in its own failure string (`"…stream bad after 'i_vRadius'!"`). Read in order:

   | field | words | note |
   |---|---:|---|
   | `i_cID` | 2 | an ID32 (class + id) |
   | `i_nPlayfieldID` | 1 | |
   | `i_cPos` | 3 | the position vector |
   | **`i_vRadius`** | **1** | |
   | `i_nLabel` | 1 | |
   | `i_nToolTip` | 1 | |

   then dispatched to vtable slot `+0x1ec` as `(&id, playfieldId, &pos, radius, label, tooltip)`. That is
   *exactly* the waypoint object's layout — id at `[4..5]`, position at `[6..8]`, the scalar at `[10]` — so the
   one word after the position is the radius.

2. **The `addmapmarker` console command.** Its handler `FUN_00a12680` calls `FUN_0139f520`, the very function
   that writes `+0x28`. The command registers four parameters, in order:
   `"Type of marker (destination, pvp, custom)"`, `"Name of marker"`, `"Position of marker"`,
   **`"Radius of marker"`**.

So the field is **`MarkerRadius`** (`QuestGoalDefinition.MarkerRadius`, and the codec field). The values fit: it appears only on Collect and Kill goals — the kinds
whose target roams — as 3, 10, 20, 25, 40, 50, 60, 80, 90, 100, 105, 130, 150, 170 world units, and 0 (a plain
pin with no extent) everywhere else.

Method: ask "what else fills this struct?" before "what reads this field?" — a sibling producer labels the value
on the way past. All 401 s2c dispatch methods are in `aoc-client-rpc-methods.json` with their fields in wire order,
so a `grep` for a field-shaped guess costs nothing.

The two tail fields `+0x6d` and `+0x70` are zero on every captured sample.

## What the record's fields are, from the client's quest catalog

Field names not in the binary can be in the client's **data**. Cross-referencing the captured records against RDB type 1000104
([quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)) named three of the five and pinned the scope of
a fourth. The probe is 2626 captured records over 141 snapshots, covering 99 distinct quests.

**Every one of these words is definition data.** Per quest, across all
2626 records and every captured character, `@+0x14`, `@+0x28`, `@+0x2c`, `@+0xec`, `@+0x114`, `@+0x118` and
`@+0x11c` never vary; `@+0xf0`/`@+0xf8` vary on exactly one quest (10238, the known two-spelling variant) and
`@+0x10` on three. 97 of the 99 quests appear in more than one capture, so this is not a single-source
artefact. A definition field has a definition-side source, which is what pointed at the catalog.

### `@+0x10` — the quest level, not a state

It equals the DB `Level` on 96 of the 99 captured quests (12373, 17402 and 17403 differ by 1–2, i.e. the row
took its level from a different capture). On the beach starter it is 1 because that quest is level 1 — not an
"active" state. Distinct from `QuestCodec.State` (`StateActive`/`StateCompleted`), a real state on a different
message.

### `@+0x28` — the quest's experience reward (settled in-client)

The client's journal renders a quest's detail pane with an `XP:` and a `Money:` line, and `XP:` is this field.

[V1: Faolan.ClientLauncher/Program.cs — driven live against a local server: quest 13227's column was set to
5553, then 0, then 7777, relogging between each, and the panel read back `XP: 5553`, `XP: 0`, `XP: 7777` with
nothing else changed. In the same runs `@+0xf0 = 8888` and `@+0xf8 = 999999` produced no change to either
label — `Money:` stayed blank.]

Its positional law holds and is unexplained: `@+0x28` is populated on the last step of a quest
line (25 of 26 captured cases) and `@+0xf0`/`@+0xf8` never are. It scales with level — level 23 → 900..4981,
level 30 → 5553, level 70 → 15421, level 80 → 69337..138674, the level-80 values being exactly
{1, 4/3, 5/3, 2} × 69337.

### `@+0x20` — the quest's money reward (settled in-client)

The twin of `@+0x28`: a u64 money at 0x20 and a u32 XP at 0x28, adjacent in the struct, and the two lines the
journal's detail pane prints as `Money:` and `XP:`.

[V1: Faolan.ClientLauncher/Program.cs — driving it live on a rendered quest, 111200 rendered `11S 12C` and
250000 rendered `25S`; the harvested 214 of quest 13507 renders `2C 14T`. So the unit is tin, with 100 tin =
1 copper and 100 copper = 1 silver.]

21 of the 99 captured quests carry one, constant per quest across every character and
capture, and that set is a subset of the quests carrying an XP reward: some quests pay experience only.

At level 80 the harvested pairs run money ≈ **1.10 ×** xp (152546/138674, 127115/115562, 101684/92449). That
is the same ratio `@+0xf8`/`@+0xf0` shows at level 80, which is the strongest hint yet that those two are the
same (xp, money) pair at a different scope (see "What the four lists mean" above).

### `@+0xf0` / `@+0xf8` — not the catalog's `xp_reward` / `cash_reward`

The client catalog's `xp_reward`/`cash_reward` pair is not this pair: with both set to large distinctive values
on a last-step quest, the pane shows the XP from `@+0x28` and leaves `Money:` blank. A field-name match in a
neighbouring data file is not evidence about the wire.

#### Not the catalog line reward either

`--report rdb-quest-catalog`'s `LineRewardCheck`, which joins every carrier to the catalog's `MainQuest`, rules
that out three ways:

1. **The populations are disjoint.** All 31 carriers sit on a line whose catalog reward is `0`, and all 26 lines
   the catalog *does* pay have no carrier at all. `@+0xf0 == xp_reward` on **0 of 31**; `@+0xf8 == cash_reward`
   on **0 of 31**.
2. **It is not constant across the line.** Of the carriers' captured siblings, 0 repeat the value and 4 carry a
   different one — a line-scoped field cannot do that.
3. **The one apparent match is a magnitude collision.** `[Thunder River]`'s `xp_reward = 45800` equals the
   captured `@+0xf0` of quests 13895 and 14427 — but 13895 is on line 1734
   `[King_Conans_Throneroom] Storming the Gate` and 14427 on line 1976 `[ACG_Oasis_of_Zaara] Sacrilegious
   Rites`. Neither is a Thunder River line, and the three that are (1961/1963/1971) carry no `@+0xf0`. 45800 is
   simply what a level-70 XP reward is worth, so it recurs across unrelated content.

#### The two pairs are positionally complementary

The carrier is the step at the **start** of its line: over the 16 carriers whose line spans more than one
captured sequence, **14 sit at its lowest and 0 at its highest** (the strict fully-ordered measure agrees —
first 12, last 0, middle 2). `XpReward`/`MoneyReward` are the other way round, on the **last** step of a line
(25 of 26).

#### …and a line carries one of them, not both

"One pair at each end of a line" predicts a reward-bearing line carrying **both** pairs, one at each end. It does not:
`--report rdb-quest-catalog`'s `TwoPairExclusion` measures, over the 100 captured quests / 95 captured lines,

```
per QUEST  @+0xf0 only 30, @+0x28 only 20, BOTH 1   (chance predicts 6.5 overlaps)
per LINE   @+0xf0 only 30, @+0x28 only 20, BOTH 1
the one overlap, quest 13479: @+0xf0 80 == @+0x28 80   (@+0xf8 7 against @+0x20 0)
```

A line carries **one** reward, in one slot pair or the other — and on the single line that carries both, the XP
values are **equal** (a captured value: quest 13479's record is gated byte-exact by `QuestcRegionTests`). The
positional law then describes *which slot a line uses*, not two rewards at two ends.

The advertised-reward candidate is also negative:
`QuestGiverList.MainQuestReward` is the **item** reward list — imported as `QuestLineReward`, its `type` being
the item's `SearchCategory` on 1,635 of 1,635 resolvable rows — and it carries no xp or money amount at all, so
an advertised money+xp pair cannot surface there.

Two client-side routes are closed: there is **no `com.GameInterface.Quest*`
namespace** among the client's Lua-bound UI registries (Guild, Factions, Inventory, DialogIF, Lore,
PvPMinigame, … — quests are not exposed by name), and a co-occurrence scan for functions touching Quest_c's own
members together (`[+0xf0]` ∧ `[+0xf8]` ∧ `[+0x11c]` ∧ `[+0x118]`, and again with `[+0xec]`) leaves exactly
three: the constructor `FUN_0091cf10`, the reader `FUN_0091df50` and the writer `FUN_0091d260`.

**That three-function result is a positive answer once it has a control.** Read on its own it could
mean "the client reaches Quest_c's fields some way this scan cannot see"; it does not, because a *sibling* field
on the same class comes out the other way. Scanning reads of `[reg + 0x118]` inside the quest module returns a
fourth function — `FUN_0091f6f0`, a live consumer — while the same scan for `+0xf0` and `+0xf8` returns only the
three. So beyond the journal panel's step branch, nothing the client computes depends on **which slot** a quest's reward
sits in. The two keep their offsets as names (`FieldF0`/`FieldF8`); our sender echoes what the corpus captured per
quest.

### `@+0x114` / `@+0x118` — the quest's level band, and the journal's difficulty colour

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

so `@+0x118` is **the level the quest is for** and `@+0x114` its **tolerance**, and the return is the journal's
quest colour. Its caller is the journal panel at `FUN_012ce1c0`. This independently confirms `@+0x118`, which the
corpus reaches by measurement (it is the field the money:xp ratio is a function of — see
`QuestDefinition.BandLevel`), and names `@+0x114` (`BandLevel` / `BandTolerance`).

`@+0xf8` tracks `@+0xf0` through a level-dependent factor (~0.267 at level 23 rising to ~1.10 at 80), but 99
samples do not close the law. Do not derive one from the other.

`@+0xf0` additionally selects which spelling the quest's @609 objective region uses for its trailing text —
see the `ObjectiveRegionVerified` note on `QuestDefinition`.

### `@+0x14` — a bitfield of the quest line; bit 4 = not shareable, bit 10 = not deletable

Five values across the corpus (2, 10, 18, 26, 1042), every one ≡ 2 (mod 8): bits 0–2 fixed `010`, bits 3, 4
and 10 toggling. **Not** a goal count — 2 appears on records carrying 1, 2, 3 and 6 goals.

[V1: Faolan.ClientLauncher/Program.cs — its scope is the line, not the quest: the value is constant across
every quest of the same line in all **90** lines that have a captured quest, with zero conflicts, and constant
per quest across 2626 captured records.]

**bit 4 (0x10) = the quest may not be shared.**

[V1: Faolan.ClientLauncher/Program.cs — swept live: one rendered quest's column was driven through all five
observed values, relogging each time, and the journal's detail pane greys out its `Share` button for exactly
the three values with bit 4 set (18, 26, 1042) and enables it for the two without (2, 10). The captures within
each class are pixel-identical, which isolates bit 4 — bit 3 differs between 2 and 10 and between 18 and 26,
and changes nothing.]

The corpus fits the meaning: bit 4 is set on the Destiny steps, the Alchemist tier-4 progression and the
Khitai/faction content (37 of the 82 eligible quests), and clear on ordinary side quests (45) — personal-story,
crafting-progression and faction content being non-shareable is exactly the expected rule.

**bit 10 (0x400) = the quest may not be deleted.**

The exact sibling of bit 4, one widget along. The detail pane's two action buttons are enabled by negating
their bit — the client computes `enabled = ~(flags >> 4) & 1` for Share and `~(flags >> 10) & 1` for Delete.

[V1: `tools/ghidra/DumpQuestLineFlagBits.java` + a live read-watchpoint — a `rwatch` on a running client's
`Quest_c @+0x14` caught two PCs, both inside `FUN_01617260`, the panel refresh. Its Share branch is
`FUN_00ae4170(~(byte)(*(uint *)(param_3 + 0x14) >> 4) & 1)` on the widget at `+0x52c`, and the bit-10 branch is
the same shape on `+0x528`.]

**It is overridden for game masters**, so an in-client sweep on a GM account cannot see it:

```c
if (flags & 0x400)  enabled = (GmLevel != 0);   // stat 0x0D7
else                enabled = 1;
```

On a GM account the button is force-enabled whatever the bit says. **A null result from an in-client sweep is only
as good as the account it was run on.**

[V1: `Faolan.ClientLauncher/Program.cs` — proven by A/B on one quest with a single variable changed. Quest 10238
"The Slaver" on a NON-GM account: flags 1042 (bit 10 set) → Delete greyed; flags 18 (bit 10 clear, bit 4 left
set) → Delete enabled. Same character, same quest, same solo state.]

The corpus agrees: bit 10 is the Tortage Destiny storyline (The Slaver, The Quarry, The City of Tortage, Nadini,
Casilda, The Scavenger) plus the Alchemist progression — exactly the quests retail refuses to let you abandon.

**Share is also disabled when you are not in a group**, independently of bit 4. A solo observation of a greyed
Share button says nothing about the bit; only an A/B on the bit does.

**bit 3 (0x08) — the client never reads it.** There is no consumer anywhere in the image, and the scan that says
so carries its own controls.

A bit test has two spellings in this binary and a scan for one is blind to the other — the mask form
(`TEST byte ptr [reg+0x14], 8`, `DumpQuestLineFlagBits.java`) and the shift form (`SHR reg,3; TEST CL,1`,
`FindBitShiftAtOffset.java`), which is the one bits 4 and 10 are actually written in. Run over the whole image:

| bit | shift-form sites | includes the known consumer? |
|---|---|---|
| 4 (Share) | 33 | yes — `FUN_01617260`, the journal panel |
| 10 (Delete) | 12 | yes — `FUN_01617260`, plus `FUN_012ccd10` in the same journal module |
| **3** | 12 | no — 11 are `SAR reg,3` **divide-by-8 pointer arithmetic** in container code, and the 12th
(`FUN_0137ec50`) is a protobuf serializer testing its own `has`-bits at `+0x84` |

and the mask form adds nothing: its `[reg+0x14], 8` hits are stack locals (`[ESP/EBP + 0x14]`) or the C++
iostream/locale internals around `0x0183xxxx`. So bit 3 is authoring metadata the record carries and the client
ignores — like `@+0xf0`/`@+0xf8`, and unlike its two neighbours in the same word. Six quests carry it: 12381,
12583, 12889, 13473, 16004, 16183.

A live watchpoint is weaker than this scan: it only sees the branches that session executes, while the scan
covers every site in the image.

Bit 4 is not "expansion content": 17402/17403 are in Khitai with it clear — they are shareable Khitai quests.

Do not read the quest tree pane in a sweep like this. The client persists its expanded/collapsed and scroll
state across relogs, so the left pane drifts between runs independently of the data — it produced three
apparent "classes" that map to no bit at all. Only the detail pane is deterministic.

### `@+0x11c` — 1 on exactly the quests absent from the client's quest-giver catalog

1 for 19314, 19364, 19365, 19522 and 19620 (World Bosses, Anniversary 2014, Halloween 2015, Onslaught), 0 for
the other 94.

A second, independent signal picks out the same set: those are exactly the captured quests that have **no
entry in RDB 1000104**, the client's quest-giver catalog (the sixth such quest, 19363, has no captured journal
record at all, so the wire never stated a `@+0x11c` for it). Among the 99 quests we can see both sides of, the
two agree perfectly.

That suggests the flag marks a quest reached some way other than a quest giver — script-granted, event, meta,
raid-finder — which fits what those five are. It is a hypothesis: n = 5, and the catalog omits 952 of the
client's 5920 quests for reasons that are themselves not established (see
[quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)), so the correlation may be inherited from
whatever really drives that omission rather than being the field's meaning.

It is not "content newer than the RDB": the client is the same build the captures came from, and its own LDB
carries the text for those quests.

## Synthesising a Quest_c record

`SnapshotTails.IsSnapshotEligible` wants six things: the record scalars, a non-empty goal list, a verified
objective region, the line's 4CC, the five header text refs, and an order for the quest's line (the index block
states the held quest's step number, so a multi-step line whose `JournalSequence` no capture has filled keeps its
quests out rather than shipping "step 0"). Seventeen quests have **everything but the record** — no capture carried
one for them.

`QuestImportOps.SeedDerivedSnapshotRecords` derives one, running after `VerifyObjectiveRegions` because that
verdict is its gate. It re-tests every other arm of `IsSnapshotEligible`, so it can never flag a quest the
gate would reject. What it fills:

| field | derived from |
|---|---|
| `@+0x14` flags | the line — a captured sibling states it for the whole line; else 2, the modal value and bit-4-clear (shareable) |
| `@+0x118` | the quest's `Level` (captured values run level..level+5; the level is that range's low end) |
| `@+0xec` | 1 — constant in all 99 captured records |
| `@+0x28`, `@+0xf0`, `@+0xf8` | left 0 |
| `@+0x2c` chain head | left 0; `QuestcRecords.ForQuest` already falls back to the quest's own id |

This synthesises wire data, which the gate's own comment warns against. What makes it defensible is that
the record's **shape** is fixed and only its **values** are invented, and each invented value was proven
harmless in-client before being shipped: `@+0x14` renders without complaint at all five observed values (only
the `Share` button moves, on bit 4), `@+0x28` is the printed XP so 0 renders "XP: 0", and `@+0xf0`/`@+0xf8`
changed nothing visible at any value tried.

[V1: Faolan.ClientLauncher/Program.cs — verified on a live login: character Hakotep holds 21 snapshot-eligible
active quests of which **5 are derived records**, and the client's journal header reads `Quests (21/45)` — the
exact count — with the derived quests rendered and **no `statlist.cpp:134` assert** in the client log. The
hazard the gate guards against is real for the objective region; the record scalars are safe to derive.]

The cost is cosmetic and visible: a derived quest shows **`XP: 0`**, because `@+0x28` is not recoverable for
a quest no capture carried. It is wrong on screen, not wrong on the wire.

**This does not generalise far.** The remaining 4940 quests are not blocked by the record — they have no goal
data at all (only 108 quests have wire goals, and only 99 a captured @609 region to verify against). The limit
is capture coverage of the objective region, not decode.
