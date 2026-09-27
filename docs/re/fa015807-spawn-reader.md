# FA015807 NPC-spawn reader — `SimpleCharFullUpdateIIR_t::ReadSubClass`

RE of the FA015807 (SimpleCharFullUpdate) spawn body: the pre-IIR scaffold, the embedded CoreCharacter, and the
post-IIR stream. `NpcSpawnBuilder` builds the whole body from typed DB columns (`BuildMessageFromColumns`,
`BuildLive`, `BuildBody`); there is no captured template and no verbatim-burst path. See
`docs/subsystems/npc-three-tier-architecture.md` for the row → live entity → wire model.

Decompiled from `AgeOfConan.exe` (Ghidra) — reproduce with `tools/ghidra/DumpSpawnIir.java` + `DumpFns.java`.
Grammar validated against 4558 captured FA015807 records (zero framing failures) by `SpawnBodyReader`.

## Reader primitives (`DumpFns.java`)

| Decompiled fn | Meaning |
|---|---|
| `FUN_00cba1b0(dst, n)` | the raw blob read of `n` bytes (the stream cursor) |
| `FUN_0048f900` → `U32` | `ReadUInt32` — reads 4 bytes, byte-swaps to big-endian when the stream's `+0x1a` endian flag is set |
| `FUN_004e0390` → `U8` | `ReadByte` — reads 1 byte |
| `FUN_01122b10` | a **count-framed sub-list**: first `U32 = (count + 1) * 0x3F1`; then `count × { U32 + sub-object }` |
| `FUN_0145ac80` | the embedded **`CoreCharacterFullUpdateIIR::ReadSubClass`** (identity/appearance/stats) — its own `u8` version `'A'` = `0x41` |

The `0x3F1` (1009) multiplier on the sub-list count is a wire **framing invariant** — the first word is
always a clean multiple of 1009, which the client asserts (`uVar2 % 0x3f1 == 0`). Useful as a parser anchor.

## On-wire record layout

```
[u32 type] [u32 handle] [u32 0]                         # record header (type 0xC350 char class, live handle)
[varint tlen] [ProtoGameCodeDynelIIR transform]         # base Read: pos Vec3 + rot Quat + playfield_instance
# ---- SimpleCharFullUpdateIIR_t::ReadSubClass (FUN_01121c00) starts here ----
[u8 version = 0x05]                                      # class version; client rejects != 5
[u8] [17×u32] [3×u8] [4×u32] [2×u8] [5×u32]              # the pre-IIR scaffold (fixed read sequence)
[SUBLIST @1122b10: u32=(count+1)*0x3F1, count×{u32+obj}] # per-spawn sub-list
[27×u32]                                                 # post-sublist scaffold
[CoreCharacterFullUpdateIIR @145ac80: u8 ver=0x41, …]    # embedded identity/appearance/generic_stats
```

The read **sequence** (not the struct-slot order) is the wire order; recovered by walking the decompiled
call order in `FUN_01121c00`. Totals: **53 × U32 + 6 × U8** in the scaffold, around the sub-list and before
the embedded CoreCharacter. There are no float reads and no string/blob reads in the sequence; `FUN_0048f900` is
a plain 4-byte read with a conditional byteswap and `FUN_004e0390` a plain 1-byte read. With an empty sub-list the
block is **fixed-width: 222 bytes** (version+1 → CoreChar), and the framing walk lands on `0x41` at +223.

The sub-list is `count==0` on 11,394 of the 11,400 captured NPC spawns; the six that carry one entry (all one NPC,
Viriden) use the same entry shape a player's frame carries. `SpawnPreFrameSublistCodec` reads it for both. The
scaffold's start is walked to through the body's own header rather than computed as "223 bytes before the
CoreCharacter", which would assume an empty sub-list (`SpawnPreIirSublistTests`).

### Embedded `CoreCharacterFullUpdateIIR::ReadSubClass` (`FUN_0145ac80`)
- `u8 version == 'A'` (0x41) — rejected otherwise (`g_nCoreVersion == nVersion`).
- `U32` (→ +0x5c) then `FUN_00cbfb80`.
- a `U32 count` + `count × U32` list (→ the +0x140 vector).
- sub-reads `FUN_0145b8a0` / `FUN_010087b0` / `FUN_0145ba10`.
- two `U32 count` + `count × (U32 key, U32 value)` lists — the **generic_stats** (the client asserts
  `g_Int32StatsOrder.size() == m_CoreCharacterMessage.generic_stats_size()`).

## Validation

`SpawnBodyReader` walks the fixed scaffold for every captured FA015807 record and checks three predictions:
leading version `0x05`, the sub-list's first word is a multiple of `0x3F1`, and the embedded CoreCharacter version
`0x41` lands at the exact predicted offset. Across all login + create captures:

```
FA015807 records: 4558   version==0x05: 4558/4558   framing failures: 0
CoreChar ver==0x41 (count==0 records): 4444/4444
```

`SpawnPreIir` + `SpawnPreIirCodec` (`Faolan.Core.Network/Codecs/Spawn/`) decode the scaffold into a typed struct
(`Version`, 6 `Flags`, 53 `Words`, the typed `Sublist`) and re-encode it byte-exact (`SpawnPreIirTests`).

## The pre-IIR scaffold is a stat block — every slot carries a stat id

**`SimpleCharFullUpdateIIR_t::Apply` (`FUN_011215e0`) pushes every slot into the dynel as `SetStat(statId,
value)` — an explicit id per slot** `[V1: tools/ghidra/DumpScaffoldConsumers.java]`.

To find the consumer, scan every instruction for references to the 53 struct offsets and keep only functions
touching **≥5 distinct** ones — a shape no unrelated class shares. The reader `FUN_01121c00` comes back with all 53,
which is the control; `FUN_011215e0` sits beside it in the same translation unit and starts with a `dynamic_cast`
of its argument to `SimpleChar_t`.

`SpawnPreIir.WordStatIds` and `SpawnPreIir.FlagStatIds` carry the mapping. What it says the block holds is the
character's **PvP / minigame career**:

| slot | struct | stat | LDB name |
|---|---|---|---|
| flag 1 | `+0x1dc` | `0x01F` | Archetype |
| flag 2 | `+0x1dd` | `0x043` | **Class** |
| flag 3 | `+0x1de` | `0x12E` | Prestige Class |
| word 0 | `+0x184` | `0x564` | Fatalities |
| word 2 / 4 | `+0x18c` / `+0x194` | `0x29A` / `0x29B` | Annihilation Wins / Losses |
| word 27 / 28 | `+0x1a4` / `+0x1ac` | `0x29C` / `0x29D` | Capture the Skull Wins / Losses |
| word 31 / 32 | `+0x1cc` / `+0x1d4` | `0x29E` / `0x29F` | Siege Wins / Losses |
| word 6 | `+0x19c` | `0x560` | Minigames Abandoned |
| word 13 | `+0x1e0` | `0x0A5` | Shield Allocation |
| word 34 | `+0x1e4` | `0x090` | Soul Fragments |

47 of the 53 words and 5 of the 6 flags get a stat id; the six trailing words (`+0x24c`..`+0x264`) are the
sublist's own state, not stats. The ids sit next to `PvPTotalKills` (`0x290`) and `PvPTotalDeaths` (`0x292`), and
most of the `0x545..0x593` run is one long block of the same family (named in
[aoc-client-stat-names.md](aoc-client-stat-names.md)).

**Two independent corroborations:** the class byte lands on stat `0x043`, the id recovered from the in-client
character sheet, and `+0x1e0` lands on `0x0A5` = `CharacterStatId.ShieldAllocation`. `SpawnPreIirTests` gates both.

An NPC has no PvP career and a fresh character has no career yet, which is why the block is zero on a fresh spawn.

### An NPC's block is unwritten server memory

The apply writes the whole block inside `if (virtual_predicate_0x60(dynel) == 0)` and applies three stats
unconditionally outside it (`+0x1e0` `0x0A5`, `+0x1e4` `0x090`, `+0x208` `0x57F`). The block is a stat block on
every spawn, and what an NPC carries in it is server data unrelated to that NPC. Three independent lines say so.

**1. Slot `0x60` is not a player-vs-NPC predicate.** Resolving `SimpleChar_t`'s primary vtable (`0203e93c`) with
`tools/ghidra/FindVtableForClass.java` and reading slot 24 gives `FUN_006ea600`, a one-line getter:
`return this->u8 @ +0x1e8`. Sweeping the slot across every RTTI-discoverable vtable in the image
(`tools/ghidra/DumpVirtualSlotAcrossClasses.java`, 714 vtables, 62 distinct targets) shows the dynel family
splits two ways and neither way is player-vs-NPC:

| classes | slot `0x60` |
|---|---|
| `n3Dynel`, `n3VisualDynel`, `SimpleDynel`, `GameCodeDynel`, `ClimbingDynel` | `FUN_00594fe0` — `return 1` |
| `CoreCharacter`, `SimpleChar_t` | `FUN_006ea600` — `return this->u8 @ +0x1e8` |

So the guard means *"this is a character"*. The flag is written once, in `CoreCharacter::CoreCharacter`
(`FUN_006eace0`, `MOV byte ptr [ESI + 0x1e8],AL` @`006ead90`), as `flag = (arg == 0)`. The only construction path
from this message is `SimpleCharFullUpdateIIR_t::CreateDynel` (vtable slot 10, `FUN_01121570`), which branches on
`TEST byte ptr [ESI + 0x5c],0x1` and passes `1` when the bit is clear, `0` when set — and the base IIR ctor
(`FUN_014582b0`, `MOV dword ptr [EBX + 0x5c],EDI` with `EDI == 0`) leaves it **clear**. Clear → arg `1` →
flag `0` → predicate `0` → **the stat block is applied**, for players and NPCs alike.

**2. An NPC's block is not per-type.** Grouping every captured NPC spawn by name: of the 705 names sighted more than
once, **700 carry a varying block**. 591 captured Pillagers carry **551 distinct** 53-word blocks.
Gated by `SpawnPreIirCorpusTests.AnNpcsScaffold_IsPerSighting_NotPerType`.

**3. The content belongs to unrelated server subsystems.** Four Pillagers in a *single* capture carry a
teleport-script error, a melee-library trace, an LDB `<localized>` fragment complete with a decryption key for a
**guild-name** parameter, and unstructured binary. A player's block, by contrast, is numeric and stable — the
same character in the same session carries identical words, and **not one of the 317 captured player frames
contains a printable run** while 5438 of 17400 NPC frames do
(`SpawnPreIirCorpusTests.OnlyNpcScaffolds_CarryLeakedServerText`). Not every NPC block is text: the
`SpawnNpcCrocodile` and `SpawnNpcCasilda` goldens carry small scalars, floats (e.g. `0x41fadfbc`≈31.4 @0x238) and
consecutive pointer-like values (`0x82a367d0`/`0x82a365b0`/`0x82a36800`).

**How much the server does write: exactly 3 of the 53 words.** Over the 1171 frames visibly carrying leaked
text, ask per word whether it is ever *string interior* (all four bytes printable — a zero-padded small integer
like `111` is `'o'` plus nulls and must not count). Every slot lands at 70–90% **except `w17`, `w18` and `w38`,
which land at 0.0%**: the leak never reaches them because the server fills them. Gated by
`SpawnPreIirCorpusTests.OnlyThreeScaffoldSlots_AreWrittenOnAnNpcSpawn`. `w17` (struct `0x1fc`) is a 5-state enum
`{3,0x6f,0x78,0xc9,0x12c}`, modal `0x6f`, not identity-bound.

That is the shape `SpawnPreIirs.Minimal()` emits — zeros everywhere but the category word `w17` — and it is
byte-exact to a real captured minimal NPC. Reproducing a captured NPC's block would be reproducing retail's leak.

Not established: the allocation mechanism. "Uninitialised heap" and "a shared server-side scratch buffer that is
legitimately reused" both fit the captures. The claim that holds is the narrower one: *the other 50 words are not
written for that NPC and carry unrelated server-side data*.

### Reading the leaked text

The bytes are contiguous on the wire but the client scatters consecutive 4-byte chunks into **non-contiguous**
struct fields (`0x184, 0x188, … 0x1a8, 0x1b0, …`), so reading them in wire order gives scrambled text.
Reassembling the 53 u32s **in struct-offset order** and taking each word **little-endian** (the stream's endian flag
is set, so `ReadU32` byteswaps — i.e. the wire is LE) recovers contiguous text (`SpawnPreIir.DecodeScriptBuffer`):

```
ScriptFormula defined at ScriptCommands/ShowFIFOMessageFromServerScriptCommand.cpp(19) in ScriptGroup
'PvPMonthlyFestivalArenaOfDeath_Solo' (id=5000419) Script 'eventcomplete' (id=6) Command
'showfifomessagefromserver' … <font color="green"> … Command 'if' (id=54)
```

What the text reveals is about the retail server, not the NPC carrying it; that harvest is
[retail-server-heap-leak.md](retail-server-heap-leak.md), shipped as `--report scaffold-heap`.

## The post-IIR: NPC and player share one grammar

`SpawnPostIirCodec` (composed inside `SimpleCharFullUpdateCodec`) reads the NPC and the player post-IIR with one
grammar:

```
[u32 headCount][headCount x u32]        the plain-counted head word list
[u32 firstRecordLen][u32 movementMode]
[firstRecordLen bytes]                  the first record, ENDING in the buff list's framing word
[the entry slot]                        appearance table + skills + pets
[u32 count] pairs, [u32 count] pairs    two plain-counted pair lists
[u32 ListFrame] pairs                   the count-framed third
```

The minimal forms — entry slot and all three pair lists empty — pin the header.
[V3: SpawnPostIirGrammarTests.TheNpcAndPlayerPostIirStreams_AreTheSameGrammar]

- The create-editor preview's **40-byte** stream is the fully-empty player scaffold: `firstRecordLen` 4 (the
  buff list's count word) and every list empty, so `40 = 12 + 4 + 24`.
- The NPC minimal **41-byte** form is the same thing with `firstRecordLen` 5 — one extra leading byte inside
  that first record (the empty move-task slot). That single byte is the whole difference between the two.
- `firstRecordLen` is the length of the first record only. On a player it stays 4 whether the character has 0
  or 3 buffs, because the buff records are that list's elements, not further records.
- **The third header word is the entity's movement mode** (`SpawnPostIir.MovementMode`), the same value the
  embedded `ProtoCoreCharacterIIR` carries in its own `movement_mode` field (21) — one value, sent twice in one
  spawn body, in two unrelated byte regions. Re-derive with `--report field-predictors`
  [V1: Faolan.Importer/Cli/ReportCli.cs].
  - Its values are **walk (1) / run (2) / sprint (3)**, named from the client's own speed switch and the
    `<movementset>` XML it reads; see [`movetask-grammar.md`](movetask-grammar.md) for the derivation.
  - Over **5,739** captured NPC spawns the two copies agree on **5,738** (the one exception is a Rocknose part-way
    through a path plan), and over **209** captured player spawns on all 209.
  - It is rolled per spawn event, not per type: 61.9% stable across templates even when restricted to
    freshly-spawned NPCs with no move task and no buffs. (A scan that stops at the first sighting of each type
    cannot see this.) Of the 562 spawns carrying an active move-task plan, 559 are `1`; a spawn with an empty slot
    is `2` on 4,064 of 5,168; `3` is 14 sightings, every one a Flesheater with an empty slot.
- `12 + firstRecordLen + 24` is the length only when all three pair lists are empty, i.e. the minimal forms.

### The record body

Every captured stream is read to its exact last byte:

```
[SpawnPostIir prologue]       header + first record + the ACTIVE BUFFS
  [u32 headCount][headCount x u32]
  [u32 firstRecordLen][u32 movementMode]
  [firstRecordLen-4 prefix][u32 buff list frame]      <- the record ENDS in the buff count
  [k x 60-byte ScaffoldBuff]
[the ENTRY SLOT]                    NPC: [u32 count][morph entries][u32][u32]
                                    PLAYER: [u32 count][u32 entryTableX][f32 1.0][14-byte entries][ID list]
[SpawnPostIir tail]           [u32 c1][c1 x 8] [u32 c2][c2 x 8] [u32 ListFrame][n x 8]
```

All **17,717** captured spawns (17,400 NPC, 317 player) parse to the exact byte and re-encode byte-exact; the
family is told apart by the first record's move-task slot (an NPC has one, a player does not).
[V3: SimpleCharFullUpdateCorpusTests.EveryCapturedSpawn_ParsesToTheExactByte_AndReEncodes]
Of the 317 player streams, 31 have an empty entry slot, where the player's `[count][entryTableX][marker]` and the
NPC's `[count][two words]` are the same twelve bytes — the two readings agree about everything.

**The buff count is read, not searched.** The last four bytes of the first record are the buff list's framing word
on every captured stream, without exception. This is invisible from the player side, where the first record is that
word (length 4, no prefix); an NPC carries a move-task slot in front of it — one `0x00` byte fresh, a whole
`MoveTaskNode` mid-session. Searching for the record count instead leaves a handful of streams unread and needs a
"does this look like a buff?" guard to stop the search inventing records out of runs of zero bytes.
Reading it also handles a stream whose ASCII is scrambled in 4-byte groups (`uoY era ehtsohCuG`) — a
mis-reassembled TCP segment, harmless because the corruption is inside a string rather than in the framing.

**The 60-byte records are the player scaffold's buff records**, not an NPC-only shape. All 941 carry the same
`Kind` and `EntityClass.Character` as their target class, their timer pair orders correctly in every one, and
the nine that appear on NPCs all resolve in our own catalog: *Stagger*, *Hearty Meal*, *Shrink*, *Ether Channel*,
*Runed Flesh (Rank 1/5)*, *Undead Minion: Mutilator*, *Deathless Acolyte: Arcanist*, *Frozen Hatred*. Two of
those are necromancer pet summons and one is the Frozen Hatred proc.

Their `CasterLevel` runs **0..82** (5..82 on the NPC records alone), and 82 is above the level cap, so the corpus
does not prove the field is the caster's level — the gate pins the observed range rather than asserting 1..80.

Two boundary traps: the entry table ends **20** bytes from the stream end, not 24 (the word at −24 belongs to the
table's own region), and the terminator's three pair lists are **counted, not constant** — requiring the last 24
bytes to be a fixed pattern rejects every stream whose lists are populated.

### The first record's slot is a MoveTask

```
[u8 present]                  0 on a fresh NPC, 1 when a task follows; absent entirely on a player
[MoveTaskNode]                the same recursive node the MoveTaskIIR packet carries
```

Of the 17,400 NPC slots, 14,153 are empty and **3,247** carry a task; every one is consumed to its exact last
byte by `MoveTaskNode.Read` and re-encodes byte-exact. The modes are `MoveTaskCodec`'s own:

| mode | | streams |
|---|---|---|
| `0x04` | ListMoveTask — a waypoint path | 3,170 |
| `0x07` | approach/facing | 32 |
| `0x08` | approach target | 33 |
| `0x01` | PathMoveTask | 11 |
| `0x02` | stationary from/to/facing | 1 |

[V3: SimpleCharFullUpdateCorpusTests.EveryFirstRecordSlot_IsAMoveTask]

The pre-IIR frame is the player's stat block, the entry slot is the morph table, and this slot is a MoveTask —
**before decoding an FA015807 region, look for the codec that already reads it.**

### Appearance lives in the IIR and the morph table, not in the rest of the post-IIR

Appearance is in the IIR (`CharacterStats.head_mesh` / `looks_package`) plus, on a humanoid, the entry slot's
**morph table**. 19 humanoid NPC types (Kalanthes, Guard, Nobleman, Black Dragon Guard, Poacher, Bat Pict Mauler,
…) emit the 41-byte minimal post-IIR themselves in some captures, so a humanoid with no morph rows renders
correctly with the minimal form; one with morph rows needs the table, and the minimal form drops it (the NPC
renders bald). A stale playfield token, by contrast, makes the client drop the spawn entirely —
`SpawnTransformPatch` stamps the live token.

**Locate the morph table by reading the stream, not by offset.** It sits at `12 + firstRecordLen` only when the
character carries no active buffs; each buff pushes it 60 bytes further. Reading the stream lifts 269 more
captured bodies than the offset arithmetic and loses none — in the DB, +258 morph rows across 7 spawns (Red Hand
City Guards in Tortage, map 4020).
[V3: NpcSpawnAppearanceTests.DecodeAppearanceLenient_WithAnActiveBuff_StillRecoversTheTable]

## Construction

A fresh NPC spawn is built from the DB:

1. **pre-IIR** — `SpawnPreIirs.Minimal(category = 0x6f)`: version `0x05`, all 6 flags 0, all 53 words 0 except
   `w17` = the category enum.
2. **post-IIR** — `SpawnPostIirs.Npc(movementMode, morphs)`: the minimal form for an animal, the appearance/morph
   table for a humanoid.
3. **message** — `NpcSpawnBuilder.BuildMessageFromColumns` from the type-level stat core (`NpcStatCore`, a 1:1
   side table of proto-mirror columns plus the CoreChar header word `IirHeader`) and per-placement overrides on
   `NpcSpawn`.
4. **body** — `NpcSpawnBuilder.BuildBody` / `BuildLive`, driven by `WorldEntity.TryBuildLive`.

`NpcSpawnFromScratchTests` decomposes the `SpawnNpcCrocodile` (animal) and `SpawnNpcCasilda` (humanoid) goldens
into the live path's inputs and checks the rebuilt record parses back with the expected post-IIR.

## Player spawn: the entry table's closing record is the skill list frame

On a fresh player spawn, what reads as a final `[u16 tag][u32 value]` "closer" entry is **per-class**:
**value = archetype × 100** (Soldier 100 / Priest 200 / Rogue 300 / Mage 400), **tag = (fresh feat/skill list
length + 2) × 0x3F1** (`tools/capture/parse_spawn_scaffold.py`, 117 player self-spawns, zero violations). It is
not a record: the "tag" is the skill list's `ListFrame` word and the "value" the first skill's id (the archetype
ability), read 8 bytes off. That is why stamping one class's on another crashes the client's CoreCharacter
construction on relog (a Necromancer carrying the Barbarian `0x56B6/0x12C` crashes the loading screen; its own
`0x4301/0x190` loads): the client's skill parse validates the frame word. See `docs/re/player-spawn-generation.md`;
the writer derives both structurally (`ListFrame.ForCount(Skills.Count)` / `ArchetypeSkillId`).
