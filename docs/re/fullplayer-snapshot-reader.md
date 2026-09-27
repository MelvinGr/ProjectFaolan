# FullPlayerCharacterIIR (0x737A6DF9) wire-reader — the snapshot "tails" grammar

Recovered from `AgeOfConan.exe` via the vtable-xref method (`tools/ghidra/DumpFullCharReader.java` +
`DumpFullCharRecords.java`, slot 8 = `ReadSubClass`; both write to `$FAOLAN_GHIDRA_OUT`).

`SnapshotTails.FromScratch` builds the whole tails from the character's DB state — the quest portion, the
game-static access-window catalog and the stat skeleton — and is what world entry (`WorldEntryService`) sends. The
captured `BeachTailsTemplate.bin` survives only as the golden test oracle: `SnapshotTailsFromScratchTests`
reproduces it byte-for-byte. `FullCharSnapshotSubClassCodec.Parse` reads every captured subclass sequentially to
its last byte with no opaque region (`SnapshotOpaqueSurfaceTests`). The protobuf root in front of the tails and the
container entries are in [spawn-dissection.md](spawn-dissection.md) and
[player-spawn-generation.md](player-spawn-generation.md).

## Class id → reader

`0x737A6DF9` == BKDR-31(`FullPlayerCharacterIIR`) (`docs/re/aoc-opcode-ids.md`).

| class | vtable | `ReadSubClass` (slot 8) |
|---|---|---|
| `FullPlayerCharacterIIR` | `0208c6b4` | `FUN_0148c210` |
| `CoreFullPlayerCharacterIIR` | `01fc90bc` (ctor `FUN_00796500`) | `FUN_00797440` |

`FUN_0148c210` calls the base `FUN_00797440` first, then reads its own trailing list. The IIR ids are BKDR-31
hashes computed at runtime, so `0x737A6DF9` never appears as a constant; anchor on the class-name string's xrefs
(the ctor passes `"FullPlayerCharacterIIR"` to the IIR registrar) or on a crash stack (`0x007975A6` falls inside
`FUN_00797440`).

```c
version = ReadU32();                 // FUN_0048f900
if (version == 0x18) {               // == QuestcRegionCodec.Version
    FUN_0091f5c0(stream);            // the quest journal
    FUN_00798c70(stream, this+0x20);
    FUN_004d4c70(stream, *(this+0x40));
    count = ReadU32();
    while (count--) {                // one MainQuest_c (0x54 bytes) per quest line
        ...
        FUN_00922210(quest);
    }
    FUN_0077d220(this+0x44);
    FUN_0077d5b0(this+0x54);
    FUN_004d5900(stream);
    FUN_0139d450(stream, *(this+0x74));   // StatList_t::read
    FUN_0139d450(stream, *(this+0x78));   // StatList_t::read
    ...
}
```

**Two stat lists sit directly after the quest loop.** That is why every quest-region desync surfaces as
`statlist.cpp (134) (nSize%1009)==0 && nSize>0` — the stat list is the first strict length check downstream, not
the thing that is wrong.

## Stream primitives

- `FUN_0048f900` = **ReadU32** (big-endian; byte-swaps when the stream's BE flag `+0x1a` is set — same as
  every other IIR reader). All counts/ids below are ReadU32.
- `FUN_0048f840` = **ReadString** — `[u16 len][len bytes]` into an MSVC `std::string`. The `<remoteformat>` XML
  tokens are read here.
- `FUN_0048f720` = **ReadU64**.
- `FUN_00cc0540` = **Read4×U32** — a fixed 16-byte record.
- `FUN_00cba1b0` = the raw `memcpy`-from-stream primitive the inline ReadU32 expansions call.

## The `0x3F1` list-framing invariant

Most lists are framed by a count word `c` where **`c % 0x3F1 == 0` and `c > 0`**, and the **record count is
`c / 0x3F1 - 1`** (`0x3F1` = 1009). The client asserts `"(nSize%1009)==0 && nSize>0"` on violation. Some lists
instead use a **plain** count (noted below).

## Top-level grammar (in wire order)

`CoreFullPlayerCharacterIIR::ReadSubClass` (`FUN_00797440`):

1. `u32 version` — must == `0x18` (else `"...Wrong IIR_t version"`).
2. **The active quest journal** — `FUN_0091f5c0`: `0x3F1`-framed; per quest a `u32 listKey` (= questId), a
   **`Quest_c`** record (0x120-byte object, ctor `FUN_0091cf10`, read by `FUN_0091df50`) and that quest's own `@609`
   objective region, interleaved per quest. The per-record grammar, the objective region and every named field are
   in [questc-region-decode.md](questc-region-decode.md). The five header strings are the journal's readable text.
3. **The completed-quest journal** — `FUN_00798c70` → `FUN_00922c90`: `0x3F1`-framed; records of 112 bytes
   (stride 0x70): `[u32 areaId][string ×3][ACGHash][u32 trailerFlag][u32 innerCount][innerCount × { u32 a, u32 b,
   0x3F1-framed string list }]` (`FUN_005d7190` is the ACGHash reader, `FUN_004d55b0` the string list,
   `FUN_0079b760`/`FUN_00922eb0` the inner reads). A character with no completed quests carries a bare `0x3F1`
   (`ListFrame.ForCount(0)`), which always precedes the index; with completed quests the frame is
   `(areas+1)*0x3F1`.
4. **The held quest ids** — `FUN_004d4c70`: `0x3F1`-framed list of `u32` quest ids (the quest index's header,
   `[u32 (count+1)*0x3F1][u32 questId × count]`).
5. **The quest index: one `MainQuest_c` per held quest line** — plain `u32 count`, then per block
   (`FUN_00922210`):

   ```
   [u32 areaId @0x14] [u32 ACGHash @0x18] [u32 stepNumber @0x30] [u32 stepsAfter @0x34] [u32 tf3 @0x44]
   [u32 n] [ (questId, sequence) × n ]     # the area's full quest catalog
   [u32 trailerQuestId] [u32 a @0x48] [u32 b @0x4c] [u32 c @0x50]
   ```

   All numeric, no strings (`QuestIndexRegion.Block`, `QuestIndexRegionCodec`):
   - the `(questId, sequence)` sequence is 1-based and **not strictly monotonic** (branch/chapter quests repeat
     one);
   - `trailerQuestId` is the **area's anchor quest** (catalog sequence 1), matched into the pair list and parked in
     the current-quest slot `@0x38` — not the held quest (beach: held 12371 at sequence 5, trailer 10238 at 1);
   - **`@0x30`/`@0x34` are per-character progress, not line data**: on all 2,626 captured blocks `@0x30` is the
     held quest's own sequence in the pair list and `@0x34` is `n - @0x30`. Their reader is the journal's line
     panel `FUN_01617260`, which fetches the line through `CoreQuestHolder::GetMainQuest` (`FUN_008107d0`, a scan
     of the holder's `+0x4c..+0x50` list on `+0x14`), takes `@0x30` as a running counter through the Roman-numeral
     formatter `FUN_010cfbc0` (the I/II/III the journal shows for a line's chapters), then draws `@0x34`
     placeholder rows for the steps to come and hides that section (`FUN_00ae3b30(widget+0x4e0, 0, 1)`) when
     `@0x34` is 0;
   - **`@0x44`/`@0x48`/`@0x50` are the same slots the incremental `MainQuestUpdateIIR`'s Unknown1/Unknown0/Unknown2
     land in** (`FUN_00810840` + the ctor `FUN_00921980`), so the snapshot block and the IIR trailer are two
     producers of one struct. `@0x48` is the quest tracker's sort tiebreaker (`FUN_012ce1c0` parks it in
     `QJQuestNode_c +0x70`; `FUN_012cde80`/`FUN_012cbbb0` compare it) — per line it is 1 on solo, 4 on raid, 8 on
     group-dungeon content across all 97 captured blocks. `@0x44` and `@0x50` have no reader (neither direct
     `GetMainQuest` caller touches them, and the class's 7 vtable slots are `PtrTarget_c` boilerplate);
   - **`@0x50` is also the completed-journal record's `trailerFlag`** (`FUN_00922c90` stores it at `+0x6c` of the
     0x70-byte record, after the strings at `+0x04`/`+0x20`/`+0x3c` and the hash at `+0x68`), and the area block
     `QuestJournalIIR_c::ReadSubClass` (`FUN_0091a4e0`) reads through the same `FUN_00922c90` — three producers,
     one field (`TrailerFlag`). It is stored and read nowhere: after the reader the only code touching it is the
     MergeType-1 stamp in `JournalHolder_c::UpdateQuestEntryList`; the journal window band (243 functions) and the
     QJ node / tracker band (604) never read it (the one `+0x6c` byte test there, `FUN_01618640`, is on a tree
     node, not the record); and `MainQuest_c::WriteToStream` (`FUN_00921db0`) has no caller and sits in no vtable.
     Its meaning is the server's. Its carriers: the Kalanthes raid chain, a World Bosses mastery line, the 2014
     anniversary and 2015 Halloween event lines, two Onslaught lines.

   **The area's ACGHash is server-authored.** `FUN_00922210` reads it off the stream and stores it; nothing in the
   client looks one up by area id, so it cannot be recovered from the client or the RDB — only captures carry it
   (`QuestLine.Magic`).
6. **List D — the playfield-access table** (`PFAccessWindow_`). `FUN_0077d220`: `0x3F1`-framed; record =
   `[u32 key]` (read by the outer loop) + `FUN_0077db70`:

   ```
   entry = [u32 playfieldId][u8 requiresToken][u32 accessType][u32 secondsRemaining][u16-str name]
           [u32 childCount] childCount × child
   child = [u8 flag][u32 id][u16-str displayName][u16-str parentName]
   ```

   These are `PlayfieldAccessHolder`'s playfield-access entries (`CoreCharacter\PlayfieldAccessHolder.cpp`, channel
   `GameCode/PlayfieldAccess`), 86 on every captured subclass (`PfAccessWindowEntry`, `AccessWindowCatalog`):
   - `accessType`: the holder's checker (`FUN_0077e200`) looks the playfield up and switches on the stored pair's
     `+0x14` (the insert path `FUN_0077e9a0` pins the pair layout `{key, u8 flag, u32 accessType, u32, …}`): 0 and
     2 pass, 1 and 3 log `"Access DENIED"`, anything else asserts `"Invalid access type %u for playfield %u"`. Its UI
     caller (`FUN_014bd250`) opens the **`playfield_access_window`** off the same check — the client half of the
     instance-entry gate (open work tracked in Gitea #511). The catalog carries 2 on every base-game playfield and 3
     on the Khitai / Dragon's Spine ones, so the "game-static" table is the captured account's entitlement state (it
     did not own those expansions);
   - `requiresToken`: when set, the opener `FUN_014bd250` queries token pool 50 = `TeleportCoins` and refuses with
     LDB-100 `PlayfieldAccessNoTokens` (*"You do not have enough Teleport Coins to travel"*); the window-open flow
     also reads it (node `+0x10`) as the gate for that check;
   - `secondsRemaining`: a timed access — the holder's update slot `FUN_0113a390` warns below
     `TWK_PlayfieldAccess_WarningTimer` = 300 with `EndOfPlayfieldAccessWarning` (*"You will lose access to %s in %d
     minutes"*, dividing by 60).

   The same record travels alone as `DispatchSendPlayfieldAccessData`'s `accessInfo` (that handler calls this very
   reader), and `DispatchPlayfieldAccessGranted(playfieldId, hours, allCharacters)` grants one. A child's two
   strings are empty on all captured children; `childCount` is ≤3 everywhere, so its high bytes read as constants.
7. **List E** — `FUN_0077d5b0`: `0x3F1`-framed; record = `[u32 key]` + `FUN_0077df00`:
   `[string][u32][u32][u32][u8][string]`. Empty on all 151 captured subclasses; the same holder's second list.
8. **List F** — `FUN_004d5900`: `0x3F1`-framed; `N-1` records, each reads 2 `u32` and stores the second. Empty on
   all 151.
9. **`StatList_t` #1** — `FUN_0139d450` @ `+0x74`: `0x3F1`-framed; `N-1` × `StatPair_t` (`FUN_0139d210` =
   `[u32 statId][u32 value]`, asserts `"StatPair_t:reading %d %d"`).
10. **`StatList_t` #2** — `FUN_0139d450` @ `+0x78`: same grammar.
11. **The known-spell list** — present only if `*(this+0xe9) != 0`: `FUN_0077af90`: `0x3F1`-framed; **12-byte**
    wire records (three 4-byte reads; `0x14` is the in-memory stride), asserting `"inconsistent SpellBook_t"`
    (`Spellbook.cpp`) — see below.
12. **Trailing list** — plain `u32 n`, then `n × [u8][u64][u64]` (17 bytes; `FUN_0048f720` reads 8 bytes).

Then `FullPlayerCharacterIIR::ReadSubClass` (`FUN_0148c210`) reads its own plain-counted
`[u32 m][ m × ([u32][u32]) ]`; its ids are RDB template ids (`0xF426A` = 1000042, the feat id-map), so it reads as a
counter table.

Every one of the 151 captured subclasses parses to its exact last byte and re-encodes byte-exact through
`SubClassPostAccessBlock`. Every capture opens identically — `D×0, E×0, StatList#1×17, StatList#2×148` — and what
varies is the spell list. [V3: SubClassPostAccessBlockTests.EveryCapturedPostAccessBlock_ParsesAndReEncodesByteExact]

The spell list is **optional**, not merely sometimes empty: 21 of the 151 end at `StatList#2` and go straight to
the two zero-count tails, and each of those is its capture's first snapshot — the one the create editor is sent.
The other 130 carry the list, empty (its bare `0x3F1` frame) or not; absent and empty are different bytes.
[V3: SubClassPostAccessBlockTests.TheSpellBookList_IsAbsentOnlyInTheSessionsFirstSnapshot]

## Multi-quest layout

Per held quest, its `Quest_c` record is immediately followed by its own `@609` objective region, whose lead word
is `(goals+1)*0x3F1` (beach 2 goals = `0xBD3`, Casilda 1 goal = `0x7E2`). Then the completed-journal frame, then the
index. The index marker is `(questCount + 1) * 0x3F1`, derived from the count. The first block's `areaId` is the
beach area 241 (`0xF1`) on every beach capture, which makes it read as a sentinel there; the block trailer's
`a`/`b`/`c` vary per quest (`a` up to 8, `c` 0 or 1).

**All record/objective scalars are per-quest constants** — byte-identical across a quest's active and completed
login captures; the beach record is the same fresh 601 B early or late. The index block's `stepNumber`/`stepsAfter`
are the exception (per character; `[1][count-1]` only while the character stands on step 1 — the beach line runs
1/3/5/8 across characters).

A held quest joins the snapshot only if `SnapshotTails.IsSnapshotEligible` — the record scalars, the five header
text refs, @609 goals, `ObjectiveRegionVerified`, its area's ACGHash and the line's order; an ineligible quest is
absent from the journal (retail sends no quest packet before the first in-session accept). Builder:
`SnapshotTails.QuestPortion(quests, primary, instance)`; golden `BuildQuestPortionTests` reproduces the 2-quest
region byte-for-byte, and `QuestcGenerationTests.BuildWholeQuestPortion_ReproducesBeachTails` proves the beach
`@0..1292` regenerates with no literals (the index fields are derived from `QuestLine.Magic` and the line's steps
in `JournalSequence` order). Open work tracked in Gitea #389 and #926 (capture coverage).

## The stat lists: self-labelling, doubled, and a baseline

`StatList_t` #1/#2 are `[u32 statId][u32 value]` pairs, and **all 22,348 stat ids across the 151 captures are
defined `CharacterStatId` values**. The names read exactly as a character sheet: `HealthCurrent=8000`,
`HealthMax=8000`, `Intelligence=10`, `StaminaMax=5000`, `PvpMaxExp=3500`. A value is found by id; nothing needs
counting.

The table is **two identical copies** of the same 74-pair run — same ids and same values in both halves, on every
capture. `SubClassPostAccessBlock.HasRepeatedStatTable` detects the doubling, and patching a vital writes both
halves; `SubClassPostAccessBlock` addresses the table by id (`GetStat`/`SetStat` across both lists).
[V3: SubClassPostAccessBlockTests.TheStatTable_IsSelfLabellingAndDoubled]

Dumping the sheet (`SubClassStatBlock.ExtractFromSnapshot`) across **29 distinct archetype characters** (L1 fresh
through L80, every class):

- **The sheet is a fixed baseline skeleton.** The attributes are the unmodified base — `Strength` (0x324) and
  `Intelligence` (0x328) are **10 for every character, L1 to L80** — and ~two-thirds of the slots are 0/constant
  across all 29. The character's real leveled stats arrive via the live `StatIIR` (0xF98E10B3) and vitals
  (0x96B8DC59) updates. `SubClassStatBlock.CreateFreshBlock` is the skeleton.
- **The slots that vary per character**: `PlayfieldToken` (session), the vitals (HP/mana/stamina, by class+level,
  written by `SubClassPostAccessBlock.ApplyClassVitals` from `CharacterMaxStats.SnapshotVitals()`), XP/level, the
  PvP counters, and ~10 unnamed.
- `0x0039` = **`ExpCurrentLevelBase`** — the total XP at which the current level began, so the XP bar fills
  `(Exp - base) / (MaxExp - base)`: `base <= Exp <= MaxExp` everywhere, `base == Exp` for a freshly-dinged
  character, and **level 24's base (263640) is exactly level 23's `MaxExp`**.
- `0x044A` → **`Timestamp`** — a unix epoch (values decode to real 2010 dates; the same value recurs across
  different characters, one character varies across captures) — a shared session/server time.
- `0x025F` — a per-character constant (byte-identical across NecroL80's 42 captures), only 3 distinct values
  corpus-wide; not race (two Stygian necros differ). Unnamed.
- `0x0398/9A/9B/9D` — four signed values, all four equal per character, base -300, scaling with level (Hoxxling's
  quad tracks her `ExpCurrentLevelBase` across captures). Unnamed.

The remaining per-character unknowns track level/class but map to no independently-observable quantity; naming
them needs in-client value probing.

The class label is in the front protobuf root (`ProtoFullCharacterIIR`), not the tails.

## The known-spell list

The list that carries a character's **learned spells** (and the feats/abilities the action bar binds to) is
region 11, a `0x3F1`-framed list of **12-byte records**:

```
[u32 id  big-endian] [u32 0xFFFFFFFF] [u8 0x00 ×3] [u8 type]      # type 2 = spell, 5 = feat/ability
```

A developed character carries the class's whole available set (NecroL80 = 108 records); a fresh one carries only
its learned spells (Faolanx necro = 2 records = its 2 create-spells `0x42CA77`/`0x125D9B`, count `0x0BD3`). An empty
list here makes seeded spells vanish on relog and type-4 attacks not place on the bar. `KnownSpellListCodec` models
it, and `SnapshotTails.FromScratch` fills it from the character's `CharacterSpells`
(`KnownSpellLists.FromSpellIds`).

## Inventory

No server→client inventory packet follows a `MoveItemInInventory` request; the client learns its bag state from
this snapshot's containers (101 Equipped, 104 Bag, 112 Resource, 113 Quest), and later changes arrive through
the dedicated item messages. See [spawn-dissection.md](spawn-dissection.md) for the container entry grammar.
