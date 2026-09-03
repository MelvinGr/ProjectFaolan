# FullPlayerCharacterIIR (0x737A6DF9) wire-reader — the snapshot "tails" grammar

Recovered from `AgeOfConan.exe` via the vtable-xref method (`tools/ghidra/DumpFullCharReader.java` +
`DumpFullCharRecords.java`, slot 8 = `ReadSubClass`; see the agent memory `ghidra_vtable_xref_readers`).
Goal (ACHIEVED for the tails, see Status): replace the captured `BeachTailsTemplate.bin` tails with a real,
generated grammar so the `.bin` can be retired and the character's **class/progression** authored instead of
inherited from Strie. `SnapshotTails.FromScratch` reproduces the whole tails byte-for-byte from
generated quest + a game-static access-window catalog + the code stat skeleton, and is now the LIVE production
world-entry path (`WorldEntryService.cs` `SendBurstlessWorldEntry`, the burst-less entry #210 — the
only world-entry path); the captured `BeachTailsTemplate.bin` survives only as the golden test oracle. See the
memory `snapshot_tails_construction_scope`.

## Tails build from scratch (2026-06-17)

**The WHOLE tails now build from scratch — no captured-tails template.** `SnapshotTails.FromScratch`
assembles the three regions and reproduces `BeachTailsTemplate.bin` byte-for-byte (`SnapshotTailsFromScratchTests`):
- **quest portion** `@0..1292` — DB-generated (`BuildQuestPortion`), wired into the production login
  snapshot (in-client verified 2026-06-16).
- **access-window table** `@1292..7917` — carved out into the typed game-static catalog `AccessWindowCatalog`
  (`Faolan.Core.Network/Constants/AccessWindowCatalog.cs`, #258; the 86-entry / 172 `PFAccessWindow_`-name
  playfield catalog, identical for every character — reference data, not generated; the former
  `AccessWindowTable.bin` blob is retired).
- **stat block** `@7917..end` — the code skeleton `SubClassStatBlock.CreateFreshBlock` (313 fixed slots + ~14
  character/map-derived, patched) serialized by `SubClassPostAccessBlockCodec.Build`.

So the captured Strie tails are no longer needed to *author* a beach entry's tails. `BeachTailsTemplate.bin`
now serves only as the golden test oracle. **`SnapshotTails.FromScratch` is now wired into the burst-less world entry
(#210) — the ONLY world-entry path (`WorldEntryService.cs` `SendBurstlessWorldEntry`/
`BuildBurstlessSnapshot`); the captured-burst tails are retired.** The production FullChar tails are fully
generated.

**UPDATE 2026-06-19 — MULTI-QUEST journal generates byte-exact + WIRED LIVE (#255 closed, verified in-client; commit e89e83fe).**
The quest region now carries the player's full set of held quests (from `CharacterQuests`), not just the beach quest —
so the login journal shows the real quests, not the captured beach char's phantom 2/30 (#254). Multi-quest wire order
(decoded byte-exact from the Strie/Stihetfv 2-quest `[beach+13479]` login snapshot, 2247 B):
`[QuestcRegion version 0x18][count=(N+1)*0x3F1]`, then **PER QUEST**: its `Quest_c` record IMMEDIATELY followed by its
OWN `@609` objective region — whose lead word is `(goals+1)*0x3F1` (NOT a constant `0xBD3`; beach 2 goals = `0xBD3`,
Casilda 1 goal = `0x7E2`; the old `QuestObjectiveRegion.ChapterTag` const is now the region's `ListFrame` count, computed). Then the quest
**index** = shared header `[0x3F1][marker=(N+1)*0x3F1][all questIds][count][sentinel 0xF1]` + **one block per quest**
`[magic][stepNumber][stepsAfter][tf3][goalCount][(step,1..n) pairs][trailer questId,1,0,0,linkWord]`, where `magic` is the quest's goal-table
NAME word (beach `0x57365154` "W6QT", Casilda `0x52524C33" "RRL3") and the block trailers **CHAIN**: `linkWord` = the
NEXT held quest's `AreaId`, the LAST block = the primary's `IndexTrailerWord`. **All record/objective scalars are
PER-QUEST CONSTANTS** (byte-identical across a quest's active AND completed login captures — NOT progress-bearing; the
beach record is the same fresh 601 B early or late). ⚠️ The index block's first two lead words are the exception, and
were mistaken for constants for the same reason as everything else here: they are the held quest's **step number in
its line** and the **steps after it** (`[1][count-1]` only while the character stands on step 1 - the beach line runs
1/3/5/8 across characters), see the 2026-09-03 update below (#926). Builder: `SnapshotTails.QuestPortion(quests, primary,
instance)` + a `BuildFromScratch(quests, primary, ...)` overload (beach-only delegates → byte-identical). New
`QuestDefinition` cols: `Level`, `SnapshotQuestLineReward`, `IndexMagic` (+migrations; `SnapshotState` was collapsed into
`Level` and the `SnapshotField*` scalars renamed `SnapshotUnknown*` on 2026-07-20). A held quest joins the snapshot
only if `FullCharSnapshotSubClass.IsSnapshotEligible` — record scalars + the 5 header text refs + @609 goals +
`ObjectiveRegionVerified` + its area's 4CC. ⚠️ There is NO live fallback any more: `RestorePlayerQuests` was deleted
(#389/#390), so an ineligible quest is simply ABSENT from the journal. Golden: `BuildQuestPortionTests` reproduces the 2-quest region
byte-for-byte. NB the 3 earlier in-client crashes were WRONG ASSEMBLY ORDER (records-then-objective + single-block
index), NOT a progress/obj-list issue — the obj-lists are empty and the records are constants.

## UPDATE 2026-07-20 (#391) — the quest INDEX is fully decoded across the corpus; NO phantom "IndexTag"

The measurement in #391 ("81 of 141 snapshots have no quest index we can identify") was the last big
degenerate-corpus trap. The index was modelled from beach/2-quest goldens only, so two beach-only accidents
were baked into the grammar. Both are now corrected and `FullCharSnapshotSubClassCodec.Parse` identifies the
index in **all 141** snapshots (`UnknownBeforeAccessWindowTable` 105,744 B → **0**, guarded by
`SnapshotOpaqueSurfaceTests`):

1. **There is NO leading `0x3F1` "IndexTag".** That word is the **empty completed-journal frame**
   (`ListFrame.ForCount(0) == 0x3F1`), which ALWAYS precedes the index. When a character HAS completed quests
   the frame is `(areas+1)*0x3F1` and the index follows with no `0x3F1` in front — which is exactly why the
   index parser (demanding a `0x3F1`) failed on all 81 snapshots that carry a completed journal. The wire is
   always `[active journal][completed-journal frame][quest index]`.
2. **The "`0xF1` sentinel" is the first block's quest AreaId** (beach area 241 == `0xF1`), and the block
   trailer `[1][0][0]` fields VARY per quest (a up to 8, c 0 or 1) — constant-looking only because every golden
   is a beach char. The lead pair `[tf1][tf2]` varies too, per CHARACTER (step number / steps after, #926). The `(questId, idx)` catalog pairs' second field is a 1-based **sequence that is NOT
   strictly monotonic** (branch/chapter quests repeat one), so the old `idx == i+1` assertion was also a
   beach-only accident.

Corrected grammar (no leading tag), `QuestIndexRegion` — as re-carved to the client's own `MainQuest_c` reader
(`FUN_00922210`) in the 2026-08-20 update below (the original #391 write-up hung each block's `areaId` off the
PREVIOUS block as a "chainWord", manufacturing a chain grammar and a phantom "terminal word"):
```
[u32 marker=(count+1)*0x3F1] [u32 questId × count] [u32 count]
per held quest (count blocks, id order):
  [u32 areaId] [u32 magic] [u32 stepNumber] [u32 stepsAfter] [u32 tf3=0]   # stepNumber + stepsAfter == n (#926)
  [u32 n] [ (questId, sequence) × n ]             # the AREA's full quest catalog
  [u32 trailerQuestId] [u32 a] [u32 b] [u32 c]
```
- `trailerQuestId` is the **area's ANCHOR quest** (catalog sequence 1), NOT the held quest — they coincide only
  when the held quest is the area's first. Beach: held 12371 (seq 5) but the block trailer is 10238 (seq 1).
  (`QuestIndexRegions.ForQuests` had this wrong; fixed — it exposed a real generation bug the beach goldens hid.)
- There is NO terminal word: the "constant `0x156E7`" the old carve ended on is the ACCESS-WINDOW TABLE's own
  `ListFrame(86)`, constant only because every capture carries the same 86-entry catalog.

Round-trip (`QuestIndexRegion` carries per-block `AreaId`, `AreaQuestSequences` and `TrailerA/B/C`) is
byte-exact for all 141. GENERATION from the DB is byte-exact only for the single-block beach shape today; the
multi-area per-quest sequences/trailers are not modelled in the DB yet (generation follow-up, #389), and
`IsSnapshotEligible` remains gated off, so nothing ships that path.

## UPDATE 2026-08-20 (#464) — the access-window table IS client list D, and the whole subclass reads sequentially

The `PFAccessWindow_` table was carved in #258 as a COUNT-LESS region located by a landmark search (the first
`PFAccessWindow_` name string), ended by a lookahead heuristic, with four `Unknown*` fields no capture ever
varied. Lining the tails' client read order against the bytes showed that carve was SHIFTED (#466's failure
shape): the table is **list D** — `PlayfieldAccessHolder`'s `0x3F1`-framed list (`FUN_0077d220`/`FUN_0077db70`,
`CoreCharacter\PlayfieldAccessHolder.cpp`) — and under the client's frame every phantom field dissolves:

```
table = [u32 (count+1)*0x3F1]  count × entry
entry = [u32 playfieldId][u8 flag][u32 accessType][u32 unknown][u16-str name][u32 childCount] childCount × child
child = [u8 flag][u32 id][u16-str displayName][u16-str parentName]
```

| old carve | actually |
|---|---|
| name's "trailing NUL" + `UnknownBeforeChildMode` (u16) + `childMode` (u8) | ONE big-endian u32 — the CHILD COUNT (≤3 everywhere, so its high bytes read as constants) |
| childMode 1/2/3 "variants" + "detail modes" + the mode-3 lookahead | plain `count × child` elements |
| `PfAccessWindowChild.Unknown` (u32, 0 in all 13) | the two u16 length words of a child's two EMPTY strings |
| `DetailUnknown` (u8) + 3-char `ShortCode` ("M2W") | one big-endian u32 id (5.03M–5.06M) |
| the quest index's "terminal `0x000156E7`" | the table's own `ListFrame(86)` |
| each index block's "chainWord" / the region's "leadAreaId" | the NEXT/first block's own leading `areaId` (`MainQuest_c` reads five head words: `FUN_00922210`) |

Verified byte-exact: the beach template's whole region walks under the client grammar to its exact last byte,
and the re-carved codecs round-trip **all 151** captured subclasses (the suite's corpus gates). The landmark
search and the `UnknownBeforeAccessWindowTable` gap field are gone — every region is now read SEQUENTIALLY —
and the one field that remains genuinely unnamed in the whole family is the entry head's `unknown` u32 (zero on
all 86 records, register: Gitea #464).

⭐ **What the table IS**: playfield ACCESS records — and the entry's second u32 is NAMED from the consumer:
it is the **ACCESS TYPE**. `PlayfieldAccessHolder`'s checker (`FUN_0077e200`) looks the playfield up in this
list's map and switches on the stored pair's `+0x14` — which the insert path (`FUN_0077e9a0`, key at node
`+0xC`, so pair layout `{key, u8 flag, u32 accessType, u32 unknown, …}`) pins to exactly this word: 0 and 2
pass, 1 and 3 log `"Access DENIED"`, anything else asserts `"Invalid access type %u for playfield %u"`. Its UI
caller (`FUN_014bd250`) opens the **`playfield_access_window`** off the same check — the window all these
`PFAccessWindow_` names belong to — i.e. the client half of the instance-entry gate (#511). The catalog
carries 2 on every base-game playfield and 3 on the Khitai / Dragon's Spine ones, so the "game-static" table
is really the captured ACCOUNT's entitlement state (it did not own those expansions); an entitled account
presumably receives 2s. List E (`FUN_0077d5b0`, `[u32 key][str][u32 ×3][u8][str]`, empty in every capture) is
the same holder's second list. The entry's `flag` byte is also consumed: the window-open flow reads it
(node `+0x10`) as a boolean gating an extra item/stat check before the window opens.

## UPDATE 2026-07-20 (#393) — the PostAccessBlock stat sheet is a BASELINE skeleton, not live stats

The two `StatList_t` (regions 9-10) are the last semi-opaque part of the tails. They are NOT opaque bytes —
`StatList.Read` already models them as `[u32 statId][u32 value]` pairs and they round-trip byte-exact — but
most `statId`s were named `CharacterStatId.Unknown##`. Dumping the sheet (`SubClassStatBlock.ExtractFromSnapshot`)
across **29 distinct archetype characters** (L1 fresh through L80, every class) settles what they are:

- **The sheet is a fixed BASELINE skeleton.** The attributes are the unmodified base — `Strength` (0x324) and
  `Intelligence` (0x328) are **10 for every character, L1 to L80** — and ~two-thirds of the slots are 0/constant
  across all 29. The character's real leveled stats do NOT ride here; they arrive via the live `StatIIR`
  (0xF98E10B3, 23838 packets in the corpus) and vitals (0x96B8DC59) updates. So there is no large hidden stat
  block to decode — the snapshot sheet is a template with a handful of per-character value islands.
- **The only slots that vary per character** are: `PlayfieldToken` (session), the vitals (HP/mana/stamina, by
  class+level), XP/level, the PvP counters (already named), and ~10 genuine unknowns.
- **Newly decoded:** `0x0039` = **`ExpCurrentLevelBase`** — the total XP at which the current level began, so the
  XP bar fills `(Exp - base) / (MaxExp - base)`. Airtight: `base <= Exp <= MaxExp` everywhere, `base == Exp` for a
  freshly-dinged char, and **level 24's base (263640) is exactly level 23's `MaxExp` (263640)** — each level's
  base is the previous level's cap.
- **Non-scalar slots** (characterised across each character's many captures, #393):
  - `0x044A` → **`Timestamp`** — a unix epoch (values decode to real 2010 dates; the same value recurs across
    different characters, one character varies across captures) — a shared session/server time, not a scalar stat.
  - `0x025F` — a per-character CONSTANT (byte-identical across NecroL80's 42 captures), only 3 distinct values
    corpus-wide. NOT race: two Stygian-only necros (Faolanx, Stickienecro) differ. An appearance/creation bucket or
    identity hash; left `Unknown`.
  - `0x0398/9A/9B/9D` — four SIGNED values, all four equal per char, base -300, scaling WITH LEVEL (Hoxxling's quad
    tracks her `ExpCurrentLevelBase` across captures). A level-scaled rating quad; left `Unknown` (needs probing).

The remaining per-character unknowns cannot be named from captures (the values track level/class but map to no
independently-observable quantity). Naming them needs **in-client `.setlocal <id> <value>` probing** — the method
that pinned HP/mana/stamina current-vs-max (see `CharacterStatId`). That is the open tail of #393; the bytes
already round-trip, so it is labelling, not decoding.

DONE 2026-06-16 (the single-quest beach region authors from server state):
- **Quest_c (`@0..609`)** — `QuestcRegion`/`QuestcRecord` codec (byte-exact round-trip), wired into
  `FullCharSnapshotSubClass`, and `QuestcRecords.ForQuest` GENERATES it from `faolan.db` (`QuestcGenerationTests`).
- **`@609` objective region** — DECODED as a per-GOAL block (NOT a `BuildQuestFullUpdate` block — that was
  DISPROVEN: 559 B vs 869 B). Codec `QuestObjectiveRegion`/`QuestObjectiveGoal` (round-trip), wired into
  `FullCharSnapshotSubClass`, and `QuestObjectiveRegions.ForQuest` GENERATES it from the per-goal DB model.
- **Quest INDEX (`@1168`)** — codec `QuestIndexRegion` (the `0x3F1` index + the `0x57365154` "W6QT" goal-id
  table), wired into `FullCharSnapshotSubClass`, with a `Build` generator.
- **Per-goal `QuestDefinition` model** — `QuestGoalDefinition` table + the snapshot-generation columns
  (`Field118/FieldEc/FieldF0/FieldF8`, `IndexMarker/IndexTableField*/IndexTrailerWord/IndexQuestLineQuestIds`) +
  migration; seeded
  for the beach starter (importer `SeedBeachQuestGoals`). ⚠️ `IndexMagic`, `IndexTableField1..3` and
  `IndexQuestLineQuestIds` are no longer seeded — `DeriveQuestIndexFromLine` computes them from `QuestLines.Magic`
  and the line's steps in `JournalSequence` order (Gitea #641), which reproduces every deleted literal exactly. `BuildWholeQuestPortion_ReproducesBeachTails`
  proves `@0..1292` regenerates byte-exact with NO literals (only the per-character quest state).
- **Production wiring (live, in-client verified 2026-06-16 — INTERIM, since SUPERSEDED by #210)** — the (now
  removed) `RetailLoginReplay` login-replay path generated the quest portion and spliced it over the captured
  tails' `@0..1292` unconditionally (graceful fallback to the captured region on failure); byte-identical for a
  fresh beach char. At that point `SnapshotTails.FromScratch` was TEST-ONLY and production still shipped the captured
  world-burst tails for everything OUTSIDE the quest portion. **That interim is retired: #210 landed, so
  `SnapshotTails.FromScratch` now builds the WHOLE tails and is the sole production path
  (`WorldEntryService.cs`); there is no captured-burst splice anymore.**
- **Stat block decoded** — `CharacterStatBlockTests`: only 25 of 338 values vary across fresh chars (class/map/XP
  + one session slot); the other 313 are a fixed skeleton, relocated to `SubClassStatBlock`.

DONE (2026-06-17): the **access-window table** (`[1292,7917)`, 86 `PFAccessWindow_` entries / 172 name strings)
was carved into the typed game-static catalog `AccessWindowCatalog` (#258; `AccessWindowTable.bin` retired), and
the **stat skeleton** + its patches are assembled by `SnapshotTails.FromScratch` (serialized via
`SubClassPostAccessBlockCodec.Build`). Together with the generated quest portion, `SnapshotTails.FromScratch` reproduces
`BeachTailsTemplate.bin` byte-for-byte (`SnapshotTailsFromScratchTests`).

DONE (#210, since landed): both remaining integration steps below are complete —
1. ✅ `SnapshotTails.FromScratch` is wired into the burst-less world entry (#210); it is the ONLY world-entry path
   (`WorldEntryService.cs` `SendBurstlessWorldEntry`), so the player's own FullChar tails are
   generated, not a captured burst's.
2. **In-client chat-connect smoke test** — the standing acceptance gate for any byte-changing snapshot step.

## Multi-quest layout (2026-06-16)

Decoded the 2-quest snapshot (`xxx-login`: 10238 + 13479) vs the 1-quest (`Strie`: 10238), authoring the
player's accepted quests on login from `CharacterQuests` (replacing the snapshot's single beach quest). Findings:
- **Index marker is DERIVED from the count**: `marker = 0x3F1 * (questCount + 1)` (1 quest → `0x7E2`, 2 quests →
  `0xBD3`). It is NOT a per-quest constant; baked into `QuestIndexRegions.ForQuests` (the `IndexMarker` DB column is now
  unused). Index = `[0x3F1][marker][questId × N][N][0xF1]` + the W6QT goal-id table with ALL quests' goal steps.
- Quest journal = N concatenated `Quest_c` records (one per quest); the index lists all N ids.

Multi-quest decode notes:
1. **The 2-quest `Quest_c` List A + `@609` objective region.** Pinpointed 2026-06-16: running the production
   `FullCharSnapshotSubClass`/`QuestcRegionCodec.Parse` on the `xxx-login` 2-quest tails THROWS `Quest_c record: listKey 3027 !=
   questId 9` (3027 = `0xBD3` = the objective region's chapterTag). So after the two `Quest_c` records the parser runs
   into the `@609` objective region and reads its `0xBD3`/`[1][9]` as a third record's listKey - i.e. `QuestcRegion`'s
   List A model (validated only on the 1-quest beach, whose record's 4 objective sub-lists are EMPTY and consume
   exactly to @609) does NOT handle the 2-quest case. Likely the 13479 record (state=3 in-progress) has NON-empty
   objective sub-lists that consume extra bytes, so the parser misaligns.

   **2-quest layout fully mapped from the bytes (2026-06-16, `xxx-login` snapshot body offsets):**
   ```
   @456  version 0x18
   @460  List A count 0xBD3 (= 0x3F1*3 -> records = 2)
   @464  10238 Quest_c record   (601 B, obj-lists EMPTY - byte-identical to the beach record)
   @1065 10238 objective region (0xBD3 chapterTag, 559 B - the beach "@609" block, 2 goals)
   @1624 13479 block            (879 B, QuestFullUpdate-content shape - objectives INLINE, NO 0xBD3)
   @2503 index (0x3F1 tag, marker 0xBD3, ids [27FE,34A7] count 2, W6QT table @2527)
   ```
   So `QuestcRegionCodec.Parse` (loops `records=2`, reading `QuestcRecord` back-to-back) reads record 1 (10238, to
   @1065) then tries record 2 AT @1065 - the 10238 OBJECTIVE region (0xBD3), whose `[1][9]` reads as ver=1/questId=9
   -> the `listKey 3027(0xBD3) != questId 9` throw. KEY: the objective region is INTERLEAVED between the two
   Quest_c records, and the two quests are structurally DIFFERENT (10238 = split record + separate 0xBD3 objregion
   like the beach; 13479 = a single inline 879 B block). This is the exact ambiguity flagged above: whether the
   client's List A reads 2 records with List B/C/MainQuest consuming 10238's objregion, or List A interleaves
   record+objregion per quest.
2. ~~**Harvest 13479's snapshot scalars + goals**~~ DONE 2026-07-20: harvested for every captured quest, not just
   the seeds (`ObjectiveGoalExtractor`, `QuestLineExtractor`, `CompletedQuestExtractor`). ⚠️ The index block's
   `GoalSteps` are NOT goal ids - they are the AREA's quest catalog, renamed `Block.AreaQuestIds`.

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

1. `u32 version` — must == `0x18` (else `"...Wrong IIR_t version"`). This is `QuestcRegionCodec.Version`.
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
   `[u32 @0x14][u32 @0x18][u32 @0x30][u32 @0x34][u32 @0x44][u32 objCount][ (u32,u32) × objCount ][u32 tracked]
   [u32 @0x48][u32 @0x4c][u32 @0x50]` — **all numeric, no strings** (the readable text for these quests is in
   the `Quest_c` journal list, region 2). This is `QuestIndexRegion.Block`, field for field: `@0x14` AreaId,
   `@0x18` the ACGHash, `@0x30`/`@0x34` StepNumber/StepsAfter, `@0x44` TableField3, then the pair list, then
   TrailerQuestId (matched into the pair list and parked in the current-quest slot `@0x38`) and
   TrailerA/B/C at `@0x48`/`@0x4c`/`@0x50`. ⭐ **`@0x30`/`@0x34` are per-character PROGRESS, not line data**
   (2026-09-03, #926): on all 2,626 captured blocks `@0x30` is the held quest's own sequence in the pair list and
   `@0x34` is `n - @0x30`. Their reader is the journal's line panel `FUN_01617260`, which fetches the line through
   `CoreQuestHolder::GetMainQuest` (`FUN_008107d0`, a scan of the holder's `+0x4c..+0x50` list on `+0x14`), takes
   `@0x30` as a running counter through the Roman-numeral formatter `FUN_010cfbc0` (the I/II/III the journal shows
   for a line's chapters), then draws `@0x34` placeholder rows for the steps to come and hides that section
   (`FUN_00ae3b30(widget+0x4e0, 0, 1)`) when `@0x34` is 0. The `[1][count-1]` reading held only because every
   captured character stood on step 1 of the early corpus's lines. ⭐ **`@0x44`/`@0x48`/`@0x50` are the SAME slots the incremental
   `MainQuestUpdateIIR`'s Unknown1/Unknown0/Unknown2 land in** (`FUN_00810840` + the ctor `FUN_00921980`), so
   the snapshot block and the IIR trailer are two producers of one struct (#464). The one consumer found:
   `@0x48` is the quest TRACKER's sort tiebreaker (`FUN_012ce1c0` parks it in `QJQuestNode_c +0x70`;
   `FUN_012cde80`/`FUN_012cbbb0` compare it) — per line it is 1 on solo, 4 on raid, 8 on group-dungeon
   content across all 97 captured blocks. `@0x44` and `@0x50` have no reader (re-checked 2026-09-03: neither of the two direct `GetMainQuest`
   callers touches them, the class's 7 vtable slots are the `PtrTarget_c` boilerplate, and an offset census over
   the struct's slot set is too generic to anchor - 889 functions); see `MainQuestUpdateIIR`'s record remarks
   for the per-field data. `@0x50` is ALSO the word after the 4CC in the completed journal's line record
   (`FUN_00922c90` stores it at `+0x6c` of the 0x70-byte record, after the strings at `+0x04`/`+0x20`/`+0x3c`
   and the hash at `+0x68`): the two agree on every line captured in both records, so one `QuestLine.TrailerFlag`
   column feeds both. Its population, decrypted: the Kalanthes raid chain, a World Bosses mastery line, the
   2014 anniversary and 2015 Halloween event lines, two Onslaught lines (#926).
6. **List D** — `FUN_0077d220`: `0x3F1`-framed. Record = `[u32 key]` (read by the outer loop) +
   (`FUN_0077db70`): `[u8 requiresToken][u32 accessType][u32 secondsRemaining][string][u32 innerCount][ innerCount x ([u8][u32][string][string]) ]`
   — VARIABLE-WIDTH (the inner count is a PLAIN count, not `0x3F1`-framed).
   ⭐ **This IS the `PFAccessWindow_` access-window table** (#464): the records are `PlayfieldAccessHolder`'s
   playfield-access entries (`CoreCharacter\PlayfieldAccessHolder.cpp`, channel `GameCode/PlayfieldAccess`),
   86 on every captured subclass. ⭐ **Every field of the record is named as of 2026-08-29** — the head bool is
   `RequiresToken` (the opener `FUN_014bd250` queries token pool 50 = `TeleportCoins` when it is set and refuses
   with LDB-100 `PlayfieldAccessNoTokens`, *"You do not have enough Teleport Coins to travel"*), and the third
   word is `SecondsRemaining` on a TIMED access (the holder's update slot `FUN_0113a390` warns below
   `TWK_PlayfieldAccess_WarningTimer` = 300 with `EndOfPlayfieldAccessWarning`, *"You will lose access to %s in
   %d minutes"*, dividing by 60). ⚠️ The same record travels alone as `DispatchSendPlayfieldAccessData`'s
   `accessInfo` — that handler calls this very reader — and `DispatchPlayfieldAccessGranted(playfieldId, hours,
   allCharacters)` is what grants one. See `PfAccessWindowEntry` for the full derivation. An earlier pass called this list "empty on all 151" — that was the
   off-by-one-list alignment the old landmark-carved table forced (see the 2026-08-20 update below).
7. **List E** — `FUN_0077d5b0`: `0x3F1`-framed. Record = `[u32 key]` (read by the outer loop) +
   `FUN_0077df00`: `[string][u32][u32][u32][u8][string]` — also VARIABLE-WIDTH.
   **Empty on all 151 captured subclasses.** Also a `PlayfieldAccessHolder` list; its access-type consumer
   (`FUN_0077e200`) reads the stored record's `+0x14` and refuses entry on types 1/3 ("Access DENIED"), which
   is the client half of the #511 instance-entry gate.
8. **List F** — `FUN_004d5900`: `0x3F1`-framed; `N-1` records, each reads 2 `u32`, stores the 2nd.
   **Empty on all 151** (the "F×17" of the earlier post-access walk was `StatList_t` #1's own 17 pairs —
   byte-identical 8-byte records, which is also why that walk had to call the second stat list "stat×0").
9. **`StatList_t` #1** — `FUN_0139d450` @ `+0x74`: `0x3F1`-framed; `N-1` × `StatPair_t` (`FUN_0139d210` =
   `[u32 statId][u32 value]`, asserts `"StatPair_t:reading %d %d"`). **This is our `PostAccessBlock` stat
   table** (HP/Mana/Stamina/XP/Level/PvP + the archetype-derived stats).
10. **`StatList_t` #2** — `FUN_0139d450` @ `+0x78`: same grammar (the second stat table).
11. **`SpellBook_t`** — only if `*(this+0xe9) != 0`: `FUN_0077af90`: `0x3F1`-framed; `N-1` records of **20
    bytes (stride 0x14)**, each `[u32][u32][u32]` (asserts `"inconsistent SpellBook_t"`, `Spellbook.cpp`).

Then back in `FullPlayerCharacterIIR::ReadSubClass` (`FUN_0148c210`):

12. **Trailing list** — **plain** `u32 count`, then `count` × **17 bytes**: `[u8][u64][u64]`. ⚠️ This was
    written up as `count × [u32,u32]`; `FUN_0048f720` reads **8** bytes (`FUN_00cba1b0(&buf, 8)`), and the
    loop reads one raw byte before the two wide reads. A closing `u32` follows the list.

### How much of the tail this actually walks (2026-07-28, #393)

Walking the post-catalog remainder (then carried as an opaque `UnknownAfterAccessWindowTable` byte[]; since #464 it is read sequentially as `SubClassPostAccessBlock` with no fallback) with the grammar above — Lists D/E (variable-width, both empty), List F,
`StatList_t` #1, then either `StatList_t` #2 or the 12-byte known-spell list, then the trailing 17-byte-element
list and its closing word — consumes **65 of 151** captured subclasses to their exact last byte:

| walk | subclasses |
|---|---|
| `D0, E0, F×17, stat×148, stat×0, trail×1` | 24 |
| `D0, E0, F×17, stat×148, trail×0` | 21 |
| `D0, E0, F×17, stat×148, spells×1, trail×1` | 11 |
| `D0, E0, F×17, stat×148, spells×2, trail×1` | 9 |

**CLOSED — 151/151 (2026-07-28).** The two things blocking it were both errors in this document, and both fell
out of decompiling rather than measuring:

- **`SpellBook_t`'s wire record is 12 bytes, not 20.** `FUN_0077af90` makes THREE 4-byte reads per record and
  advances its in-memory array by `0x14` — the documented "records of 20 bytes (stride 0x14)" was the in-memory
  stride. At 12 bytes it is the same `[u32 id][u32 0xFFFFFFFF][u32 type]` list `KnownSpellList` already models
  (#74), so region 11 and the "separate per-character known-spell list" are one list, not two.
- **There are TWO trailing lists, not one.** The base reader ends with `[u32 n][ n × ([u8][u64][u64]) ]` (17-byte
  element), and `FullPlayerCharacterIIR::ReadSubClass` then reads its own `[u32 m][ m × ([u32][u32]) ]`. The
  second one's ids are RDB template ids (`0xF426A` = 1000042, the feat id-map), so it reads as a counter table.

With those corrected, every one of the 151 captured subclasses parses to its exact last byte and re-encodes
byte-exact through `SubClassPostAccessBlock`. Every capture opens identically — `D×0, E×0, StatList#1×17,
StatList#2×148` — and what varies is the spell list, i.e. the per-character part.
[V3: SubClassPostAccessBlockTests.EveryCapturedPostAccessBlock_ParsesAndReEncodesByteExact]

The spell list is **optional**, not merely sometimes empty: 21 of the 151 end at `StatList#2` and go straight to
the two zero-count tails, and each of those is its capture's FIRST snapshot — the one the create editor is sent.
The other 130 carry the list, empty (its bare `0x3F1` frame) or not; absent and empty are different bytes.
[V3: SubClassPostAccessBlockTests.TheSpellBookList_IsAbsentOnlyInTheSessionsFirstSnapshot]

Lists D and E carry VARIABLE-width records (both call ReadString) and are empty in all 151, so which leading
empty frame is which is not observable on the wire, and neither is the record shape behind them; the model reads
them as the empty lists they are rather than labelling them beyond what the captures can prove.

### The stat table LABELS ITSELF (2026-07-28, #393)

`StatList_t` #1 is not a positional array that needs slot names — every element is `[u32 statId][u32 value]`, and
**all 22,348 stat ids across the 151 captures are defined `CharacterStatId` values**, not one unknown. The names
read exactly as a character sheet: `HealthCurrent=8000`, `HealthMax=8000`, `Intelligence=10`, `StaminaMax=5000`,
`PvpMaxExp=3500`. A value is found BY ID; nothing needs counting.

The table is also **two identical copies** of the same 74-pair run — same ids *and* same values in both halves, on
every capture. That doubling is what `SubClassPostAccessBlock.HasRepeatedStatTable` detects, and why patching a
vital has to write both halves.
[V3: SubClassPostAccessBlockTests.TheStatTable_IsSelfLabellingAndDoubled]

`SubClassPostAccessBlock` addresses the table BY ID (`GetStat`/`SetStat` across both lists). It used to index it
positionally through a `CharacterValueIndex` enum of early-dev guesses (word 80 is MaxExp, not XP) over a flat
`uint[]` view of the whole region — replaced with the two typed `StatList`s in #543.

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
folder) models this list, and `SnapshotTails.FromScratch` builds the character's own learned spells into it — the live
world entry loads `CharacterSpells` → `knownSpellIds` and passes them through (`WorldEntryService.cs`
loads them at world entry; `KnownSpellLists.FromSpellIds` emits the type-2 records inside the stat block), so the
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
u32   marker == 0x412# @0x14  (carried per quest; QuestFullUpdateCodec emits the same record)
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
`QuestDefinition` today (one `GoalId`, one marker). Open work is tracked in Gitea #186.

**CORRECTION (2026-06-16):** the earlier claim that `@609` is "a QuestFullUpdate block authored by
`QuestFullUpdateCodec`" is **WRONG** for the beach starter. Empirically (`Snapshot609AnalysisTests`):
`BuildQuestFullUpdate`'s content for 10238 is **869 B** and starts `[questId][1][questId][3][0][3][0x412]`,
whereas the template's `@609` region is **559 B** starting `[0x0BD3][1][9][instance][questId][goalId]`. They
differ at byte 2 — different grammars. (The now-removed `BuildSnapshotQuestBlock`/`SnapshotQuestInjector`
`FAOLAN_SNAPSHOT_QUESTS` experiment was validated against a *different* capture's 13479 block, not this region,
so it did **not** reproduce the beach `@609`.) The beach region IS now authored — by the dedicated codec
`QuestObjectiveRegions.ForQuest` (byte-exact, `QuestObjectiveRegionTests`), NOT by the live full-update.

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
tests pass, incl. `BeachSnapshotConstructionTests`).

**Generation (DONE, byte-exact).** `QuestcRecords.ForQuest(QuestDefinition, state)` (was `BuildSingle(.., n, fieldEc, fieldF0,
fieldF8)` assembles a record from a DB `QuestDefinition` (its 5 header text refs + `AreaId`) plus the few
per-quest scalars not yet harvested into the definition (`N`, the `@0xf0`/`@0xf8` words). `QuestcGenerationTests`
proves building the beach starter (10238) from `faolan.db` reproduces the template's `@0–609` byte-exact - the
journal text is fully DB-sourced.

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

**EMPIRICAL DECOMPOSITION (#258, 2026-06-21).** `FullCharSnapshotSubClassCodec.Parse` on the beach template now leaves
**ZERO opaque bytes** — `UnknownBefore = 0`, every byte falls into a typed region and `ToArray()` round-trips
byte-exact: `Quests 609 + QuestObjectives 559 + QuestIndex 124 + AccessWindowEntries (86 entries, 6625 B) +
PostAccessBlock (338 vals, 1365 B) = 9282`. So the beach tails are **fully decoded**; List B and the other
"middle" lists are empty (count-only). The `PFAccessWindow_` strings our heuristic walks are NOT List B — they
are a flat playfield-entry table (`u32 playfieldId, u8 section, u32 kind, u32, string name, …, childMode`) that
`FullCharSnapshotSubClass` ALREADY parses into typed `PfAccessWindowEntry` records. It is **game-static**: beach and the
developed NecroL80 snapshot both carry the SAME **172** `PFAccessWindow_` name strings — i.e. it is reference
data, identical for every character, not per-character progression.

Regions of a fully-synthetic fresh-beach tails (which lets the `.bin` be deleted):
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

## Per-character CLASS identity; class-derived stats now authored (2026-06-17, #239/#4/#186)
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
  REAL captured stat sheet is available `SnapshotTails.FromScratch` ships that sheet (imported), otherwise the fresh
  skeleton with per-class vitals patched. The distinct class *identity* in the root is still the open part (#239).
- (The earlier standalone tails-stat-block patch was reverted; the correct approach is the sheet-level authoring
  above. `CharacterStatBlockTests`'s WRONG label was `[78]=archetype` — every archetype has `[78]=3`; `[64]=mana` is
  CONFIRMED.)

## Inventory/bag carve-out (moved from Opcodes.cs 2026-07-06)

RE notes on the FullPlayerCharacterIIR (0x737A6DF9) inventory/bag serialization, formerly a comment block in the `IirTypes` enum (then named `GameServer0X2000RespondsOpcodes`):

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
  (mostly count=1); this tentatively matches our own Bag/Resources/Quest container split
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
u32 pairs** — the (questId, sequence) pairs our `QuestIndexRegion.Block.AreaQuestIds` models (see `QuestIndexRegionCodec`).

### The area 4CC is SERVER-authored

`FUN_00922210` **reads the 4CC off the stream and stores it**; nothing in the client looks one up by
area id. So there is no client-side area catalog, and the value cannot be recovered from the client
at all - not from the RDB, not from a table. Only captures carry it. (The RDB search was doomed for
this reason, not merely because records are compressed.)

Practical consequence: an area's 4CC becomes available only once a capture shows that area in a
completed-quest journal. See `QuestLine.Magic` and Gitea #389/#390.
