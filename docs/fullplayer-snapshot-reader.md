# FullPlayerCharacterIIR (0x737A6DF9) wire-reader — the snapshot "tails" grammar

Recovered from `AgeOfConan.exe` via the vtable-xref method (`Captures/ghidra/DumpFullCharReader.java` +
`DumpFullCharRecords.java`, slot 8 = `ReadSubClass`; see the agent memory `ghidra_vtable_xref_readers`).
Goal: replace the opaque `SnapshotTails.UnknownBeforeAccessWindowTable` blob (the last template-sourced
region of `BeachTailsTemplate.bin`) with a real, generated grammar so the `.bin` can be retired and the
character's **class/progression** authored instead of inherited from Strie. See `docs/quest-log-handover.md`
and the memory `snapshot_tails_construction_scope`.

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

### The `@609` region (after `Quest_c`) — objective detail block

In the template, `Quest_c` ends at byte `@609` (its 4 numeric objective lists are **empty** for quest 10238 —
counts at `@581/@585/@601/@605` are all 0). The bytes from `@609` are a **QuestFullUpdate-style objective
detail block**, NOT inside `Quest_c`:

```
@609 0x0bd3   marker      @613 1          @617 9        @621 0x02165eff (instance)
@625 10238 (questId)      @629 1448       @633 1        @637 1448       @641 1
@645..669 zeros           @673 180        @677 4010(map)  @681 416  @685 701
@697 0x64 → @699 [u16 len=100] "<remoteformat id=\"1448\" category=\"50304\" …>"  (objective text)
… then id=180 cat=51200, id=3490 cat=50304, <localized id=9500 cat=51000> …
```

This is exactly the shape `Captures/decode_snapshot_quests.py` parses and `WorldQuest.BuildQuestFullUpdate`
already emits byte-exactly (the handover notes it is "byte-identical to the QuestFullUpdate body from questId
onward"). So the objective TEXT is delivered by this block, keyed by questId, and is already an
authored/decoded grammar in the codebase. **Open:** map this block to its exact client reader in the
top-level sequence (it is one of List B/C / the `MainQuest_c` neighborhood — List B's `FUN_00922c90` record
shape does not match these bytes, so the order/identity needs another tracing pass).

So the human-readable journal text is entirely the **5 header strings**; the numeric objective records are
unused for the beach starter (its objectives render from the strings). **Generation:** emit `listKey` +
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
