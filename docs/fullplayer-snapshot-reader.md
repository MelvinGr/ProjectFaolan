# FullPlayerCharacterIIR (0x737A6DF9) wire-reader — the snapshot "tails" grammar

Recovered from `AgeOfConan.exe` via the vtable-xref method (`Captures/ghidra/DumpFullCharReader.java` +
`DumpFullCharRecords.java`, slot 8 = `ReadSubClass`; see the agent memory `ghidra_vtable_xref_readers`).
Goal: replace the opaque `SnapshotTails.UnknownBeforeAccessWindowTable` blob (the last template-sourced
region of `BeachTailsTemplate.bin`) with a real, generated grammar so the `.bin` can be retired and the
character's **class/progression** authored instead of inherited from Strie. See `docs/quest-log-handover.md`
and the memory `snapshot_tails_construction_scope`.

## Status & handover (2026-06-16)

The whole `UnknownBefore` blob is mapped, and the **quest-journal region is decoded, modeled, and
generatable from the DB**. Commits this session (branch `development`, all green — 401 tests): the Ghidra
reader decode, the `Quest_c`/`MainQuest_c`/`StatList_t`/`SpellBook_t` grammar, the `decode_questc.py`
dissector, the `QuestcRegion`/`QuestcRecord` codec, its integration into `SnapshotTails` (byte-identical),
and `QuestcRecord.BuildSingle` generation from `QuestDefinition` (byte-exact for the beach starter 10238).

DONE:
- Top-level `FullPlayerCharacterIIR::ReadSubClass` grammar (every region named — see below).
- `Quest_c` (List A) **fully field-labeled + validated** against `BeachTailsTemplate.bin`.
- `QuestcRegion` codec round-trips the template `@0–609` byte-exact (`QuestcRegionTests`) and now models
  that region inside `SnapshotTails` (replaced the opaque `Header0`/`Unknown*`/`RemoteFormat*` fields).
- **Generation**: `QuestcRecord.BuildSingle(QuestDefinition, …)` reproduces 10238's region byte-exact from
  `faolan.db` (`QuestcGenerationTests`) — the journal text is fully DB-sourced.

REMAINING to retire the `.bin`'s quest region for ARBITRARY characters (in priority order):
1. **Harvest the 3 undecoded per-quest scalars** (`N`, `@0xf0`, `@0xf8`) into `QuestDefinition`
   (importer + a migration). Today `BuildSingle` takes them as parameters; the beach values are
   `N=4, @0xf0=0x96, @0xf8=15`. LOW/MED.
2. **Build the `@609` per-goal objective-detail block + the quest index.** The `@609` grammar is now
   DECODED (`Captures/decode_snapshot_609.py`, `Snapshot609AnalysisTests`) — see the `@609` section below —
   but it is a per-GOAL block (the beach starter has 2 goals: 1448 + 3490, each with its own marker), NOT a
   `BuildQuestFullUpdate` block (that earlier claim was DISPROVEN 2026-06-16: 559 B vs 869 B, different
   header). So a builder + a **per-goal `QuestDefinition` model** (goals/markers/sub-text-refs) is the real
   remaining work here; the index grammar exists (`decode_snapshot_quests.py` / `SnapshotQuestInjector`).
3. **Wire into `BeachTailsBuilder`** behind a flag (default off) → in-client **chat-connect** smoke test
   (the first step that changes the emitted bytes; chat connecting = the client accepted the load).
Then the **access-window table** and **stats** regions are the last template-sourced pieces.

## Class id → reader

`0x737A6DF9` == BKDR-31(`FullPlayerCharacterIIR`) (confirmed; `docs/aoc-opcode-ids.md` row 33).

| class | vtable | `ReadSubClass` (slot 8) |
|---|---|---|
| `FullPlayerCharacterIIR` | `0208c6b4` | `FUN_0148c210` |
| `CoreFullPlayerCharacterIIR` | (base) | `FUN_00797440` |

`FUN_0148c210` calls the base `FUN_00797440` first, then reads one trailing list. So the on-wire tails are
**entirely the base `CoreFullPlayerCharacterIIR` body** followed by the outer trailing list.

## Stream primitives

- `FUN_0048f900` = **ReadU32** (big-endian; byte-swaps when the stream's BE flag `+0x1a` is set — same as
  every other IIR reader). All counts/ids below are ReadU32.
- `FUN_0048f840` = **ReadString** — a length-prefixed `std::string` (MSVC SSO: builds the string, stores
  `len`/`cap`, memcpy-inline when `len < 0x10` else heap pointer). This is the **only string reader** in the
  tails; the `<remoteformat>` XML tokens are read here.
- `FUN_0048f720` = **ReadU64** (wide read, returns `undefined8`).
- `FUN_00cc0540` = **Read4×U32** — a fixed 16-byte (4× ReadU32, BE-swapped) record; used as the per-objective
  numeric reference pair inside the `Quest_c` objective sublists.
- `FUN_00cba1b0` = the raw `memcpy`-from-stream primitive the inline ReadU32 expansions call.

## The `0x3F1` list-framing invariant

Most lists are framed by a count word `c` where **`c % 0x3F1 == 0` and `c > 0`**, and the **record count is
`c / 0x3F1 - 1`** (`0x3F1` = 1009). The client asserts `"(nSize%1009)==0 && nSize>0"` on violation. This is
the same invariant seen in the FA015807 spawn reader and GameActionList. Two lists instead use a **plain**
count (no `0x3F1`): the quest list and the outer trailing pair-list.

## Top-level grammar (in wire order)

`CoreFullPlayerCharacterIIR::ReadSubClass` (`FUN_00797440`):

1. `u32 version` — must == `0x18` (else `"...Wrong IIR_t version"`). This is `SnapshotTails.Header0`.
2. **List A — the quest journal** — `FUN_0091f5c0`: `0x3F1`-framed; `N-1` records of **`Quest_c`** (0x120-byte
   objects; the constructor `FUN_0091cf10` sets `Quest_c::vftable`), read by `FUN_0091df50` — see the per-record
   grammar below. **This is where the journal `<remoteformat>` XML lives** (the 5 header strings); it is the
   rich counterpart to the numeric `MainQuest_c` list (region 5).
3. **List B** — `FUN_00798c70` → `FUN_00922c90`: `0x3F1`-framed; `N-1` records of **112 bytes (stride 0x70)**.
   Each record (`FUN_00922c90`): `u32` + 3× `ReadString` + `FUN_005d7190` (**ACGHash reader** — reads a hash
   key, asserts `"Read invalid hash from stream"`) + `u32` (→ +0x1b) + `u32 innerCount` + per-entry inner reads
   that include a **nested `0x3F1`-framed string list** (`FUN_004d55b0`) and `FUN_0079b760`/`FUN_00922eb0`.
   Genuinely complex (nested string-lists + hashes); **not yet byte-validated** against the template — its
   exact record framing is the remaining unknown. (See the `@609` note below.)
4. **List C** — `FUN_004d4c70`: `0x3F1`-framed; `N-1` plain `u32` values.
5. **`MainQuest_c` list** — **plain** `u32 count`, then `count` × `MainQuest_c` (`FUN_00922210`). Each:
   `[u32 @0x14][u32 @0x18][u32 @0x30][u32 @0x34][u32 @0x44][u32 objCount][ (u32,u32) × objCount ][u32 @0x48]
   [u32 @0x4c][u32 @0x50]` — **all numeric, no strings** (the numeric state/goal tracking; the readable text
   for these quests is in the `Quest_c` journal list, region 2).
6. **List D** — `FUN_0077d220`: `0x3F1`-framed (records via `FUN_0077db70`; error path "does not return",
   typically empty for a fresh char).
7. **List E** — `FUN_0077d5b0` (374 B; not yet decoded).
8. **List F** — `FUN_004d5900`: `0x3F1`-framed; `N-1` records, each reads 2 `u32`, stores the 2nd.
9. **`StatList_t` #1** — `FUN_0139d450` @ `+0x74`: `0x3F1`-framed; `N-1` × `StatPair_t` (`FUN_0139d210` =
   `[u32 statId][u32 value]`, asserts `"StatPair_t:reading %d %d"`). **This is our `PostAccessBlock` stat
   table** (HP/Mana/Stamina/XP/Level/PvP + the archetype-derived stats).
10. **`StatList_t` #2** — `FUN_0139d450` @ `+0x78`: same grammar (the second stat table).
11. **`SpellBook_t`** — only if `*(this+0xe9) != 0`: `FUN_0077af90`: `0x3F1`-framed; `N-1` records of **20
    bytes (stride 0x14)**, each `[u32][u32][u32]` (asserts `"inconsistent SpellBook_t"`, `Spellbook.cpp`).

Then back in `FullPlayerCharacterIIR::ReadSubClass` (`FUN_0148c210`):

12. **Trailing pair-list** — **plain** `u32 count`, then `count` × `[u32,u32]` (wide reads via `FUN_0048f720`).

## `Quest_c` per-record grammar (`FUN_0091df50`) — VALIDATED against `BeachTailsTemplate.bin`

The outer List A loop (`FUN_0091f5c0`) reads **`u32 listKey` = the questId** before each record, then calls
`FUN_0091df50`. ReadString (`FUN_0048f840`) wire format = **`[u16 len][len bytes]`**. Field labels below are
confirmed by parsing the template's only `Quest_c` (quest **10238**), which consumes exactly to the next
region:

```
u32   listKey        # = questId (the List A map key; read by the outer loop, not the record reader)
--- Quest_c record (FUN_0091df50): ---
u32   ver     == 1   # @0x?? guard (else stream-error, bail)
u32   questId        # @0x0c  (10238)
u32   state          # @0x10  (1 = active; same field as QuestFullUpdate state)
u32   reserved == 0  # @0x114
u32   N              # @0x118 (4 — journal goal/line count)
u32   marker == 0x412# @0x14  (constant; also emitted by WorldQuest.BuildQuestFullUpdate)
string × 5           # [u16 len][bytes] <remoteformat> tokens:
                     #   50301 title, 50302 summary, 50300 area(id=241), 52000 region(id=4), 52000 region(id=4010)
u32   @0xec   (=1)
u32   areaId  @0xcc  (=241 — matches the 50300 string's id)
u32   questId @0x2c  (=10238 again)
u32   @0x11c  (=0)
u64   @0x20          (FUN_0048f720; =0 here — likely acceptTime)
u32   @0x28   (=0)
obj-list 1 → vec @0x10c   # u32 count (capped 100) — EMPTY (0) for 10238
obj-list 2 → vec @0x110   # u32 count (capped 100) — EMPTY (0)
u32   @0xf0   (=0x96=150)
u64   @0xf8          (=15 here)
obj-list 3 → vec @0x100   # u32 count (capped 100) — EMPTY (0)
obj-list 4 → vec @0x104   # u32 count (capped 100) — EMPTY (0)
                          # (post-read: sort + ref-count fixups; no further stream reads)
```

Each populated **`ObjRec`** (0x48-byte heap node; not exercised by the beach starter) reads 16 `u32`:
`u32 ×2 · u32 ×2 · Read4×U32 (text ref #1) · u32 ×2 · Read4×U32 (text ref #2) · u32 ×2`.

### The `@609` region (after `Quest_c`) — per-goal objective-detail block (DECODED 2026-06-16)

In the template, `Quest_c` ends at byte `@609` (its 4 numeric objective lists are **empty** for quest 10238 —
counts at `@581/@585/@601/@605` are all 0). The bytes from `@609` up to the quest index (`@1168`) are a
**per-GOAL objective-detail block** — its OWN grammar, fully decoded + validated by
`Captures/decode_snapshot_609.py` (parses to the index boundary exactly) and locked by
`Faolan.Tests/Snapshot609AnalysisTests`:

```
@609  u32 0x00000BD3            chapterTag (opens the region, per-chapter grouping word)
--- per goal (the beach starter 10238 has TWO: goalId 1448 then 3490) ---
u32 1 ; u32 k                   # goal separator ([1][9] goal 1, [1][12] goal 2)
u32 instance                    # the live player's client instance (re-addressed like the rest of the tails)
u32 questId ; u32 goalId ; u32 n ; u32 goalId(rep) ; u32 1
u32 ×7 0                        # reserved/padding
u32 subId ; u32 mapId ; u32 markerX ; u32 markerY   # goal 1 marker == QuestDefinition (416,701); goal 2 (521,757)
u32 0 ; u32 0
u32 objLen ; <remoteformat category="50304" …>      # objective text, u32-length-prefixed inline (NOT 00-len)
<00 u8len text record>          # a cat-51200 <remoteformat> (goal 1) or a cat-51000 <localized> (goal 2)
```

The objective TEXT is fully **DB-sourced** (every `key=` matches `QuestTextRefs` for quest 10238), but the
STRUCTURE is per-goal and the starter carries **two** goals each with its own world marker — richer than
`QuestDefinition` today (one `GoalId`, one marker). So generating this region needs a **per-goal definition
model** (Gitea #186, remaining item 2).

**CORRECTION (2026-06-16):** the earlier claim that `@609` is "a QuestFullUpdate block authored by
`WorldQuest.BuildQuestFullUpdate`" is **WRONG** for the beach starter. Empirically (`Snapshot609AnalysisTests`):
`BuildQuestFullUpdate`'s content for 10238 is **869 B** and starts `[questId][1][questId][3][0][3][0x412]`,
whereas the template's `@609` region is **559 B** starting `[0x0BD3][1][9][instance][questId][goalId]`. They
differ at byte 2 — different grammars. `BuildSnapshotQuestBlock`/`SnapshotQuestInjector` (the
`FAOLAN_SNAPSHOT_QUESTS` path) were validated against a *different* capture's 13479 block, not this region, so
they do **not** reproduce the beach `@609`. Generation of the beach region is therefore **not** authored yet.

**Reader (Ghidra) still partial.** Ruled out by byte layout: `QuestFullUpdateIIR::ReadSubClass` (= a `Quest_c`,
no `ver==1·questId·…·0x412` header here), List B (`FUN_00922c90`, mis-frames on the 2nd ReadString),
`MainQuest_c` (all-numeric), `Quest_c`. The static top-level order makes List B next after List A yet `@609`
fits none — likely List B/C / MainQuest are empty here and consume only a few bytes, shifting the true block
start; pinning the exact reader needs a **dynamic trace**. NOT blocking the *generator* now that the wire
grammar above is decoded — the blocker is the per-goal `QuestDefinition` model, not the byte layout.

Validated by the dissector `Captures/decode_questc.py` (parses version + List A + each `Quest_c` and asserts
it consumes exactly to the objective-block boundary `@609`; `listKey == questId`). A C# codec
`QuestcRegion`/`QuestcRecord` (Parse/ToArray + typed fields) round-trips the template's `@0–609` **byte-exact**
(`QuestcRegionTests`) and is **wired into `SnapshotTails`** — it now models the tails' front region instead of
the former opaque `Header0`/`Unknown*`/`RemoteFormat*` fields. The live snapshot is byte-identical (all 400
tests pass, incl. `BeachSnapshotConstructionTests`). Generating DIFFERENT quests from a character's
`CharacterQuests` is the next step (gated by an in-client chat-connect smoke test).

**Generation (DONE, byte-exact).** `QuestcRecord.BuildSingle(QuestDefinition, state, n, fieldEc, fieldF0,
fieldF8)` assembles a record from a DB `QuestDefinition` (its 5 header text refs + `AreaId`) plus the few
per-quest scalars not yet harvested into the definition (`N`, the `@0xf0`/`@0xf8` words). `QuestcGenerationTests`
proves building the beach starter (10238) from `faolan.db` reproduces the template's `@0–609` byte-exact - the
journal text is fully DB-sourced. Remaining for arbitrary quests: harvest `N`/`@0xf0`/`@0xf8` into
`QuestDefinition`, and regenerate the `@609` objective block + index (already authored by `BuildQuestFullUpdate`).

So the human-readable journal text is entirely the **5 header strings**; the numeric objective records are
unused for the beach starter (its objectives render from the strings). The raw wire emit is: `listKey` +
`[ver=1, questId, state, 0, N, 0x412]` + the 5 `<remoteformat>` strings + `[1, areaId, questId, 0]` +
`u64 0` + `0` + four `0` empty-list counts — i.e. a fresh single-quest `Quest_c` is fully authored from
`(questId, state, areaId, N, the 5 text tokens)`. The wire grammar is fully pinned and ground-truth-verified.

## What this means for `BeachTailsTemplate.bin`

The opaque `UnknownBeforeAccessWindowTable` is really **Lists A–F + the quest list** (regions 2–8); the
access-window table our heuristic finds by the `PFAccessWindow_` ASCII is one of those named-record lists
(its name string is read by a List A/B sub-reader, not a separate region), and `PostAccessBlock` = the two
`StatList_t`s (region 9–10). Every region is a `0x3F1`/plain-count list — **fully tractable**, same shape as
the spawn/loot/movetask readers already regenerated.

Remaining to author a fully-synthetic fresh-beach tails (and so delete the `.bin`):
- **`Quest_c` (List A) DONE** — wire grammar pinned + field-labeled + ground-truth-validated against the
  template (above); a fresh single-quest record is authorable from `(questId, state, areaId, N, 5 text tokens)`.
- Pin List B (`FUN_00922c90`) short reads + the `PFAccessWindow_` name source (List B is the prime suspect for
  the access-window entries our heuristic finds by ASCII).
- Then model regions 2–12 in `SnapshotTails` (today: typed header + opaque `UnknownBefore` + parsed
  access-window + typed `PostAccessBlock`) as the real list sequence and regenerate from server state.

Dumps: `/tmp/ghidra-aoc/fullchar_reader.txt`, `/tmp/ghidra-aoc/fullchar_records.txt` (regenerate with the two
scripts above).
