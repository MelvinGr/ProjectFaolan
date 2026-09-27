# Combat + world IIR readers

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

The discriminator `target_type != 0xC879` is read from the linked entity, **not** the wire, so it is
not self-describing — but **all 898 captured instances are the long form** (17 u32 = 68 B, total
85 B), so the long layout is the one in practice.

The 7-word tail reader **scatters** rather than storing in order. `FUN_00897b60` is called with
`EDI = record + 0x50` and lands wire positions 1..7 at `+0x50, +0x58, +0x54, +0x5c, +0x70, +0x60,
+0x64` — the last one via `MOVSS`, so that word is a **float**. So `spellBaseId` (`+0x54`) is the
third tail word on the wire while `idx` (`+0x50`) is the first; reading them in offset order puts
the spell id in a word nothing reads.

#### Its apply — `FUN_008d5960` (vtable slot 2)

The reader gives the shape; the apply gives the meaning (a client log alone does not).

```c
target = dynamic_cast<CoreCharacter*>(record->carrier);        // else the 0xC879 Destructible branch
caster = (record->casterClass == 0xC350) ? resolve(record->caster) : 0;
... combat-log / floating-number render (FUN_006a5180 / FUN_006a5ac0) ...
if (record->deathReason == 10 && caster) FUN_007e95a0(record->spellBaseId);   // the ONLY spellBaseId read
target->stats.SetStat(0x152, record->deathReason);                            // 0x152 = DeathReason
target->stats.SetStat(record->statId,
                      target->stats.GetStat(record->statId) + record->delta); // UNCONDITIONAL
if (record->deathReason) target->vtable[0x278](record->deathReason);
```

| field | offset | what the apply does with it |
|---|---|---|
| `statValue` | `+0x2c` | **nothing** — never read; the client recomputes from what it holds |
| `delta` | `+0x30` | the only field that moves a stat, added to the current value |
| `school` | `+0x34` | combat-log argument |
| `deathReason` | `+0x40` | `SetStat(0x152)`, gates the spell lookup at 10, and a target virtual at `+0x278` when non-zero |
| `casterClass` / `caster` | `+0x44` / `+0x48` | resolved to the attacker; `0xC350` required |
| `statId` | `+0x4c` | which stat the delta moves |
| `idx` | `+0x50` | non-zero picks the richer combat-log path |
| `spellBaseId` | `+0x54` | read only inside `if (deathReason == 10)`; gates nothing |
| `count` | `+0x74` | tweaks the combat-log verb at 5 and 6 |

**A record naming spell 0 is not voided.** The health write sits below the spell lookup and does
not depend on it. `SpellTemplate_t::GetMagicSpell` (`FUN_0099bef0`) has ~80 callers, so its error
line appearing next to a `StatDamageIIR` in a log is coincidence, not causation.

**The delta is on the ×100 vitals scale**, and the corpus proves it without needing an absolute:
`statValue` equals the same handle's `StatUpdateIIR` stat 27 **exactly** (diff 0 on twelve handles,
up to 92 records each) and moves by exactly `delta` across 1,015 consecutive pairs.

`StatUpdateIIR_c`'s apply (`FUN_007a00e0`) is the contrast worth having beside it: a plain loop
calling the same `[entity+0x10c] +0xc` setter for every `(statId, value)` pair, with **no vitals
filter and no own-handle special case**. `RegenIIR` (`FUN_0079dd10`) reaches that setter too, via
`+8` then `+0xc` with `current + delta`. One setter, three callers.

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
WorldObjectDynelColumnTests]. It is not opaque geometry - it opens with an RDB asset reference:

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
- The node list is almost always empty, and the nodes that exist are **not geometry**. They are a
  standing particle effect and the bone it hangs on:

  ```
  [effect] [0] [0] [0] [0] [attractor]
  ```

  - **`effect`** (ordinal 0) is an RDB **1010207** record — the particle-effect type. All **11** shipped ids
    resolve there in the client's own RDB, checked one at a time (`--report rdb-id`).
  - **`attractor`** (ordinal 5) is the attachment point — the same slot
    `SimpleDynelVisualChangeCodec.StartParticleEffect` carries. It takes exactly **{34, 35}** and no other value,
    and it is **constant per effect id** (4998690 and 3139195 always hang on 34, the other nine always on 35).
    That is what an attachment point is; a coordinate could not be.

  **It is not the visual-change payload with its columns rotated.** That payload's order is client-confirmed
  from the reader itself (the `if (attractor < 1)` branch): `[effect][attractor][rotation][x][y][z]`, attractor
  at index **1**. Here it is at index **5**. Two different layouts over the same six semantic fields, not one
  layout read two ways.

  **The middle four (ordinals 1–4) are `0` in every one of the 127 shipped nodes**, so which is the rotation
  and which are the offset is **corpus-undecidable**. `[effect][four zeros][attractor]` is the whole of what the
  data supports. (A smaller corpus — 24 nodes, two effect ids, attractor always 34 — makes the id look like it
  co-varies with the asset; the full population does not.)
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

The container trailer is `[u32 len][ProtoLootBagIIR]` - the chest's loot contents, not geometry, which is why
those 15 rows vary in length so much. It is capture-time session state, so the server does not persist it (a chest
spawns with a clean empty scaffold and the loot system fills it). What the captures held is kept as evidence in
[`container-loot-observed.json`](container-loot-observed.json) - 15 containers, 45 items, 12 distinct.

## SimpleDynelVisualChangeIIR (`0xE7F4F8F8`, reader `FUN_007a6910`, applier `FUN_007a6480`)

Changes one aspect of a world object's presentation. The reader is a `switch` on a u32 `kind` that selects the
payload; the **applier beside it** is the same switch again, and every case logs its own enum name into the
`GameCode/simpledynel` debug channel before acting - so the meanings below are the client's words, not an
inference from what a payload looked like [V3: SimpleDynelVisualChangeCodecTests.TheHarvestSequence_MatchesTheCapturedBrick].

```
[u32 typeTag][u32 handle][u8 0][u32 kind][ payload ]
```

| kind | client name | payload | what it does |
|---|---|---|---|
| 0 | `e_SetBaseAnim` | `[u16 len][ascii]` | the resting animation - `"Open"`, `"object_open"`, `"object_closed"` |
| 1 | `e_PlayActionAnim` | `[u16 len][ascii]` | a one-shot animation |
| 2 | `e_StartParticleEffect` | `[effect][attractor][rot°][x][y][z]` | see below |
| 3 | `e_StopParticleEffect` | `[u32]` | 0 in all 19 captured records |
| 4 | `e_ChangeMesh` | `[rdb id][rdb type]` | swaps the geometry; id 0 draws nothing |
| 5 | `e_SetKDTree` | `[rdb id]` | swaps the collision; id 0 drops it |
| 6 | `e_SetName` | `[u16 len][ascii]` | |
| 7 | `e_SetDisplayName` | `[u16 len][ascii]` | |
| 8 | `e_FadeIn` | `[f32 seconds]` | 2.0 on a harvest, 0.2 / 0.5 elsewhere |
| 9 | `e_FadeOut` | `[f32 seconds]` | |
| 10 | `e_ChangeDecal` | `[u32]` | |
| 11 | `e_SetBounds` | `12 x u32` | |
| 12 | `e_SetInverseOBBMatrix` | `16 x u32` | a 4x4 matrix |

The three that name a resource were each checked against the client's own RDB index rather than taken on the
name:

- **`e_ChangeMesh`'s pair is the same `(id, type)` the object's spawn already carries** (the bounds struct above,
  `WorldObject.GeometryAssetId`/`GeometryAssetType`). The applier stores them at dynel+0x1D0/+0x1D4 and rebuilds.
- **`e_SetKDTree`'s id is the mesh manifest's own `<CollisionMesh2 file>`.** `1010030:23748` is
  `quest_quarry_stonebrick01` and names `168444`; that is exactly what retail sends after a `ChangeMesh` to
  23748, and it is what `WorldObject.Kdtree` holds. Same for both harvest-node meshes (`3113740` → `3113743`,
  `3113735` → `3113739`).
- **`e_StartParticleEffect`'s first word is an RDB **1010207** record** - the client reads only the id and pairs
  it with `0xF6A1F` itself, and errors as "StartParticleEffect() Invalid particle resource id". The second word
  is the attractor (attachment point): `if (attractor < 1)` the effect is placed free-standing at the trailing
  position instead, which is the branch retail's quarry harvest takes - its three floats are the dynel's own
  world position to two centimetres. The third word is degrees (the client multiplies by π/180).

**The six-word "node" in the bounds struct above is the same thing:** 34/35 is precisely the attractor word, and
every node id (`4998690`, `3198555`, `3198574`, `3139195`, …) is a type-1010207 record. So a signpost's "second
client-side resource reference" is a standing particle effect plus the bone it hangs on, not geometry.

## No-op / out-of-band readers

- `UpdateInventoryIIR_t` (`0xA74D2814`, `FUN_005b9230`) — `ReadSubClass` just `return 0`. The
  inventory update content is delivered out-of-band (the `ProtoInventory` path), not through this
  IIR reader, so there is no incremental wire grammar here.

## Decode coverage

Decoded + dissected (this doc + `BehaviorControlCenterCodec`). The codecs and their use live with the
subsystems — [`../subsystems/combat.md`](../subsystems/combat.md) for StatDamage/CombatShield/ChainKill/
CombatReactive/ThreatLevel/HateList, and the simulation/world layer for SetPos/CharInPlay + the dynel
full-updates.
