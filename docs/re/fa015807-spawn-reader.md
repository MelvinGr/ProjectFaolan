# FA015807 NPC-spawn reader — `SimpleCharFullUpdateIIR_t::ReadSubClass` (Gitea #185 / #54)

RE of the **NPC-spawn pre-IIR scaffold** — the "last un-lifted part" of the from-DB NPC spawn. `NpcSpawnBuilder`
builds the FA015807 (SimpleCharFullUpdate) body byte-exact from the row, re-addressing the live handle/position +
token; it is now the SOLE NPC-spawn path (every FA015807 is dropped from the login burst and interest-spawned, so
there is no verbatim-burst fallback anymore — #185).

> **RESOLVED (2026-06-17): the scaffolds are RUNTIME state, not config — generate the minimal form from scratch.**
> A cross-capture sweep (`tools/capture/classify_fa015807_fields.py` for the pre-IIR, `NpcSpawnAppearanceTests` for
> the post-IIR; **5026 / 5151 scaffolds across all 45 captures**) overturned the "per-NPC RDB-config" theory below.
> The scaffolds carry **no per-NPC config and no session-volatile reference**; **every** non-zero word is
> per-instance runtime state (target/owner handles, timers, action counters) that is **0 on an idle fresh spawn**.
> Per-NPC verdict for Crocodile / Citizen / Nemedian / Black Dragon Guard: **0 words** const-per-type, **0**
> const-per-session — all per-instance. The most-minimal real captured NPCs carry exactly **one** non-zero pre-IIR
> word (`word17`@0x1fc, a 5-state enum `{3,0x6f,0x78,0xc9,0x12c}`, modal `0x6f`, NOT identity-bound) + the
> recordCount==1 minimal post-IIR. So the from-scratch fresh scaffold is a fixed constant, needs **no RDB
> extraction, no DB columns, no re-addressing** — see "Resolution" at the bottom. The earlier sections are kept
> for the wire-grammar RE (still correct) but their *construction* conclusions are superseded.

Decompiled from `AgeOfConan.exe` (Ghidra) — reproduce with `tools/ghidra/DumpSpawnIir.java` +
`DumpFns.java` (both committed). Grammar **validated against 4558 captured FA015807 records** (zero
framing failures) by `SpawnBodyReader`.

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
[SUBLIST @1122b10: u32=(count+1)*0x3F1, count×{u32+obj}] # per-spawn sub-list (count==0 on every beach NPC)
[27×u32]                                                 # post-sublist scaffold
[CoreCharacterFullUpdateIIR @145ac80: u8 ver=0x41, …]    # embedded identity/appearance/generic_stats
```

The read **sequence** (not the struct-slot order) is the wire order; recovered by walking the decompiled
call order in `FUN_01121c00`. Totals: **53 × U32 + 6 × U8** in the scaffold, around the sub-list and before
the embedded CoreCharacter.

### Embedded `CoreCharacterFullUpdateIIR::ReadSubClass` (`FUN_0145ac80`)
- `u8 version == 'A'` (0x41) — rejected otherwise (`g_nCoreVersion == nVersion`).
- `U32` (→ +0x5c) then `FUN_00cbfb80`.
- a `U32 count` + `count × U32` list (→ the +0x140 vector).
- sub-reads `FUN_0145b8a0` / `FUN_010087b0` / `FUN_0145ba10`.
- two `U32 count` + `count × (U32 key, U32 value)` lists — the **generic_stats** (the client asserts
  `g_Int32StatsOrder.size() == m_CoreCharacterMessage.generic_stats_size()`).

## Validation

`python3 `SpawnBodyReader`` walks the fixed scaffold for every captured FA015807 record and
checks three predictions: leading version `0x05`, the sub-list's first word is a multiple of `0x3F1`, and the
embedded CoreCharacter version `0x41` lands at the exact predicted offset. Across all login + create captures:

```
FA015807 records: 4558   version==0x05: 4558/4558   framing failures: 0
CoreChar ver==0x41 (count==0 records): 4444/4444
```

## The scaffold is a STAT BLOCK — every slot carries a stat id (2026-08-03, #603)

The scaffold had been filed as opaque per-instance runtime state: reproducible, zero on a fresh spawn, and
otherwise unattributed. It is not opaque. **`SimpleCharFullUpdateIIR_t::Apply` (`FUN_011215e0`) pushes every
slot into the dynel as `SetStat(statId, value)` — an explicit id per slot** `[V1:
tools/ghidra/DumpScaffoldConsumers.java]`, so the block has a key, and the key is a stat id space we already
name elsewhere.

Found by scanning every instruction for references to the 53 struct offsets and keeping only functions touching
**≥5 distinct** ones — a shape no unrelated class shares. The reader `FUN_01121c00` comes back with all 53,
which is the control; `FUN_011215e0` sits beside it in the same translation unit and starts with a
`dynamic_cast` of its argument to `SimpleChar_t`.

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
sublist's own state, not stats. The ids sit next to `Kills` (`0x290`) and `Deaths` (`0x292`), and most of the
`0x545..0x593` run is one long unnamed block of the same family.

**Two independent corroborations**, which is what makes a hand-transcribed Ghidra table trustworthy: the class
byte lands on stat `0x043`, the id #239 recovered from the in-client character sheet, and `+0x1e0` lands on
`0x0A5` = `CharacterStatId.ShieldAllocation`, which was named long before this. `SpawnPreIirTests` gates both.

**This also explains why the block is all zeros.** An NPC has no PvP career and a fresh character has no
career yet — so "every word is 0 on an idle fresh spawn" was the right observation with the wrong cause under
it. The from-scratch minimal scaffold stays exactly as it is; what changes is that the zeros are now *known* to
be zeros rather than assumed safe. When minigames/PvP stats are implemented (#146 / #503), this is where they
go out.

### It does NOT conflict with the char-buffer reading — an NPC's block is UNINITIALISED SERVER DATA (2026-08-03, #603 closed)

The section further down concludes the same `0x184..0x264` region is an inline **char buffer** holding an NPC's
RDB script-binding text. The reconciliation first proposed here was a **union discriminated by dynel kind** —
PvP career for a player, script text for an NPC — because the apply writes the whole block inside
`if (virtual_predicate_0x60(dynel) == 0)` and applies only three stats unconditionally outside it (`+0x1e0`
`0x0A5`, `+0x1e4` `0x090`, `+0x208` `0x57F`).

**There is no union.** The block is a stat block on every spawn, and what an NPC carries in it is server data
that has nothing to do with that NPC. Three independent lines say so.

**1. Slot `0x60` is not a kind predicate.** Resolving `SimpleChar_t`'s primary vtable (`0203e93c`) with the new
`tools/ghidra/FindVtableForClass.java` and reading slot 24 gives `FUN_006ea600`, a one-line getter:
`return this->u8 @ +0x1e8`. Sweeping the slot across every RTTI-discoverable vtable in the image
(`tools/ghidra/DumpVirtualSlotAcrossClasses.java`, 714 vtables, 62 distinct targets) shows the dynel family
splits two ways and neither way is player-vs-NPC:

| classes | slot `0x60` |
|---|---|
| `n3Dynel`, `n3VisualDynel`, `SimpleDynel`, `GameCodeDynel`, `ClimbingDynel` | `FUN_00594fe0` — `return 1` |
| `CoreCharacter`, `SimpleChar_t` | `FUN_006ea600` — `return this->u8 @ +0x1e8` |

So the guard means *"this is a character"*, not *"this is a player"*. The flag is written once, in
`CoreCharacter::CoreCharacter` (`FUN_006eace0`, `MOV byte ptr [ESI + 0x1e8],AL` @`006ead90`), as
`flag = (arg == 0)`. The only construction path from this message is
`SimpleCharFullUpdateIIR_t::CreateDynel` (vtable slot 10, `FUN_01121570`), which branches on
`TEST byte ptr [ESI + 0x5c],0x1` and passes `1` when the bit is clear, `0` when set — and the base IIR ctor
(`FUN_014582b0`, `MOV dword ptr [EBX + 0x5c],EDI` with `EDI == 0`) leaves it **clear**. Clear → arg `1` →
flag `0` → predicate `0` → **the stat block is applied**. That is the default, for players and NPCs alike.

**2. An NPC's block is not per-type, so it cannot be that NPC's script binding.** Grouping every captured NPC
spawn by name: of the 705 names sighted more than once, **700 carry a varying block**. 591 captured Pillagers
carry **551 distinct** 53-word blocks. Gated by
`SpawnPreIirCorpusTests.AnNpcsScaffold_IsPerSighting_NotPerType`.

**3. The content belongs to unrelated server subsystems.** Four Pillagers in a *single* capture carry a
teleport-script error, a melee-library trace, an LDB `<localized>` fragment complete with a decryption key for a
**guild-name** parameter, and unstructured binary. A player's block, by contrast, is numeric and stable — the
same character in the same session carries identical words, and **not one of the 317 captured player frames
contains a printable run** while 5438 of 17400 NPC frames do
(`SpawnPreIirCorpusTests.OnlyNpcScaffolds_CarryLeakedServerText`).

**How much the server does write: exactly 3 of the 53 words.** Over the 1171 frames visibly carrying leaked
text, ask per word whether it is ever *string interior* (all four bytes printable — a zero-padded small integer
like `111` is `'o'` plus nulls and must not count). Every slot lands at 70–90% **except `w17`, `w18` and `w38`,
which land at 0.0%**: the leak never reaches them because the server fills them. Gated by
`SpawnPreIirCorpusTests.OnlyThreeScaffoldSlots_AreWrittenOnAnNpcSpawn`.

That is the whole mechanism, and it is exactly the shape `SpawnPreIirs.Minimal()` already emits — zeros
everywhere but the category word `w17`. So the from-scratch scaffold is not merely "safe": reproducing a
captured NPC's block would be reproducing retail's leak.

⚠️ **What is NOT established** is the allocation mechanism. "Uninitialised heap" and "a shared server-side
scratch buffer that is legitimately reused" both fit the captures, and nothing here distinguishes them. The
claim that holds is the narrower one: *the other 50 words are not written for that NPC and carry unrelated
server-side data*.

## Kind-level Ghidra RE — the block is a fixed-width char buffer, read as 53×U32 (2026-06-15, superseded in part — see above)
The original plan was a **column↔offset map**: diff the 53 scaffold U32s across spawn rows, find the few
that vary, feed each from a DB column. That model was wrong, and so was a first guess that the slots were a
mix of `ReadFloat` / string-blob reads. Decompiling the readers themselves settles it
(`tools/ghidra/out/spawn_iir.txt` + `spawn_fns.txt`):

- `FUN_01121c00` (`SimpleCharFullUpdateIIR_t::ReadSubClass`) reads, after the `u8 version==0x05`, exactly
  **53× `FUN_0048f900` + 6× `FUN_004e0390`**, then the sublist, then the embedded CoreCharacter. There are
  **no float reads and no string/blob reads** in the sequence (`grep` of the dump: `53 FUN_0048f900`,
  `6 FUN_004e0390`, the rest is the error-message path).
- `FUN_0048f900` is a **plain `ReadU32`** — reads exactly 4 bytes (`FUN_00cba1b0(&local_8,4)`), then a
  conditional byteswap on the stream's `+0x1a` endian flag. `FUN_004e0390` is a plain 1-byte read. Neither
  is polymorphic.

So the block is genuinely **fixed-width: 222 bytes** (version+1 → CoreChar), which is why the framing walk
lands on `0x41` at +223 across all 4094 records. The ASCII that shows up in the capture
(`fifomessagefromserver`, `<font color=…>` markup, event names) is **the value of u32 slots**, not a special
read: the C++ struct has fixed-width inline **char buffer(s)** over the region `0x184–0x264` that the IIR
reads 4 bytes at a time. `SpawnPostIirTests` carries the exact wire-order→struct-offset map and
classifies each word over a beach burst: the great majority are `ascii/charbuf` (text in some NPCs, zero in
others — 36/36 blocks distinct because the per-NPC text differs), with only a few genuine scalars
(`w21`@0x1fc, the const-zero `w22`@0x200 / `w45`@0x224, the numeric `w50`@0x23c).

## What the char buffer actually contains — decoded (2026-06-15)
The bytes are contiguous **on the wire** but the client scatters consecutive 4-byte chunks into
**non-contiguous** struct fields (`0x184, 0x188, … 0x1a8, 0x1b0, …`), so reading them in wire order gives
scrambled text. Reassembling the 53 u32s **in struct-offset order** and taking each word **little-endian**
(the stream's endian flag is set, so `ReadU32` byteswaps — i.e. the wire is LE) recovers clean, contiguous
text (`analyze_fa015807_scaffold.py --text`):

```
ScriptFormula defined at ScriptCommands/ShowFIFOMessageFromServerScriptCommand.cpp(19) in ScriptGroup
'PvPMonthlyFestivalArenaOfDeath_Solo' (id=5000419) Script 'eventcomplete' (id=6) Command
'showfifomessagefromserver' … <font color="green"> … Command 'if' (id=54)
```

So the dominant content of an NPC's pre-IIR scaffold is **server-side script-engine diagnostic text** —
ScriptGroup / Script / Command names + ids, `<font color=…>` feedback markup and `<localized>` LDB fragments.

⚠️ **The reassembly is right; the attribution in the original version of this paragraph was not.** It read the
text as "the NPC's script binding, keyed per NPC type". It is neither the NPC's nor per-type: 591 captured
Pillagers carry 551 distinct blocks, and one capture's Pillagers carry text from four unrelated server
subsystems. The region is the PvP/minigame stat block above, which the server simply does not fill in for an
NPC — see *"an NPC's block is UNINITIALISED SERVER DATA"*. What the text is good for is what it accidentally
reveals about the retail server, not what it says about the NPC carrying it: that harvest is
[retail-server-heap-leak.md](retail-server-heap-leak.md), shipped as `--report scaffold-heap`.

## The POST-IIR is runtime state, NOT appearance — "missing hair" was the playfield token (corrected 2026-06-17)

An earlier offline pass guessed the larger humanoid post-IIR was a **float-stat / appearance table** and that the
minimal form "drops hair". **The cross-capture sweep (`NpcSpawnAppearanceTests`, 5151 records) disproves it:**
- **Appearance lives in the IIR** (`CharacterStats.head_mesh` / `looks_package`), which the DB build already
  sources — NOT in the post-IIR.
- **19 humanoid NPC types** (Kalanthes, Guard, Nobleman, Black Dragon Guard, Poacher, Bat Pict Mauler, …) emit
  the **41-byte minimal post-IIR themselves** in some captures. A type that renders correctly with the minimal
  post-IIR proves the larger form is not required for its looks.
- The minimal form has just **3 variants differing only in `word2` ∈ {1,2,3}** over an otherwise identical
  body; `word0 == 0` on 5148/5151.
- ⚠️ **"The larger forms are per-instance runtime records the simulation re-derives" was only a third right**
  (corrected 2026-07-28, #475). The buffs are runtime state; the rest of the larger form is the player's own
  grammar, and on a humanoid it carries the **morph table** — which the minimal form drops. So the minimal
  scaffold is correct for a type that has no morph rows and WRONG for one that does; see the bald-guard
  correction below.

So the "missing hair" regression was the **#23 playfield token** (a stale token made the client drop the spawn),
now fixed by `SpawnTransformPatch` — not the post-IIR content. The minimal post-IIR is correct for animals
**and** humanoids. (`[[npc_spawn_minimal_postiir_breaks_humanoids]]` is corrected accordingly.)

## Resolution — from-scratch minimal scaffold, DEFAULT (2026-06-17, #185 done)
The wire grammar was already pinned; the sweep settled the **construction** question (there is nothing to source).
Implemented:
1. **pre-IIR** — `SpawnPreIir.BuildMinimal(category = 0x6f)`: version `0x05`, all 6 flags 0, all 53 words 0
   except `word17` = the modal category enum. Byte-exact to a real captured minimal NPC.
2. **post-IIR** — the existing `SpawnPostIirs.BuildMinimalPostIir()` (recordCount==1) is a captured-valid form.
3. **lift** — `SpawnPostIirs.WithMinimalScaffolds(body)` rewrites a captured/cross-session body to the fresh
   form (transform + IIR identity kept, both scaffolds reset), applied in `NpcSpawnBuilder.TryBuild` on every
   `freshSpawn` build. **This is now unconditional/default** (the `FAOLAN_MINIMAL_SCAFFOLD` opt-in is gone); the
   byte-exact reproduction goldens are unaffected because they build with `freshSpawn=false`.
4. **golden** — `SpawnNpcMinimalPanther.bin` (Kier-Dheed's Panther, fully minimal in both halves) proves
   `BuildMinimal` (pre) + `BuildMinimalPostIir` (post) + `WithMinimalScaffolds` byte-exact (`SpawnPreIirTests`,
   526 tests green).

**In-client flip DONE (2026-06-17, #185 verified):** logged onto the beach with the minimal scaffolds active —
world-entry and interest-streamed NPCs render the same ("ncs seem the same"), chat connects (the real load
confirmation), zero stream errors. So the minimal scaffold was made the **default** (commit `48be35a`) and the
`FAOLAN_MINIMAL_SCAFFOLD` / `FAOLAN_NPC_VERBATIM` env toggles + the dead `FAOLAN_BUILT_NPC_TEST` injector were
removed — construct-don't-replay is now the only NPC spawn path. The per-spawn sub-list (`@1122b10`) is
`count==0` on 11,394 of the 11,400 captured NPC spawns — but **not everywhere**, as this said: six carry one
entry (all of one NPC, Viriden), and it is the same entry shape a player's frame carries, so its grammar is not
unobserved at all — the player side decoded it in #466. Shared as `SpawnPreFrameSublistCodec` since #475. The same reader primitives + `0x3F1` framing drove the dynel/loot readers
(#187, done) — see `tools/ghidra/DumpIirReaders.java`.

## The NPC post-IIR and the PLAYER post-IIR are the SAME grammar (2026-07-28, #475)

They were modelled as two unrelated formats. `SimpleCharFullUpdateCodec` writes `[u32 4][u32 2]` as bare
constants; `SpawnPostIir` reads the same two words as `[firstRecordLen][recordCount]` and keeps everything
after them as an opaque `byte[]`. One shared reader walks both:

```
[u32 headCount][headCount x u32]        the plain-counted head word list
[u32 firstRecordLen][u32 word2]         <- the player's "4" and "2"
[firstRecordLen bytes]                  the first record, ENDING in the buff list's framing word
[the entry slot]                        NPC: the morph table · PLAYER: entry table + ID list
[u32 count] pairs, [u32 count] pairs    two PLAIN-counted pair lists
[u32 ListFrame] pairs                   the count-framed third
```

The MINIMAL forms — entry slot and all three pair lists empty — pin the header the two codecs used to disagree
about. [V3: SpawnPostIirGrammarTests.TheNpcAndPlayerPostIirStreams_AreTheSameGrammar]

- The create-editor preview's **40-byte** stream is the fully-empty PLAYER scaffold: `firstRecordLen` 4 (the
  buff list's count word) and every list empty, so `40 = 12 + 4 + 24`.
- The NPC minimal **41-byte** form is the same thing with `firstRecordLen` 5 — one extra leading byte inside
  that first record. **That single byte is the whole reason the two looked like different formats.**
- `firstRecordLen` is the length of the FIRST record only. On a player it stays 4 whether the character has 0
  or 3 buffs, because the buff records are that list's *elements*, not further records.
- ⚠️ **The third header word does not count records — it is the entity's MOVEMENT MODE**, the same value the
  embedded `ProtoCoreCharacterIIR` carries in its own `movement_mode` field (21). One value, sent twice in one
  spawn body, in two unrelated byte regions. `SpawnPostIir.MovementMode`; re-derive with
  `--report field-predictors` [V1: Faolan.Importer/Cli/ReportCli.cs].
  - ⭐ The three values it takes are **WALK (1) / RUN (2) / SPRINT (3)** — named from the client's own speed
    switch and the `<movementset>` XML it reads, not from the capture. See
    [`movetask-grammar.md`](movetask-grammar.md) for the derivation (#606).
  - The evidence is a whole-corpus cross-tab: over **5,739** captured NPC spawns the two agree on **5,738** (the
    one exception is a Rocknose part-way through a path plan), and over **209** captured PLAYER spawns they agree
    on all 209. The player family is what makes this a protocol fact rather than an NPC convention — its scaffold
    is a different grammar and it was not part of the measurement that found the identity.
  - That it counts nothing was already known (streams of identical length carry 1, 2 and 3), and is what made the
    real answer findable. What was *also* written down here — "a **type-stable** enum of unknown meaning" — was
    wrong: it is rolled per spawn EVENT, 61.9% stable across templates even when restricted to freshly-spawned
    NPCs with no move task and no buffs. The type-stability claim was unfalsifiable by construction, because the
    importer scan behind it stopped at the FIRST sighting of each type.
  - What 1/2/3 MEAN is still open (Gitea #606). What the corpus says: of the 562 spawns carrying an active
    move-task plan, 559 are `1`, where a spawn with an empty slot is `2` on 4,064 of 5,168; `3` is 14 sightings,
    every one a Flesheater with an empty slot.
- `12 + firstRecordLen + 24` is the length only when all three pair lists are empty, i.e. the minimal forms. It
  is not a general identity — streams with populated pair lists are longer and still walk exactly.

### The record body, decoded (2026-07-28)

The body between that header and the terminator was written up as *"session runtime state the simulation
re-derives"*. It is the player's own stream, and **every one of the 11,632 captured streams is now read to its
exact last byte** by one of the two codecs:

```
[SpawnPostIir prologue]       header + first record + the ACTIVE BUFFS
  [u32 headCount][headCount x u32]
  [u32 firstRecordLen][u32 word2]
  [firstRecordLen-4 prefix][u32 buff list frame]      <- the record ENDS in the buff count
  [k x 60-byte ScaffoldBuff]
[the ENTRY SLOT]                    NPC: [u32 count][morph entries][u32][u32]
                                    PLAYER: [u32 count][u32 entryTableX][f32 1.0][14-byte entries][ID list]
[SpawnPostIir tail]           [u32 c1][c1 x 8] [u32 c2][c2 x 8] [u32 ListFrame][n x 8]
```

Everything but the entry slot is shared — `SpawnPostIir`, held by both `SimpleCharFullUpdateIIR` and
`SpawnPostIir`, which stay separate codecs because each asserts what its own family guarantees.

| | streams |
|---|---|
| read by the NPC codec | **11,373** |
| read by the player codec | **232** |
| read identically by both | **27** |
| unread | **0** |

[V3: SimpleCharFullUpdateCorpusTests.EveryCapturedSpawn_ParsesToTheExactByte_AndReEncodes]

The 27 that both read are player streams whose entry slot is EMPTY, where the player's
`[count][entryTableX][marker]` and the NPC's `[count][two words]` are the same twelve bytes — the two codecs
read the same stream and put the same bytes back, so it is not an ambiguity. Every NPC-shaped stream also
**re-encodes byte-exact**, which is the acceptance bar for a decoded region.
[V3: SimpleCharFullUpdateCorpusTests.EveryCapturedSpawn_ParsesToTheExactByte_AndReEncodes]

⚠️ **THE BUFF COUNT IS READ, NOT SEARCHED.** The last four bytes of the first record are the buff list's
framing word on all 11,632 streams, without exception. This is invisible from the player side, where the first
record IS that word (length 4, no prefix) so it reads as "the record" rather than as a count of what follows;
an NPC carries a move-task slot in front of it — one `0x00` byte fresh, a whole `MoveTaskNode` mid-session. Searching for the record count instead capped the walk at **99.95%** (11,626 of 11,632) and needed
a "does this look like a buff?" guard to stop the search inventing records out of runs of zero bytes. Reading
it resolves all six holdouts, including the one whose ASCII is scrambled in 4-byte groups (`uoY era
ehtsohCuG`) — a mis-reassembled TCP segment, which reads fine because the corruption is inside a string rather
than in the framing.

**The 60-byte records are the player scaffold's buff records**, not an NPC-only shape. All 941 carry the same
`Kind` and `EntityClass.Character` as their target class, their timer pair orders correctly in every one, and
the nine that appear on NPCs all resolve in our own catalog: *Stagger*, *Hearty Meal*, *Shrink*, *Ether Channel*,
*Runed Flesh (Rank 1/5)*, *Undead Minion: Mutilator*, *Deathless Acolyte: Arcanist*, *Frozen Hatred*. Two of
those are necromancer PET summons and one is the Frozen Hatred proc — corroboration from a direction the walk
knows nothing about (#307/#140).

⚠️ Their `CasterLevel` runs **0..82** (5..82 on the NPC records alone), and 82 is above the level cap. #438 identified this field as the
caster's level by a separate route, but this corpus does not prove it IS the level — the gate pins the
observed range rather than asserting 1..80.

⚠️ Two boundary traps, both of which made a correct model look wrong: the entry table ends **20** bytes from
the stream end, not 24 (the word at −24 belongs to the table's own region), and the terminator's three pair
lists are **counted, not constant** — requiring the last 24 bytes to be a fixed pattern rejected every stream
whose lists are populated.

### The first record's slot is a MOVE TASK (2026-07-28, #477)

The last region of the spawn stream carried as `byte[]`, and the last thing described as *"per-instance runtime
records (movement waypoints / buff-timer pairs), session state the simulation re-derives"*. The guess was right
about what it holds and wrong that it was ours to re-derive: it is the entity's current **MoveTask**, in the
grammar `MoveTaskNode` has parsed since #53.

```
[u8 present]                  0 on a fresh NPC, 1 when a task follows; absent entirely on a player
[MoveTaskNode]                the same recursive node the MoveTaskIIR packet carries
```

All **1,679** captured slots are consumed to their exact last byte by `MoveTaskNode.Read`, and re-encode
byte-exact. The modes are `MoveTaskCodec`'s own:

| mode | | streams |
|---|---|---|
| `0x04` | ListMoveTask — a waypoint path | 1,636 |
| `0x07` | approach/facing | 22 |
| `0x08` | approach target | 18 |
| `0x01` | PathMoveTask | 2 |
| `0x02` | stationary from/to/facing | 1 |

[V3: SimpleCharFullUpdateCorpusTests.EveryFirstRecordSlot_IsAMoveTask]

That the payloads are vec3 positions and unit quaternions is what identified it — and then the sibling codec
parsed all 1,679 without a line of new grammar. Third time in this packet: the pre-IIR frame was the NPC
scaffold, the entry slot was the morph table, this is the MoveTask. **Before decoding an FA015807 region, look
for the codec that already reads it.**

### A buffed humanoid used to spawn BALD (2026-07-28, #475)

The appearance classifier found the morph table by arithmetic — at `12 + firstRecordLen`, i.e. straight after
the first record. That is where the table sits only when the character carries **no active buffs**, because
each buff pushes it 60 bytes further. A humanoid captured mid-buff was therefore classified no-morph, stored
without morph rows, and would spawn with the minimal post-IIR — which drops the appearance table and renders
the NPC bald, the exact failure #243 exists to prevent.

Reading the stream instead of computing an offset lifts **269 more captured bodies and loses none**. In the DB
that is **+258 morph rows across 7 spawns** — all Red Hand City Guards in Tortage (map 4020), which had been
storing no appearance at all. [V3: NpcSpawnAppearanceTests.DecodeAppearanceLenient_WithAnActiveBuff_StillRecoversTheTable]

## Typed scaffold codec — landed (2026-06-16, byte-exact)
`SpawnPreIir` + `SpawnPreIirCodec` (`Faolan.Core.Network/Codecs/Spawn/SpawnPreIir.cs`) decode the
scaffold into a typed, mutable struct (`Version`, 6 `Flags`, 53 `Words`, the typed `Sublist`) and re-encode it
**byte-exact**. The scaffold's start is WALKED to through the body's own header; it used to be computed as
"the fixed 223 bytes before the embedded CoreCharacter", which assumes the sublist — the one variable-width
field — is empty, and so rejected outright the six captured spawns where it is not
(`SpawnPreIirSublistTests`). The post-IIR is likewise typed end to end (`SpawnPostIir` +
`SpawnPostIir`), not a header over raw records. Round-trip gates:
`SpawnPreIirTests` (8 tests, both goldens). This makes the from-DB path own the grammar in code instead of
slicing the captured `Data` blob — the foundation for sourcing the scaffold from DB / zeroing the buffer.

## Two findings that revise the plan above (2026-06-16)
1. **The "synthesized block is rejected" gate is STALE.** That belief came from the mid-session-spawn failure,
   which #23 cracked (2026-06-16) as the **playfield token**, not block synthesis — synthesized spawns now
   render once `SpawnTransformPatch` re-stamps the live token (`[[protocol_midsession_spawn_blocked]]`). So
   the in-client flip for a constructed scaffold is no longer presumed-blocked; it is **testable**, and the
   `npc_spawn_minimal_postiir_breaks_humanoids` result (which predates the token fix) is worth re-running.
2. **The two rendering beach NPCs carry an EMPTY script buffer.** Decoding the `SpawnNpcCrocodile` and
   `SpawnNpcCasilda` goldens (both confirmed to render in-client), `DecodeScriptText` recovers **nothing** —
   their 53 scaffold words are small scalars, floats (e.g. `0x41fadfbc`≈31.4 @0x238), and a few large
   session-volatile-looking values (`0x82a367d0`/`0x82a365b0`/`0x82a36800`, a consecutive-allocation pattern),
   NOT text. The `showfifomessagefromserver` string in the analysis sample was a **script-bound** PvP-festival
   NPC, not a typical mob. So the struct region is a **union**: script-bound NPCs put their diagnostic string
   there; ordinary mobs put runtime scalars/handles there. This is direct evidence for construction **route b**
   (an empty/scalar buffer is a valid rendering form), and it nuances the earlier "purely diagnostic text, no
   instance handles" conclusion — the large values bear watching as possible session refs on the in-client flip.
   **(Confirmed by the 2026-06-17 sweep: those large values ARE per-instance runtime handles/timers; they are 0
   on a fresh spawn, so the minimal build drops them — see "Resolution" above.)**

## From-scratch `Data` retirement — DONE for 824/988 via typed columns (#244, 2026-07-08)

The 2026-06-18 attempt (below, kept for context) concluded a byte-exact `Data` drop was "blocked by the
captures." **That was the wrong framing** — the mistake was validating against a bloated CAPTURE. Building
the CANONICAL fresh spawn from typed columns and emitting runtime state canonical from the live tier drops
the bloat by design, and it generalizes:

- The type-level stat core is lifted into `NpcStatCore` (1:1 side table, ~60 nullable proto-mirror columns +
  the RE'd CoreChar header word `IirHeader`); per-placement overrides live on `NpcSpawn`.
- `NpcSpawnBuilder.BuildMessageFromColumns` + `BuildFromScratch` assemble the whole body from the DB;
  `NpcSpawnFromScratchTests` gates it: **824/988 spawns byte-exact from columns**, 9 canonically cleaner
  (drop a captured runtime target), 155 the humanoid runtime-post-IIR tail (still `Data`).
- Wired live via `NpcSpawnBuilder.TryBuildLive` in `WorldBroadcaster`. See
  `docs/subsystems/npc-three-tier-architecture.md`.

Remaining (#243): the ~155 tail = ~148 no-morph humanoids (`BuildMinimalPostIir(2)`, verified) + ~99
runtime-bearing (morph table embedded after a runtime record — needs a lenient extractor) + 2 entry-runtime.

<details><summary>Original 2026-06-18 attempt (superseded)</summary>

Built the full from-scratch FA015807 (no per-row `Data`) reproducing the two clean goldens (Crocodile +
Casilda) byte-exact, but a clean reseed (109 login-burst NPCs) reproduced only 28/109 because the captured
bodies carry per-session runtime bloat (same Crocodile type at 469/649/733 B). Concluded — wrongly — that a
byte-exact drop needed clean per-type captures. The typed-column path above is the actual answer.
</details>

## Player spawn entry-table CLOSER is per-class (#250, 2026-06-19)

The fresh-character player-spawn scaffold's entry-table **closer** record (the final `[u16 tag][u32 value]`
entry) is **per-class**, not a constant. Stamping one class's closer on another **crashes the client's
CoreCharacter construction on relog** (in-client: a Necromancer carrying the Barbarian closer `0x56B6/0x12C`
crashes the loading screen; its own `0x4301/0x190` loads). Fix (`PlayerSpawnScaffoldRows.FreshCloser(Class)`,
commit 303fd9c): **value = archetype × 100** (Soldier 100 / Priest 200 / Rogue 300 / Mage 400). The **tag**
was first modelled as a per-class constant, but #269 (`parse_spawn_scaffold.py`, 117 player self-spawns,
zero violations) settled it as fully DERIVED: **tag = (fresh feat/skill IdList length + 2) × 0x3F1** — a
count marker the client uses to read the feat list, which is exactly why a mismatched one crashes. It is
race-independent (same class → same fresh feat list → same count → same tag) and holds for feat counts from
14 (Assassin → `0x3F10`) up to 47 (leveled chars); the old per-class hardcoded tags matched only because each
class's feat-list length is fixed. The skill IdList (incl. Necro's feat `85200`) was already faithful to the
dumps and is NOT the crash cause.
