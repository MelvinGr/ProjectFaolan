# FullPlayerCharacterIIR (0x737A6DF9) wire-reader — the snapshot "tails" grammar

Recovered from `AgeOfConan.exe` via the vtable-xref method (`tools/ghidra/DumpFullCharReader.java` +
`DumpFullCharRecords.java`, slot 8 = `ReadSubClass`; see the agent memory `ghidra_vtable_xref_readers`).
Goal (ACHIEVED for the tails, see Status): replace the captured `BeachTailsTemplate.bin` tails with a real,
generated grammar so the `.bin` can be retired and the character's **class/progression** authored instead of
inherited from Strie. `FullCharSnapshotSubClass.BuildFromScratch` reproduces the whole tails byte-for-byte from
generated quest + a game-static access-window catalog + the code stat skeleton, and is now the LIVE production
world-entry path (`GameServerListener.WorldEntry.cs` `SendBurstlessWorldEntry`, the burst-less entry #210 — the
only world-entry path); the captured `BeachTailsTemplate.bin` survives only as the golden test oracle. See the
memory `snapshot_tails_construction_scope`.

## Status & handover (2026-06-17, updated)

**The WHOLE tails now build from scratch — no captured-tails template.** `FullCharSnapshotSubClass.BuildFromScratch`
assembles the three regions and reproduces `BeachTailsTemplate.bin` byte-for-byte (`BeachTailsFromScratchTests`):
- **quest portion** `@0..1292` — DB-generated (`BuildQuestPortion`), wired into the production login
  snapshot (in-client verified 2026-06-16).
- **access-window table** `@1292..7917` — carved out into the typed game-static catalog `AccessWindowCatalog`
  (`Faolan.Core.Network/Constants/AccessWindowCatalog.cs`, #258; the 86-entry / 172 `PFAccessWindow_`-name
  playfield catalog, identical for every character — reference data, not generated; the former
  `AccessWindowTable.bin` blob is retired).
- **stat block** `@7917..end` — the code skeleton `SubClassStatBlock.CreateFreshBlock` (313 fixed slots + ~14
  character/map-derived, patched) serialized by `SubClassStatBlock.Serialize`.

So the captured Strie tails are no longer needed to *author* a beach entry's tails. `BeachTailsTemplate.bin`
now serves only as the golden test oracle. **`BuildFromScratch` is now wired into the burst-less world entry
(#210) — the ONLY world-entry path (`GameServerListener.WorldEntry.cs` `SendBurstlessWorldEntry`/
`BuildBurstlessSnapshot`); the captured-burst tails are retired.** The production FullChar tails are fully
generated. Branch `development`, all green (506 tests).

**UPDATE 2026-06-19 — MULTI-QUEST journal generates byte-exact + WIRED LIVE (#255 closed, verified in-client; commit e89e83fe).**
The quest region now carries the player's full set of held quests (from `CharacterQuests`), not just the beach quest —
so the login journal shows the real quests, not the captured beach char's phantom 2/30 (#254). Multi-quest wire order
(decoded byte-exact from the Strie/Stihetfv 2-quest `[beach+13479]` login snapshot, 2247 B):
`[QuestcRegion version 0x18][count=(N+1)*0x3F1]`, then **PER QUEST**: its `Quest_c` record IMMEDIATELY followed by its
OWN `@609` objective region — whose lead word is `(goals+1)*0x3F1` (NOT a constant `0xBD3`; beach 2 goals = `0xBD3`,
Casilda 1 goal = `0x7E2`; the old `QuestObjectiveRegion.ChapterTag` const is now `Frame`, computed). Then the quest
**index** = shared header `[0x3F1][marker=(N+1)*0x3F1][all questIds][count][sentinel 0xF1]` + **one block per quest**
`[magic][tf1..3][goalCount][(step,1..n) pairs][trailer questId,1,0,0,linkWord]`, where `magic` is the quest's goal-table
NAME word (beach `0x57365154` "W6QT", Casilda `0x52524C33" "RRL3") and the block trailers **CHAIN**: `linkWord` = the
NEXT held quest's `AreaId`, the LAST block = the primary's `IndexTrailerWord`. **All record/objective/index scalars are
PER-QUEST CONSTANTS** (byte-identical across a quest's active AND completed login captures — NOT progress-bearing; the
beach record is the same fresh 601 B early or late). Builder: `FullCharSnapshotSubClass.BuildQuestPortion(quests, primary,
instance)` + a `BuildFromScratch(quests, primary, ...)` overload (beach-only delegates → byte-identical). New
`QuestDefinition` cols: `Level`, `SnapshotUnknown28`, `IndexMagic` (+migrations; `SnapshotState` was collapsed into
`Level` and the `SnapshotField*` scalars renamed `SnapshotUnknown*` on 2026-07-20). A held quest joins the snapshot
only if `FullCharSnapshotSubClass.IsSnapshotEligible` — record scalars + the 5 header text refs + @609 goals +
`ObjectiveRegionVerified` + its area's 4CC. ⚠️ There is NO live fallback any more: `RestorePlayerQuests` was deleted
(#389/#390), so an ineligible quest is simply ABSENT from the journal. Golden: `BuildQuestPortionTests` reproduces the 2-quest region
byte-for-byte. NB the 3 earlier in-client crashes were WRONG ASSEMBLY ORDER (records-then-objective + single-block
index), NOT a progress/obj-list issue — the obj-lists are empty and the records are constants.

DONE 2026-06-16 (the single-quest beach region authors from server state):
- **Quest_c (`@0..609`)** — `QuestcRegion`/`QuestcRecord` codec (byte-exact round-trip), wired into
  `FullCharSnapshotSubClass`, and `QuestcRecord.BuildSingle` GENERATES it from `faolan.db` (`QuestcGenerationTests`).
- **`@609` objective region** — DECODED as a per-GOAL block (NOT a `BuildQuestFullUpdate` block — that was
  DISPROVEN: 559 B vs 869 B). Codec `QuestObjectiveRegion`/`QuestObjectiveGoal` (round-trip), wired into
  `FullCharSnapshotSubClass`, and `QuestObjectiveGoal.BuildSingle` GENERATES it from the per-goal DB model.
- **Quest INDEX (`@1168`)** — codec `QuestIndexRegion` (the `0x3F1` index + the `0x57365154` "W6QT" goal-id
  table), wired into `FullCharSnapshotSubClass`, with a `Build` generator.
- **Per-goal `QuestDefinition` model** — `QuestGoalDefinition` table + the snapshot-generation columns
  (`SnapshotUnknown118/UnknownEc/UnknownF0/UnknownF8`, `IndexMarker/TableField*/TrailerWord/IndexGoalSteps`) +
  migration; seeded
  for the beach starter (importer `SeedBeachQuestGoals`). `BuildWholeQuestPortion_ReproducesBeachTails`
  proves `@0..1292` regenerates byte-exact with NO literals (only the per-character quest state).
- **Production wiring (live, in-client verified 2026-06-16 — INTERIM, since SUPERSEDED by #210)** — the (now
  removed) `RetailLoginReplay` login-replay path generated the quest portion and spliced it over the captured
  tails' `@0..1292` unconditionally (graceful fallback to the captured region on failure); byte-identical for a
  fresh beach char. At that point `BuildFromScratch` was TEST-ONLY and production still shipped the captured
  world-burst tails for everything OUTSIDE the quest portion. **That interim is retired: #210 landed, so
  `FullCharSnapshotSubClass.BuildFromScratch` now builds the WHOLE tails and is the sole production path
  (`GameServerListener.WorldEntry.cs`); there is no captured-burst splice anymore.**
- **Stat block decoded** — `CharacterStatBlockTests`: only 25 of 338 values vary across fresh chars (class/map/XP
  + one session slot); the other 313 are a fixed skeleton, relocated to `SubClassStatBlock`.

DONE (2026-06-17): the **access-window table** (`[1292,7917)`, 86 `PFAccessWindow_` entries / 172 name strings)
was carved into the typed game-static catalog `AccessWindowCatalog` (#258; `AccessWindowTable.bin` retired), and
the **stat skeleton** + its patches are assembled by `FullCharSnapshotSubClass.BuildFromScratch` (serialized via
`SubClassStatBlock.Serialize`). Together with the generated quest portion, `BuildFromScratch` reproduces
`BeachTailsTemplate.bin` byte-for-byte (`BeachTailsFromScratchTests`).

DONE (#210, since landed): both remaining integration steps below are complete —
1. ✅ `BuildFromScratch` is wired into the burst-less world entry (#210); it is the ONLY world-entry path
   (`GameServerListener.WorldEntry.cs` `SendBurstlessWorldEntry`), so the player's own FullChar tails are
   generated, not a captured burst's.
2. **In-client chat-connect smoke test** — the standing acceptance gate for any byte-changing snapshot step.

## Multi-quest layout (2026-06-16) — toward authoring the player's full quest log

Decoded the 2-quest snapshot (`xxx-login`: 10238 + 13479) vs the 1-quest (`Strie`: 10238) to author the
player's accepted quests on login from `CharacterQuests` (replacing the snapshot's single beach quest). Findings:
- **Index marker is DERIVED from the count**: `marker = 0x3F1 * (questCount + 1)` (1 quest → `0x7E2`, 2 quests →
  `0xBD3`). It is NOT a per-quest constant; baked into `QuestIndexRegion.Build` (the `IndexMarker` DB column is now
  unused). Index = `[0x3F1][marker][questId × N][N][0xF1]` + the W6QT goal-id table with ALL quests' goal steps.
- Quest journal = N concatenated `Quest_c` records (one per quest); the index lists all N ids.

REMAINING to author multi-quest (the gating work, decode-first):
1. **Decode the 2-quest `Quest_c` List A + `@609` objective region.** Pinpointed 2026-06-16: running the production
   `FullCharSnapshotSubClass`/`QuestcRegion.Parse` on the `xxx-login` 2-quest tails THROWS `Quest_c record: listKey 3027 !=
   questId 9` (3027 = `0xBD3` = the objective region's chapterTag). So after the two `Quest_c` records the parser runs
   into the `@609` objective region and reads its `0xBD3`/`[1][9]` as a third record's listKey - i.e. `QuestcRegion`'s
   List A model (validated only on the 1-quest beach, whose record's 4 objective sub-lists are EMPTY and consume
   exactly to @609) does NOT handle the 2-quest case. Likely the 13479 record (state=3 in-progress) has NON-empty
   objective sub-lists that consume extra bytes, so the parser misaligns. Decode the 13479 `Quest_c` record's
   sub-lists + the 2-quest objective region from `xxx-login` (manual byte walk / dynamic Ghidra trace), then fix
   `QuestcRegion`/`QuestObjectiveRegion` to round-trip the `xxx-login` tails byte-exact - the prerequisite to
   generating multiple quests.

   **2-quest layout fully mapped from the bytes (2026-06-16, `xxx-login` snapshot body offsets):**
   ```
   @456  version 0x18
   @460  List A count 0xBD3 (= 0x3F1*3 -> records = 2)
   @464  10238 Quest_c record   (601 B, obj-lists EMPTY - byte-identical to the beach record)
   @1065 10238 objective region (0xBD3 chapterTag, 559 B - the beach "@609" block, 2 goals)
   @1624 13479 block            (879 B, QuestFullUpdate-content shape - objectives INLINE, NO 0xBD3)
   @2503 index (0x3F1 tag, marker 0xBD3, ids [27FE,34A7] count 2, W6QT table @2527)
   ```
   So `QuestcRegion.Parse` (loops `records=2`, reading `QuestcRecord` back-to-back) reads record 1 (10238, to
   @1065) then tries record 2 AT @1065 - the 10238 OBJECTIVE region (0xBD3), whose `[1][9]` reads as ver=1/questId=9
   -> the `listKey 3027(0xBD3) != questId 9` throw. KEY: the objective region is INTERLEAVED between the two
   Quest_c records, and the two quests are structurally DIFFERENT (10238 = split record + separate 0xBD3 objregion
   like the beach; 13479 = a single inline 879 B block). This is the exact ambiguity flagged above: whether the
   client's List A reads 2 records with List B/C/MainQuest consuming 10238's objregion, or List A interleaves
   record+objregion per quest. Disambiguating needs a **dynamic Ghidra trace** of `FUN_0091f5c0` (List A) +
   `FUN_00798c70`/`FUN_004d4c70`/`FUN_00922210` (Lists B/C/MainQuest) on the 2-quest tails to see which reader
   consumes @1065..1624. Then model the quest region as the real per-quest (record [+ objregion]) sequence in
   `FullCharSnapshotSubClass`/`QuestcRegion` and regenerate from `CharacterQuests`.
2. ~~**Harvest 13479's snapshot scalars + goals**~~ DONE 2026-07-20: harvested for every captured quest, not just
   the seeds (`ObjectiveGoalExtractor`, `QuestAreaExtractor`, `CompletedQuestExtractor`). ⚠️ The index block's
   `GoalSteps` are NOT goal ids - they are the AREA's quest catalog, renamed `Block.AreaQuestIds`.
3. **Extend `FullCharSnapshotSubClass.BuildQuestPortion` → `BuildQuestPortion(IReadOnlyList<(def,state)>)`** — emit N
   `Quest_c` records + the N-quest objective region + the N-id index; validate byte-exact against `xxx-login`'s
   2-quest region (golden test) before wiring.
4. **Wire from `CharacterQuests`** via `FullCharSnapshotSubClass.Deserialize`→replace the 3 quest regions→`ToArray()` (variable
   length; the access-window/stat regions follow) — drops the fixed `@0..1292` splice. Then in-client smoke test.

## Class id → reader

`0x737A6DF9` == BKDR-31(`FullPlayerCharacterIIR`) (confirmed; `docs/re/aoc-opcode-ids.md` row 33).

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

1. `u32 version` — must == `0x18` (else `"...Wrong IIR_t version"`). This is `QuestcRegion.Version`.
2. **List A — the quest journal** — `FUN_0091f5c0`: `0x3F1`-framed; `N-1` records of **`Quest_c`** (0x120-byte
   objects; the constructor `FUN_0091cf10` sets `Quest_c::vftable`), read by `FUN_0091df50` — see the per-record
   grammar below. **This is where the journal `<remoteformat>` XML lives** (the 5 header strings); it is the
   rich counterpart to the numeric `MainQuest_c` list (region 5).
3. **List B** — `FUN_00798c70` → `FUN_00922c90`: `0x3F1`-framed; `N-1` records of **112 bytes (stride 0x70)**.
   Each record (`FUN_00922c90`): `u32` + 3× `ReadString` + `FUN_005d7190` (**ACGHash reader** — reads a hash
   key, asserts `"Read invalid hash from stream"`) + `u32` (→ +0x1b) + `u32 innerCount` + per-entry inner reads
   that include a **nested `0x3F1`-framed string list** (`FUN_004d55b0`) and `FUN_0079b760`/`FUN_00922eb0`.
   Genuinely complex (nested string-lists + hashes). **EMPIRICALLY EMPTY (#258, 2026-06-21):** the
   per-record grammar — `u32 field0` + 3× `ReadString` + `u32 ACGHash` + `u32` + `u32 innerCount` +
   `innerCount ×{ u32 a, u32 b, nested 0x3F1 string-list }` — was probed by `tools/capture/decode_listb.py`
   against BOTH a fresh char (beach template / Faolanx) AND a developed char (NecroL80, 9 quests + a changemap).
   It matches NOTHING: there is no `0x3F1`-framed region anywhere in either tails whose first record reads 3
   printable strings. So List B carries **no records in practice** (it's a count-only empty list, like Lists
   C–F here) and **needs no authoring** — the from-scratch builder already reproduces both tails byte-exact
   without modeling it. The grammar above stays documented should a capture ever populate it. (This resolves
   the long-standing "List B is the remaining unknown / mis-frames at `@609`" note: `@609` is the per-goal
   objective region, NOT List B — `decode_listb.py` reads garbage strings there.)
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

## The per-character KNOWN-SPELL/FEAT list (decoded 2026-06-20, #74) — relog persistence + bar placement

NOT the stride-0x14 `SpellBook_t` above (region 11). The list that actually carries a character's **learned
spells** (and the feats/abilities the action bar binds to) is a separate `0x3F1`-framed list of **12-byte
records**:
```
[u32 id  big-endian] [u32 0xFFFFFFFF] [u8 0x00 ×3] [u8 type]      # type 2 = spell, 5 = feat/ability
```
Count `c = (recordCount + 1) * 0x3F1`. A *developed* char carries the class's whole available set (NecroL80 necro
= 108 records, RDB-catalog-derived, data-blocked); a **fresh** char carries only its learned spells (Faolanx
necro = 2 records = its 2 create-spells `0x42CA77`/`0x125D9B`, count `0x0BD3`).

In the captured beach tails (`BeachTailsTemplate.bin`) this list is **empty** at offset **`0x2425`** (count
`0x03F1`), because the captured Strie was a fresh char with no spells — identified by the preceding landmark
`00 00 00 00  00 00 02 90  00 00 00 00`. That empty list is why seeded spells USED to vanish on relog and type-4
attacks didn't place on the bar. **FIXED (#74):** the codec `KnownSpellList` (`FullCharSnapshotSubClass`'s
folder) models this list, and `BuildFromScratch` builds the character's own learned spells into it — the live
world entry loads `CharacterSpells` → `knownSpellIds` and passes them through (`GameServerListener.WorldEntry.cs`
loads them at world entry; `KnownSpellList.FromSpellIds` emits the type-2 records inside the stat block), so the
snapshot now lists the character's spells and they persist across relog + place on the bar.

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
u32   level          # @0x10  (=1; the quest's LEVEL, not a state - same field as the QuestFullUpdate level word,
                     #         see QuestDefinition.Level / #352. Beach 10238 = lvl 1; the snapshot carries it as
                     #         QuestDefinition.SnapshotState, which is therefore also the level.)
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
`QuestObjectiveRegionTests` (parses to the index boundary exactly) and locked by
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
differ at byte 2 — different grammars. (The now-removed `BuildSnapshotQuestBlock`/`SnapshotQuestInjector`
`FAOLAN_SNAPSHOT_QUESTS` experiment was validated against a *different* capture's 13479 block, not this region,
so it did **not** reproduce the beach `@609`.) The beach region IS now authored — by the dedicated codec
`QuestObjectiveRegion.Build` (byte-exact, `QuestObjectiveRegionTests`), NOT by `BuildQuestFullUpdate`.

**Reader (Ghidra) still partial.** Ruled out by byte layout: `QuestFullUpdateIIR::ReadSubClass` (= a `Quest_c`,
no `ver==1·questId·…·0x412` header here), List B (`FUN_00922c90`, mis-frames on the 2nd ReadString),
`MainQuest_c` (all-numeric), `Quest_c`. The static top-level order makes List B next after List A yet `@609`
fits none — likely List B/C / MainQuest are empty here and consume only a few bytes, shifting the true block
start; pinning the exact reader needs a **dynamic trace**. NOT blocking the *generator* now that the wire
grammar above is decoded — the blocker is the per-goal `QuestDefinition` model, not the byte layout.

Validated by the dissector `QuestcGenerationTests` (parses version + List A + each `Quest_c` and asserts
it consumes exactly to the objective-block boundary `@609`; `listKey == questId`). A C# codec
`QuestcRegion`/`QuestcRecord` (Parse/ToArray + typed fields) round-trips the template's `@0–609` **byte-exact**
(`QuestcRegionTests`) and is **wired into `FullCharSnapshotSubClass`** — it now models the tails' front region instead of
the former opaque `Header0`/`Unknown*`/`RemoteFormat*` fields. The live snapshot is byte-identical (all 400
tests pass, incl. `BeachSnapshotConstructionTests`). Generating DIFFERENT quests from a character's
`CharacterQuests` is the next step (gated by an in-client chat-connect smoke test).

**Generation (DONE, byte-exact).** `QuestcRecord.BuildSingle(QuestDefinition, state, n, fieldEc, fieldF0,
fieldF8)` assembles a record from a DB `QuestDefinition` (its 5 header text refs + `AreaId`) plus the few
per-quest scalars not yet harvested into the definition (`N`, the `@0xf0`/`@0xf8` words). `QuestcGenerationTests`
proves building the beach starter (10238) from `faolan.db` reproduces the template's `@0–609` byte-exact - the
journal text is fully DB-sourced. Remaining for arbitrary quests: harvest `N`/`@0xf0`/`@0xf8` into
`QuestDefinition`, and regenerate the `@609` objective block + index (authored by `QuestObjectiveRegion.Build` +
`QuestIndexRegion.Build`, NOT `BuildQuestFullUpdate`).

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

**EMPIRICAL DECOMPOSITION (#258, 2026-06-21).** `FullCharSnapshotSubClass.Deserialize` on the beach template now leaves
**ZERO opaque bytes** — `UnknownBefore = 0`, every byte falls into a typed region and `ToArray()` round-trips
byte-exact: `Quests 609 + QuestObjectives 559 + QuestIndex 124 + AccessWindowEntries (86 entries, 6625 B) +
PostAccessBlock (338 vals, 1365 B) = 9282`. So the beach tails are **fully decoded**; List B and the other
"middle" lists are empty (count-only). The `PFAccessWindow_` strings our heuristic walks are NOT List B — they
are a flat playfield-entry table (`u32 playfieldId, u8 section, u32 kind, u32, string name, …, childMode`) that
`FullCharSnapshotSubClass` ALREADY parses into typed `PfAccessWindowEntry` records. It is **game-static**: beach and the
developed NecroL80 snapshot both carry the SAME **172** `PFAccessWindow_` name strings — i.e. it is reference
data, identical for every character, not per-character progression.

Remaining to author a fully-synthetic fresh-beach tails (and so delete the `.bin`):
- **`Quest_c` (List A) DONE** — wire grammar pinned + field-labeled + ground-truth-validated against the
  template (above); a fresh single-quest record is authorable from `(questId, state, areaId, N, 5 text tokens)`.
- **List B et al. — NO WORK NEEDED:** empirically empty (above); the from-scratch builder already omits them
  and stays byte-exact.
- **Access-window table — DONE (#258):** the former verbatim `AccessWindowTable.bin` blob is retired. It is
  game-static (86 typed entries / 172 `PFAccessWindow_` names) and round-trips through the typed
  `PfAccessWindowEntry` model, so it now emits from the code catalog `AccessWindowCatalog`
  (`Faolan.Core.Network/Constants/AccessWindowCatalog.cs`) via `SerializeAccessWindowEntries` — byte-identical to
  the captured region, no per-character generation (identical for every char). This closes the #193
  construct-don't-replay purity goal for the tails.

Dumps: `tools/ghidra/output/fullchar_reader.txt`, `tools/ghidra/output/fullchar_records.txt` (regenerate with the two
scripts above).

## Per-character CLASS identity still open; class-derived stats now authored (2026-06-17, #239/#4/#186)
Historically the beach snapshot was the captured **Strie** packet (an Assassin = **Rogue**) and the login path
patched level / current vitals / quest / containers but **not the class or class-derived stats**, so every
character shipped Strie's Rogue identity (sheet showed "Barbarian", Rogue weapon proficiency, wrong mana).
Class-derived VITALS/mana are now authored from-scratch (see `ApplyClassVitals` below); the distinct class
*identity* in the front protobuf root is the remaining open piece (#239). Key decode facts (full writeup +
retail Necro captures):
- HP **is** in the tails stat block (`[23]/[54]` ×100; retail Necro HP 89 = 8900).
- **Mana IS** in the stat block after all (the earlier "mana not in the block" note was a search error, corrected
  in `CharacterStatBlockTests`): the DISPLAYED max mana lives at `[60]` = `[64]` (×100; verified across four
  archetypes' retail LOGIN snapshots — Necro `[64]=24600`), with a ~6-lower secondary at `[62]`. The vitals are
  now authored: `SubClassPostAccessBlock.ApplyClassVitals` writes HP/mana/stamina (displayed + base/secondary
  slots, ×100, keeping the `+148` repeated copies in sync) from `CharacterMaxStats.SnapshotVitals()` (#4/#186).
- The **class label is in the FRONT protobuf root** (`ProtoFullCharacterIIR`), not the tails; when a character's
  REAL captured stat sheet is available `BuildFromScratch` ships that sheet (imported), otherwise the fresh
  skeleton with per-class vitals patched. The distinct class *identity* in the root is still the open part (#239).
- (The earlier standalone tails-stat-block patch was reverted; the correct approach is the sheet-level authoring
  above. `CharacterStatBlockTests`'s WRONG label was `[78]=archetype` — every archetype has `[78]=3`; `[64]=mana` is
  CONFIRMED.)

## Inventory/bag carve-out (moved from Opcodes.cs 2026-07-06)

RE notes on the FullPlayerCharacterIIR (0x737A6DF9) inventory/bag serialization, formerly a comment block in the `GameServer0X2000RespondsOpcodes` enum:

```
FullPlayerCharacterIIR (0x737A6DF9) carve-out - the player inventory/bag serialization:
- after a 34-byte preamble, the body contains nested container-style blocks linked by field 6:
  101 -> f6 -> 104 -> f6 -> 112 -> f6 -> 113 -> f7=0
- each block currently looks like { f1=containerRef, f2=?, f3=?, repeated f4=itemEntry, f6=nextBlock? }
- confirmed blocks currently seen with the same shared owner/root handle: 101, 104, 112, 113
- block 101 carries 21 indices that exactly match the known Spawn appearance/customization ids
  (1,2,3,5,6,7,9,10,11,12,14,15,16,18,20,21,1002,1007,1011,1016,1018)
- block 104 is the strongest bag section: indices 0..29 line up with observed bag-slot moves,
  and a nested tiny message shaped like { f1=26, f2=count } reproduces stack counts such as
  20,78,100,34,96,83,82,56,4,1,5,3 from the earlier bag notes
- by count profile alone, 112 now looks resource-like (many non-1 stacks) and 113 looks quest-like
  (mostly count=1); this tentatively matches the CharacterEquipment model's Bag/Resources/Quest split
- the tiny message { f1=23, f2=value } now looks like a local inventory-record id rather than an
  item template id: across 104/112/113 it forms one perfect contiguous range 168534..168658
  and is the only tiny submessage that is present on every parsed entry in this sample
- { f1=26, f2=value } behaves like an optional stack-count field; when omitted, the entry still
  behaves like count=1, which matches the mostly-singleton quest-style block 113
- { f1=0, f2=value } behaves like optional instance state/flags rather than item type metadata
  because the same 4-character tail code can reappear with different count and even different
  presence/absence of this field (e.g. repeated codes in 104/112)
- each item entry ends with a stable 83-byte tail shaped like:
  { f1=len20, f2=len20, f3=len20, f4=fixed32, f5=fixed32, f7=varint, f8=fixed32 }
  where f1/f2/f3 are byte-identical, f4 always equals f5 in current samples, and f8 behaves like
  a short 4-character hash/type code because it uniquely determines the whole tail signature
- rare optional tiny submessages also exist (currently seen with leading kinds 265, 8, and 1290),
  which likely carry special-case item properties rather than core bag-slot state
- no server->client inventory packet follows later MoveItemInInventory requests, so the client
  appears to learn the initial bag state here rather than via a small incremental sync packet
```

## The client-side reader (Ghidra, 2026-07-20)

`CoreFullPlayerCharacterIIR::vftable` is at `0x01fc90bc` (loaded by the ctor `FUN_00796500`, which
passes the class name `"FullPlayerCharacterIIR"` to the IIR registrar). Slot **8** is `ReadSubClass`
= **`FUN_00797440`** — the reader for the tails this document describes. It was identified from the
`statlist.cpp` crash stack: frame `0x007975A6` falls inside it.

⚠️ The IIR ids are BKDR-31 hashes computed at runtime, so `0x737A6DF9` never appears as a constant -
searching for it finds nothing. Anchor on the class-name string's xrefs instead.

```c
version = ReadU32();                 // FUN_0048f900
if (version == 0x18) {               // == QuestcRegion.Version
    FUN_0091f5c0(stream);            // three sub-readers before the quest list
    FUN_00798c70(stream, this+0x20);
    FUN_004d4c70(stream, *(this+0x40));
    count = ReadU32();
    while (count--) {                // one MainQuest_c (0x54 bytes) per quest
        ...
        FUN_00922210(quest);         // the per-quest reader
    }
    FUN_0077d220(this+0x44);
    FUN_0077d5b0(this+0x54);
    FUN_004d5900(stream);
    FUN_0139d450(stream, *(this+0x74));   // StatList_t::read
    FUN_0139d450(stream, *(this+0x78));   // StatList_t::read
    ...
}
```

**Two stat lists sit directly after the quest loop.** That is why every quest-region desync in this
codebase surfaces as `statlist.cpp (134) (nSize%1009)==0 && nSize>0` — the stat list is the first
strict length check downstream, not the thing that is wrong.

The per-quest reader `FUN_00922210` reads five u32s into the quest object, then a **count followed by
u32 pairs** — the (questId, sequence) pairs our `QuestIndexRegion.Block.AreaQuestIds` models.

### The area 4CC is SERVER-authored

`FUN_00922210` **reads the 4CC off the stream and stores it**; nothing in the client looks one up by
area id. So there is no client-side area catalog, and the value cannot be recovered from the client
at all - not from the RDB, not from a table. Only captures carry it. (The RDB search was doomed for
this reason, not merely because records are compressed.)

Practical consequence: an area's 4CC becomes available only once a capture shows that area in a
completed-quest journal. See `QuestArea.Magic` and Gitea #389/#390.
