# Combat + world IIR readers (decode batch, Gitea #233)

Wire grammars for a batch of combat / world-entity / movement-adjacent `IIR` records, recovered
from `AgeOfConan.exe` via the vtable-xref method (`tools/ghidra/DumpVtableReaders.java`, slot 8 →
each class's `ReadSubClass`). Validator: `BehaviorControlCenterCodec` — every reader below
consumes **exactly to the declared packet length** for every captured instance.

## Shared framing + primitives

Carrier (the 0x2000 IIR envelope), identical for all records here:

```
[u32 len][u32 sub][u32 0xC350][u32 handle][u8 env][ reader ]
```

A single `env` byte sits between the handle and the inner reader (same as
[CharDCMove](chardcmove-grammar.md)). Stream primitives:

| decompiled fn | meaning |
|---|---|
| `FUN_0048f900` | `ReadU32` — 4 bytes, big-endian (floats are raw-body BE) |
| `FUN_00cba1b0(&x,1)` | `ReadByte` — 1 byte (the `x >> 0x18` idiom extracts it) |
| `FUN_00cbfb80(dst)` | `ReadLengthPrefixedBlob` = `[u32 len][len bytes]` |
| `FUN_0080c620(dst)` | the kd-tree / bounds struct reader (shared with SimpleDynel) |

## Fixed-form readers

| sub-opcode | type | reader (`FUN_*`) | body after `env` |
|---|---|---|---|
| `0x0EED7068` | `ChainKillIIR_t`       | `79c0b0`  | `[u32]` — chained-kill counter |
| `0xCFFD0519` | `CombatShieldIIR_c`    | `113e000` | `[u32]` — shield value |
| `0x6B0AEC58` | `LockedToChangedIIR_t` | `7a0630`  | `[u32][u32]` — lock target change |
| `0x1745EF6C` | `ThreatLevelIIR_c`     | `7a8fd0`  | `[u32 threat][u8 bool]` |
| `0x8A01E5A1` | `HateListUpdateIIR`    | `7a93a0`  | `[u8 flag][u32][u32][u32]` |
| `0x04B80B24` | `SetPosIIR_c`          | `a33770`  | `[float x7]` (pos×3 + rot×4, finite-checked) `[u8 bool]` |
| `0x864CFEF8` | `CharInPlayIIR_t`      | `75a920`  | **empty** — no incremental read; the carrier handle is the whole signal (the reader only resolves the SimpleChar + logs) |

`SetPosIIR_c` is the teleport/snap sibling of `StopIIR_c` (same 7-float pos+rot) plus a trailing
bool. Both assert the pos/rot are finite (`FUN_0184d187`).

## Mode-gated readers

### StatDamageIIR_t (`0xE9F24184`, reader `FUN_008d57c0`)

The damage-result record (attacker handle rides as a field; the carrier handle is the target).

```
if (target_type != 0xC879)  →  [u32 ×6]   # the "full" form fields (0x2c,0x38,0x3c,0x40,0x4c,0x74)
[u32 ×2]                                   # always (0x30,0x34)
[u32 ×2]                                   # always (0x44,0x48)
[u32 ×7]                                   # FUN_00897b60 tail (a 7-word block)
```

The discriminator `target_type != 0xC879` is read from the linked entity, **not** the wire, so it's
not self-describing — but **all 898 captured instances are the long form** (17 u32 = 68 B, total
85 B), so the long layout is the one in practice.

### CombatReactiveIIR_c (`0x54C02089`, reader `FUN_0082c720`)

Self-describing mode word — wire-derivable, byte-exact:

```
[u32 mode]
 mode == 1 → [u32 ×5]   # full reactive (0x34,0x38,0x3c,0x40,0x44)
 mode == 3 → [u32 ×1]   # short (0x30)
 else      → (nothing)
```

## Dynel-family full-updates (SimpleDynel-shaped)

`DoorFullUpdateIIR_c` (`7a2b10`), `DestructibleFullUpdateIIR_c` (`7a3540`),
`ClimbingDynelFullUpdateIIR_c` (`7a78e0`) and `TombStoneFullUpdateIIR_c` (`7a4460`) all share the
[SimpleDynelFullUpdateIIR](../../Faolan.Core.Network/Codecs/World/SimpleDynelIdentityCodec.cs) shape:

```
[u32 blobLen][blob]            # the length-prefixed ProtoDynel-ish blob (FUN_00cbfb80)
[kd-tree]                      # the shared bounds/collision struct (FUN_0080c620, variable)
[u32 × N]                      # trailing scalars: Door/Climbing N=1, TombStone N=2, Destructible N=5
```

The blob length-prefix and the trailing `N` u32s land byte-exact (262/262, 614/614, 214/214, 7/7);
the kd-tree region is the same shared reader already decoded for SimpleDynel.

### The shared bounds/kd-tree struct (`FUN_0080c620`)

Decompiled and checked against every committed `WorldObjects.KdTreeTail` (1052 rows) [V3:
WorldObjectDynelColumnTests]. It is NOT opaque geometry - it opens with an RDB asset reference:

```
[u32 assetId][u32 assetType]   # an RDB resource reference, see below
[u32 nameLen][name]            # length-prefixed ASCII (FUN_0048f840); empty in 1029/1052 rows
[u32 nodeCount]
  nodeCount x [u32 x 6]        # 24 bytes/node on the wire (the client widens each to 32 in its vector)
[f32 minX][f32 minY][f32 minZ] # the AABB; the client derives centre/extent as (max-min)*0.5
[f32 maxX][f32 maxY][f32 maxZ]
[u32][u32]                     # obj+0x30 / obj+0x31
[u8 flag]                      # obj+0x32, read as a bool
```

- `assetType` is one of exactly three RDB types: **1010030** MESHINDEXFILE (628 rows), **1010001** RenderMesh
  geometry (298), **1010201** MONSTERDATA (126) - see [`../rdb/rdb-type-catalog.md`](../rdb/rdb-type-catalog.md).
  `assetId` has 199 distinct values.
- The node list is almost always EMPTY: 1028 of 1052 rows carry `nodeCount == 0`, the other 24 carry exactly one.
  Those 24 are all `SimpleDynelFullUpdateIIR` signposts/landmarks ("Tortage Keep", "West Dock", "Avenue of
  Honor", ...) sharing one sign-shaped AABB, and their single node holds just **two distinct values** across the
  whole corpus: `[id, 0, 0, 0, 0, 34]`. The trailing `34` (`0x22`) is constant; the four middle words are zero;
  and the leading id CO-VARIES WITH THE ASSET - all 23 objects on asset 4935650 (MONSTERDATA) carry `4998690`,
  the one on asset 3761257 (MESHINDEXFILE) carries `3139195`. It matches no id in the database (items, spells,
  maps, npc templates, quests, respawn points), so it is a second client-side resource reference rather than
  geometry. Two samples cannot pin more than that, which is why those six words keep positional names.
- An object with no bounds writes the inverted-box sentinel rather than zeros - min `+FLT_MAX` (`0x7f7fffee`),
  max `-FLT_MAX` (`0xff7fff..`).
- The only name seen in the corpus is `object_closed` (23 rows, all `DoorFullUpdateIIR`-adjacent MONSTERDATA).

Struct + per-class trailer accounts for the record exactly, which is what makes the 18 distinct
`KdTreeTail` lengths fall out of just five classes:

| class | trailer | rows |
| --- | --- | --- |
| `SimpleDynelFullUpdateIIR` `0x66AEDD50` | none | 678 |
| `DestructibleFullUpdateIIR` `0x6BD56736` | `u32 x 5` | 251 |
| `DoorFullUpdateIIR` `0x33A56FB0` | `u32 x 1` | 59 |
| `ClimbingDynelFullUpdateIIR` `0x1FD6412B` | `u32 x 1` | 49 |
| `LootBagFullUpdateIIR` `0xF096251E` | protobuf | 15 |

The container trailer is `[u32 len][ProtoLootBagIIR]` - the chest's loot CONTENTS, not geometry, which is why
those 15 rows vary in length so much. It is capture-time session state, so the server does not persist it (a chest
spawns with a clean empty scaffold and the loot system fills it). What the captures happened to hold was decoded
before the column was retired and kept as evidence in
[`container-loot-observed.json`](container-loot-observed.json) - 15 containers, 45 items, 12 distinct.

## No-op / out-of-band readers

- `UpdateInventoryIIR_t` (`0xA74D2814`, `FUN_005b9230`) — `ReadSubClass` just `return 0`. The
  inventory update content is delivered out-of-band (the `ProtoInventory` path), not through this
  IIR reader, so there is no incremental wire grammar here.

## Status

Decoded + dissected (this doc + `BehaviorControlCenterCodec`). Construct/implement (C# codecs +
golden tests) belongs to the relevant subsystems: combat (#216/#218/`../subsystems/combat.md`) for
StatDamage/CombatShield/ChainKill/CombatReactive/ThreatLevel/HateList, and the simulation/world
layer (#190/#187) for SetPos/CharInPlay + the dynel full-updates.
