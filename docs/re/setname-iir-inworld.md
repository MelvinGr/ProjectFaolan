# In-world `SetNameIIR` (0xADCE0CDA) — override an entity's floating nameplate (#363)

The dev spawn-area nameplate labels (`.togglenpcgroup`) need to change the name shown over a live NPC. This
documents why the obvious approach fails and the byte-exact grammar that works, recovered from `AgeOfConan.exe`
(PE32, ImageBase 0x400000, no ASLR → Ghidra addresses == runtime; project `tools/ghidra/output/aoc.gpr`).

## Why the spawn-body name is ignored

An NPC's floating name is **not** the free `name` string in the FA015807 spawn body. `GetDisplayName`
(`FUN_00809f80`) resolves it from the entity's **template hash** (`entity+0x2e8`, i.e. `npc_stats.template_id`)
via the LDB, and only falls back to a custom name at `entity+0x2c4` when the "has custom name" gate
`entity+0x2d4 != 0` is set. Our DB names only *looked* right because they matched retail's LDB entries;
appending to the spawn `name` changes nothing on the nameplate. Every NPC carries `template_id` (586/586 types
in `faolan.db`), so this affects all real creatures.

## The override path

`SetNameIIR_t` (0xADCE0CDA) is a per-entity name commit the client honours. The **in-world** path
(`n3InfoItemRemote::Construct` → `SetNameIIR_t::ReadSubClass`) is a DISTINCT grammar from the create-editor
`SetNameIIR` (`SetNames.Accepted`, a different reader). Builder: `SetNameCodec.Build(SetNames.Nameplate(...))`.

### Wire record (after the `[u32 len][u32 opcode]` IIR carrier)

```
[u32 type=0xC350]      class (0xC350 = Character/creature) — read by Construct as the identity class
[u32 handle]           entity handle                        — identity handle
[u8  isToBePassed=0]   Construct reads 1 byte (FUN_004e0340); must be 0 or 1  <-- easy to miss; omitting it
                                                                                  shifts everything by one byte
[u32 nameType=3]       ReadSubClass this+0x48; MUST be 1..4 (see Activate)
[u8  0x01]             ReadSubClass this+0x4c (bool)
[u8  0x01]             ReadSubClass this+0x4e (bool)
[u32 nameLen]          ReadSubClass; HARD-CAPPED: must be 1..31 (client checks `< 0x20`), else name is skipped
[nameLen bytes]        ASCII name
```

### Functions

| addr | role |
|------|------|
| `FUN_005b9d30` | `n3InfoItemRemote::Construct` — reads `[hash][class][handle]` + a byte, then calls ReadSubClass (vtable slot 8) |
| `FUN_004e0340` | reads the 1-byte `isToBePassed` flag |
| `FUN_0048f900` | ReadU32 (big-endian); `FUN_00cba1b0` = raw stream copy, decrements the remaining-bytes field `stream+0x10` |
| `FUN_007ae1d0` | `SetNameIIR_t::ReadSubClass` — `[u32 nameType][u8][u8][u32 nameLen][name]`, `nameLen < 0x20` |
| `FUN_007ae3e0` | `SetNameIIR_t::Activate` — applies to the target: `FUN_006ed100(nameType, this+0x4e)` |
| `FUN_006ed100` | the apply: `switch(nameType){case 1..4: apply; default: return}` — **nameType 0 is a silent no-op**; `nameType==3\|\|4` (with the bool set) also **refreshes** the live `OverheadText` (`FUN_006dbc00`/`FUN_00447ba0`) |
| `FUN_00809f80` | `GetDisplayName` — custom name at `+0x2c4` gated on `+0x2d4`, else the `+0x2e8` template hash |
| `FUN_011d84a0` | builds the `OverheadText_c` nameplate object (once, at spawn) |

The nameplate object is created from the LDB name **at spawn time**, so `SetNameIIR` must be sent *after* the
spawn AND use `nameType=3` (or 4) so `FUN_006ed100` re-runs the OverheadText refresh — otherwise the name is
stored but the existing plate isn't redrawn.

## Failure signatures (client log)

- Wrong grammar → `n3InfoItemRemote::Construct: ReadSubClass for IIR -1379005222 (SetNameIIR_t) failed! …
  datasize left in stream=N` (the reader consumed fewer bytes than the blob declared — a missing/extra byte or
  a `nameLen >= 0x20`).
- Parses but nothing shows → `nameType` was outside 1..4 (silent `default` in `FUN_006ed100`), or the message
  arrived before the OverheadText existed / without the refresh (`nameType` not 3/4).

Scripts: `tools/ghidra/DumpSetNameReader.java`, `DumpConstruct.java`, `DumpNameRender.java`,
`DumpSetNameVtable.java`. Golden test: `Faolan.Tests/WorldSetNameIirTests.cs`.
