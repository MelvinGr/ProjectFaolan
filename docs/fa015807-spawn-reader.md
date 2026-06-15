# FA015807 NPC-spawn reader — `SimpleCharFullUpdateIIR_t::ReadSubClass` (Gitea #185 / #54)

RE of the **NPC-spawn pre-IIR scaffold** — the "last un-lifted part" of the from-DB NPC spawn. `NpcSpawnBuilder`
builds the FA015807 (SimpleCharFullUpdate) body byte-exact from DB columns *when they match the capture*, but
the pre-IIR float block + post-IIR scaffold still come verbatim from the row's captured `Data` blob, so
cross-session rows fall back to verbatim burst. This doc decodes the reader so those blocks can be generated.

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

## What this unblocks (the remaining #185/#54 work)
The **structure** is proven. To put the DB rebuild on the wire for every NPC, the remaining step is the
**semantic map** — which DB column feeds each of the 53 scaffold U32s — so `NpcSpawnBuilder` can emit them
from columns and re-address the handle to the live session, instead of slicing them out of the captured
`Data` blob. The two pieces still open:
1. The per-spawn sub-list (`@1122b10`) entry sub-object grammar (`count==0` on beach NPCs, so unobserved there).
2. The column↔offset mapping for the 53 scaffold words (most are flags/refs that are 0 or constant on the
   beach set — a diff across spawn rows will isolate the few that vary).

Once mapped, retire the `Data`-blob slice in `NpcSpawnBuilder` and `RetailLoginReplay` can construct every NPC.
The same reader primitives + `0x3F1` framing drive the dynel/loot readers (#187) — see `Captures/ghidra/DumpIirReaders.java`.
