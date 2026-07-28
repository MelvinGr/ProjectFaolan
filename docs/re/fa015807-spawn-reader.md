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
framing failures) by `PlayerSpawnReader`.

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

`python3 `PlayerSpawnReader`` walks the fixed scaffold for every captured FA015807 record and
checks three predictions: leading version `0x05`, the sub-list's first word is a multiple of `0x3F1`, and the
embedded CoreCharacter version `0x41` lands at the exact predicted offset. Across all login + create captures:

```
FA015807 records: 4558   version==0x05: 4558/4558   framing failures: 0
CoreChar ver==0x41 (count==0 records): 4444/4444
```

## Kind-level Ghidra RE — the block is a fixed-width char buffer, read as 53×U32 (2026-06-15, DONE)
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
reads 4 bytes at a time. `NpcSpawnScaffoldTests` carries the exact wire-order→struct-offset map and
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

So the dominant content of the pre-IIR scaffold is the NPC's **RDB script-engine binding / diagnostic
string** — ScriptGroup / Script / Command names + ids + the `<font color=…>` feedback markup, null-padded
into the fixed-width buffer. It is per-instance text the client reads into a struct char buffer (and
evidently keeps for script/debug, not geometry). Source: the RDB script layer (`[[lua_vm]]` /
`[[protocol_onscreen_feedback]]`), keyed per NPC type.

## The POST-IIR is runtime state, NOT appearance — "missing hair" was the playfield token (corrected 2026-06-17)

An earlier offline pass guessed the larger humanoid post-IIR was a **float-stat / appearance table** and that the
minimal form "drops hair". **The cross-capture sweep (`NpcSpawnAppearanceTests`, 5151 records) disproves it:**
- **Appearance lives in the IIR** (`CharacterStats.head_mesh` / `looks_package`), which the DB build already
  sources — NOT in the post-IIR.
- **19 humanoid NPC types** (Kalanthes, Guard, Nobleman, Black Dragon Guard, Poacher, Bat Pict Mauler, …) emit
  the **41-byte minimal post-IIR themselves** in some captures. A type that renders correctly with the minimal
  post-IIR proves the larger form is not required for its looks.
- The minimal form has just **3 variants differing only in `recordCount` ∈ {1,2,3}** over a constant `0x3F1`
  body; `word0 == 0` on 5148/5151. The larger forms concatenate per-instance **runtime records** (movement
  waypoints / buff-timer pairs), session state the simulation re-derives — not template/appearance data.

So the "missing hair" regression was the **#23 playfield token** (a stale token made the client drop the spawn),
now fixed by `SpawnPlayfieldRewriter` — not the post-IIR content. The minimal post-IIR is correct for animals
**and** humanoids. (`[[npc_spawn_minimal_postiir_breaks_humanoids]]` is corrected accordingly.)

## Resolution — from-scratch minimal scaffold, DEFAULT (2026-06-17, #185 done)
The wire grammar was already pinned; the sweep settled the **construction** question (there is nothing to source).
Implemented:
1. **pre-IIR** — `NpcSpawnPreIir.BuildMinimal(category = 0x6f)`: version `0x05`, all 6 flags 0, all 53 words 0
   except `word17` = the modal category enum. Byte-exact to a real captured minimal NPC.
2. **post-IIR** — the existing `NpcSpawnScaffold.BuildMinimalPostIir()` (recordCount==1) is a captured-valid form.
3. **lift** — `NpcSpawnScaffold.WithMinimalScaffolds(body)` rewrites a captured/cross-session body to the fresh
   form (transform + IIR identity kept, both scaffolds reset), applied in `NpcSpawnBuilder.TryBuild` on every
   `freshSpawn` build. **This is now unconditional/default** (the `FAOLAN_MINIMAL_SCAFFOLD` opt-in is gone); the
   byte-exact reproduction goldens are unaffected because they build with `freshSpawn=false`.
4. **golden** — `SpawnNpcMinimalPanther.bin` (Kier-Dheed's Panther, fully minimal in both halves) proves
   `BuildMinimal` (pre) + `BuildMinimalPostIir` (post) + `WithMinimalScaffolds` byte-exact (`NpcSpawnPreIirTests`,
   526 tests green).

**In-client flip DONE (2026-06-17, #185 verified):** logged onto the beach with the minimal scaffolds active —
world-entry and interest-streamed NPCs render the same ("ncs seem the same"), chat connects (the real load
confirmation), zero stream errors. So the minimal scaffold was made the **default** (commit `48be35a`) and the
`FAOLAN_MINIMAL_SCAFFOLD` / `FAOLAN_NPC_VERBATIM` env toggles + the dead `FAOLAN_BUILT_NPC_TEST` injector were
removed — construct-don't-replay is now the only NPC spawn path. The per-spawn sub-list (`@1122b10`) entry
sub-object grammar stays unobserved (`count==0` everywhere)
but is irrelevant to the minimal build. The same reader primitives + `0x3F1` framing drove the dynel/loot readers
(#187, done) — see `tools/ghidra/DumpIirReaders.java`.

## The NPC post-IIR and the PLAYER post-IIR are the SAME grammar (2026-07-28, #475)

They were modelled as two unrelated formats. `PlayerSpawnScaffoldCodec` writes `[u32 4][u32 2]` as bare
constants; `NpcSpawnPostIir` reads the same two words as `[firstRecordLen][recordCount]` and keeps everything
after them as an opaque `byte[]`. One shared reader walks both:

```
[u32 headCount][headCount x u32]        the plain-counted head word list
[u32 firstRecordLen][u32 word2]         <- the player's "4" and "2"
[firstRecordLen bytes]                  the first record
[u32 entryCount][u32 entryTableX]       the entry table (empty on every NPC form)
  ... entries / ID list / ID-list tail, iff entryCount > 0
[u32 count] pairs, [u32 count] pairs    two PLAIN-counted pair lists
[u32 ListFrame] pairs                   the count-framed third
```

**3,882 captured streams walk to their exact last byte** — every NPC minimal form and every create-editor
preview. [V3: SpawnPostIirGrammarTests.TheNpcAndPlayerPostIirStreams_AreTheSameGrammar]

- The create-editor preview's **40-byte** stream is the fully-empty PLAYER scaffold: `firstRecordLen` 4 (the
  buff list's count word) and every list empty, so `40 = 12 + 4 + 24`.
- The NPC minimal **41-byte** form is the same thing with `firstRecordLen` 5 — one extra leading byte inside
  that first record. **That single byte is the whole reason the two looked like different formats.**
- `firstRecordLen` is the length of the FIRST record only. On a player it stays 4 whether the character has 0
  or 3 buffs, because the buff records are that list's *elements*, not further records.
- ⚠️ **`word2` does not count records.** Streams of identical length carry 1, 2 and 3 in it, so it cannot be
  counting anything that occupies bytes. `NpcSpawnPostIir.RecordCount` and the persisted column
  `NpcStatCore.MinimalPostIirRecordCount` are both misnamed; it is a type-stable enum of unknown meaning.
- `12 + firstRecordLen + 24` is the length only when all three pair lists are empty, i.e. the minimal forms. It
  is not a general identity — streams with populated pair lists are longer and still walk exactly.

## Typed scaffold codec — landed (2026-06-16, byte-exact)
`NpcSpawnPreIir` + `NpcSpawnPreIirCodec` (`Faolan.Core.Network/Codecs/Spawn/NpcSpawnPreIir.cs`) decode the 223-byte
count==0 scaffold into a typed, mutable struct (`Version`, 6 `Flags`, 53 `Words`, the raw `Sublist`) and
re-encode it **byte-exact**, anchored by `PlayerSpawnReader` (the embedded CoreCharacter `0x41` is a fixed
9-byte header before the protobuf name; the scaffold is the fixed 223 bytes before it). The post-IIR header
is likewise typed (`NpcSpawnPostIir`, in `NpcSpawnPostIir.cs`): `[u32 word0][u32 firstRecordLen][u32
recordCount]` + raw records (the records are tick-derived session runtime state, kept raw). Round-trip gates:
`NpcSpawnPreIirTests` (8 tests, both goldens). This makes the from-DB path own the grammar in code instead of
slicing the captured `Data` blob — the foundation for sourcing the scaffold from DB / zeroing the buffer.

## Two findings that revise the plan above (2026-06-16)
1. **The "synthesized block is rejected" gate is STALE.** That belief came from the mid-session-spawn failure,
   which #23 cracked (2026-06-16) as the **playfield token**, not block synthesis — synthesized spawns now
   render once `SpawnPlayfieldRewriter` re-stamps the live token (`[[protocol_midsession_spawn_blocked]]`). So
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
