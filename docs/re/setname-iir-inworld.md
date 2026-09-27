# In-world `SetNameIIR` (0xADCE0CDA) — override an entity's floating nameplate

The dev spawn-area nameplate labels (`.togglenpcgroup`) change the name shown over a live NPC. This documents why
the spawn-body name cannot do that and the byte-exact grammar that does, recovered from `AgeOfConan.exe` (PE32,
ImageBase 0x400000, no ASLR → Ghidra addresses == runtime; the Ghidra project lives in the data repo's `Ghidra/`).

## Why the spawn-body name is ignored

An NPC's floating name is **not** the free `name` string in the FA015807 spawn body. `GetDisplayName`
(`FUN_00809f80`) resolves it from the entity's **template hash** (`entity+0x2e8`, i.e. `npc_stats.template_id`)
via the LDB, and only falls back to a custom name at `entity+0x2c4` when the "has custom name" gate
`entity+0x2d4 != 0` is set. A DB name shows only because it matches retail's LDB entry; changing the spawn
`name` changes nothing on the nameplate. Every NPC carries `template_id` (586/586 types
in `faolan.db`), so this affects all real creatures.

## The override path

`SetNameIIR_t` (0xADCE0CDA) is a per-entity name commit the client honours. The **in-world** path
(`n3InfoItemRemote::Construct` → `SetNameIIR_t::ReadSubClass`) is a distinct grammar from the create-editor
`SetNameIIR` (`SetNames.Accepted`, a different reader). Builder: `SetNameCodec.Build(SetNames.Nameplate(...))`.

### Wire record (after the `[u32 len][u32 opcode]` IIR carrier)

```
[u32 type=0xC350]      class (0xC350 = Character/creature) — read by Construct as the identity class
[u32 handle]           entity handle                        — identity handle
[u8  isToBePassed=0]   Construct reads 1 byte (FUN_004e0340); must be 0 or 1  <-- easy to miss; omitting it
                                                                                  shifts everything by one byte
[u32 nameType=3]       ReadSubClass this+0x48; must be 1..4 (see Activate)
[u8  0x01]             ReadSubClass this+0x4c (bool)
[u8  0x01]             ReadSubClass this+0x4e (bool)
[u32 nameLen]          ReadSubClass; hard-capped: must be 1..31 (client checks `< 0x20`), else name is skipped
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
spawn and use `nameType=3` (or 4) so `FUN_006ed100` re-runs the OverheadText refresh — otherwise the name is
stored but the existing plate isn't redrawn.

## What the plate renders beside the name: `max(Level, ApprenticeLevel)`

The `" (%d)"` after the name is **not** the Level stat. The nameplate text builder (`FUN_011d8b00`) reads both
level stats off the entity and formats the larger of the two:

```c
iVar4 = GetStat(0x36,  0);     // Level
iVar5 = GetStat(0x3c5, 2);     // ApprenticeLevel — the mentor system's EFFECTIVE level
if (iVar5 < iVar4) iVar5 = iVar4;
if (*(int *)(param_1 + 0x68) == 0xe) iVar5 = 100;   // one entity kind renders a flat 100
FUN_010cf890(" (%d)", iVar5);
```

That is correct for a mentored character — the plate is meant to show the level you effectively fight at — and
it is why a stale `ApprenticeLevel` is visible: it wins the `max` and the plate disagrees with the character
sheet, which reads `0x36` alone. Lowering only Level (e.g. `.level 10` on a level-23 character) shows `10` on the
sheet and `(23)` over the health bar.

A stale plate is not a missing refresh. The plate subscribes to the stats it renders — `FUN_011d79e0` registers
`FUN_011d9490` as the change callback for `0xD7` (GmLevel), `0x36` (Level), `0x3C5` (ApprenticeLevel), `0x46`
(PvpLevel), `0x1E86AE` and `0x579` — so it rebuilds on the Level push and re-reads the same stale max.

Retail never lets the two drift: `apprentice_level` is serialized on **all 17,717 captured spawns** and equals
`level` in every one (zero differing). So an un-mentored announce re-states both, which is what
`CharacterStatBlock.SetLevel` is for. `SimpleChar_t::SetStat` additionally clamps `0x3C5` back to the Level
stat for a value `< 1` or `> 80` on a non-GM client, logging
`Trying to set e_ApprenticeLevel to %d in SimpleChar::SetStat.`

## Failure signatures (client log)

- Wrong grammar → `n3InfoItemRemote::Construct: ReadSubClass for IIR -1379005222 (SetNameIIR_t) failed! …
  datasize left in stream=N` (the reader consumed fewer bytes than the blob declared — a missing/extra byte or
  a `nameLen >= 0x20`).
- Parses but nothing shows → `nameType` was outside 1..4 (silent `default` in `FUN_006ed100`), or the message
  arrived before the OverheadText existed / without the refresh (`nameType` not 3/4).

Scripts: `tools/ghidra/DumpSetNameReader.java`, `DumpConstruct.java`, `DumpNameRender.java`,
`DumpSetNameVtable.java`. Golden test: `Faolan.Tests/WorldSetNameIirTests.cs`.
