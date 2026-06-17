# FA015807 NPC-spawn reader — `SimpleCharFullUpdateIIR_t::ReadSubClass` (Gitea #185 / #54)

RE of the **NPC-spawn pre-IIR scaffold** — the "last un-lifted part" of the from-DB NPC spawn. `NpcSpawnBuilder`
builds the FA015807 (SimpleCharFullUpdate) body byte-exact from the row, re-addressing the live handle/position +
token; it is now the SOLE NPC-spawn path (every FA015807 is dropped from the login burst and interest-spawned, so
there is no verbatim-burst fallback anymore — #185). The pre-IIR float block + post-IIR scaffold still ride the
row's captured `Data` blob: the decode (this doc) confirms they carry NO session-volatile data (the char buffer is
the NPC's RDB script-binding string; the only stale ref is the transform's playfield token), so carrying them is
correct. Generating them WITHOUT `Data` is per-NPC RDB-derived (221/222 scaffold bytes vary across 5151 records),
so it needs RDB extraction of the per-NPC script bindings + config — the remaining lift.

Decompiled from `AgeOfConan.exe` (Ghidra) — reproduce with `Captures/ghidra/DumpSpawnIir.java` +
`DumpFns.java` (both committed). Grammar **validated against 4558 captured FA015807 records** (zero
framing failures) by `Captures/decode_fa015807_reader.py`.

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

`python3 Captures/decode_fa015807_reader.py` walks the fixed scaffold for every captured FA015807 record and
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
(`Captures/ghidra/out/spawn_iir.txt` + `spawn_fns.txt`):

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
reads 4 bytes at a time. `analyze_fa015807_scaffold.py` carries the exact wire-order→struct-offset map and
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

## What this means for construction (#185/#54)
The **wire grammar is fully pinned AND the content is identified** — no decoding remains for the beach case
(sublist count 0). What's left is sourcing + the in-client flip:
1. **The char buffer = the NPC's script-binding string** (above). Two construction routes, both in-client
   gated: (a) author the buffer from the NPC's RDB script binding, or (b) since it reads like an engine
   *diagnostic* the client only stores, test whether a **null/empty** buffer still renders (would make
   construction trivial — but `[[npc_spawn_minimal_postiir_breaks_humanoids]]` is the cautionary precedent).
2. The few genuine scalars (`w21`@0x1fc, the const-zero `w22`@0x200 / `w45`@0x224, numeric `w50`@0x23c) are
   small per-NPC numeric fields — author as constants / simple values.
3. The per-spawn sub-list (`@1122b10`) entry sub-object grammar is still unobserved (`count==0` on beach NPCs).

Net: there is **no remaining read-grammar unknown** for the beach case; byte-exact construction is now a
content-sourcing job (RDB script text + a handful of scalars). The same reader primitives + `0x3F1` framing
drive the dynel/loot readers (#187, done) — see `Captures/ghidra/DumpIirReaders.java`.

## Typed scaffold codec — landed (2026-06-16, byte-exact)
`NpcSpawnPreIir` + `NpcSpawnPreIirCodec` (`Faolan/GameServer/Spawn/NpcSpawnPreIir.cs`) decode the 223-byte
count==0 scaffold into a typed, mutable struct (`Version`, 6 `Flags`, 53 `Words`, the raw `Sublist`) and
re-encode it **byte-exact**, anchored by `PlayerSpawnReader` (the embedded CoreCharacter `0x41` is a fixed
9-byte header before the protobuf name; the scaffold is the fixed 223 bytes before it). The post-IIR header
is likewise typed (`NpcSpawnPostIir`, in `NpcSpawnScaffold.cs`): `[u32 word0][u32 firstRecordLen][u32
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
