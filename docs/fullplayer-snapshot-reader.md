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
- `FUN_0048f840` = secondary fixed read (u16/byte-ish; used 3× inside the 112-byte record, not yet pinned).
- `FUN_0048f720` = wide read (returns `undefined8`) used by the trailing pair-list.
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
   objects; the constructor `FUN_0091cf10` sets `Quest_c::vftable`). The reader `FUN_0091df50` (5523 B):
   `u32 ver==1`, then ~scalar fields (ids/flags/state; a `u64` @+0x20 = likely acceptTime), then a count
   **capped at 100** (objectives), then several `std::string`/vector members (the `0xf` SSO-capacity inits at
   +0x11/+0x18/+0x39 and four `0x10`-byte heap allocs at +0x40…+0x44) holding the localized
   **title/summary/area/objective** text. **This is where the journal `<remoteformat>` XML lives** — it is the
   rich counterpart to the numeric `MainQuest_c` list (region 5). **TODO: finish field-mapping `FUN_0091df50`.**
3. **List B** — `FUN_00798c70` → `FUN_00922c90`: `0x3F1`-framed; `N-1` records of **112 bytes (stride 0x70)**.
   Each record (`FUN_00922c90`): `u32` + 3× `FUN_0048f840` + `u32` (→ +0x1b) + `u32 innerCount` + that many
   `[u32,u32]` pairs (built via `FUN_004d55b0`/`FUN_0079b760`). **TODO: pin the 3 short reads + name it.**
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

## What this means for `BeachTailsTemplate.bin`

The opaque `UnknownBeforeAccessWindowTable` is really **Lists A–F + the quest list** (regions 2–8); the
access-window table our heuristic finds by the `PFAccessWindow_` ASCII is one of those named-record lists
(its name string is read by a List A/B sub-reader, not a separate region), and `PostAccessBlock` = the two
`StatList_t`s (region 9–10). Every region is a `0x3F1`/plain-count list — **fully tractable**, same shape as
the spawn/loot/movetask readers already regenerated.

Remaining to author a fully-synthetic fresh-beach tails (and so delete the `.bin`):
- **Finish field-mapping `FUN_0091df50`** (List A = the `Quest_c` journal records, 5523 B) — the largest
  region; map its scalar fields + the objective sub-list + the string members. Highest-value next step for
  `.bin` removal. (Unrelated to the wrong displayed class — see #8 above.)
- Pin List B (`FUN_00922c90`) short reads + where the `PFAccessWindow_`/`<remoteformat>` strings are read.
- Then model regions 2–12 in `SnapshotTails` (today: typed header + opaque `UnknownBefore` + parsed
  access-window + typed `PostAccessBlock`) as the real list sequence and regenerate from server state.

Dumps: `/tmp/ghidra-aoc/fullchar_reader.txt`, `/tmp/ghidra-aoc/fullchar_records.txt` (regenerate with the two
scripts above).
