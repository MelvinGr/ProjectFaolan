# Binary-only IIR readers (decode batch 4, Gitea #233)

Wire grammars for IIR records that exist in `AgeOfConan.exe` but **never appear in our retail
captures** — so the readers are recovered authoritatively from the decompile (vtable-xref,
`Captures/ghidra/DumpVtableReaders.java`, slot 8), but **cannot be byte-validated against samples**.
Treat the field counts as decompile-accurate and the framing as the standard
`[len][sub][0xC350][handle][u8 env][reader]` (unverified env size for these specific types).

Primitives: `FUN_0048f900` = `ReadU32` (BE); `FUN_00cba1b0(&x,N)` = `ReadN` (N=1 byte / 2 u16 /
4 u32, byte-swapped per the `+0x1a` endian flag); `FUN_0048f840` = `ReadString` (length-prefixed);
`FUN_005d5150` = a 0x40-byte sub-record reader; sub-readers noted inline.

## Fixed-size readers

| sub | type | reader | body |
|---|---|---|---|
| `0x6D314743` | `NewLevelIIR_t` | `79bb60` | `[u32×7]` — new level + stat/derived deltas |
| `0x45675F8F` | `GroundEffectIIR_t` | `7afb20` | `[u32×4]` |
| `0x9CC129C5` | `QuestCooldownIIR` | `91cc00` | `[u32×3]` (quest id + cooldown window) |
| `0x80138D7B` | `QuestRewardSelectionIIR_c` | `91c990` | `[u32×2]` |
| `0x0DF44B73` | `AddToGroupIIR_c` | `7a7ca0` | `[u32×4]` |
| `0xE4FF0C85` | `RemoveFromGroupIIR_c` | `7a8050` | `[u32×2]` |
| `0xD6C1F0CC` | `SetShadowCoverageIIR_c` | `7a8910` | `[u32]` |
| `0xDD9BF66A` | `TimeScaleIIR_t` | `5cde80` | `[u32×2]` |
| `0x356184B3` | `UpdateClientVisualIIR_t` | `795e10` | `[u32][u8][u8][u32]` |
| `0x8ACD1AC8` | `LootCharNeedGreedUpdateIIR_c` | `7a6040` | `[u32×4][u8][u8][u32]` |

## Empty / no-op readers

| sub | type | reader | note |
|---|---|---|---|
| `0x26D0EDD5` | `MasterLooterOpenIIR_c` | `7a4a20` | empty reader — carrier handle is the whole signal |
| `0x72491DE9` | `DeleteItemIIR_t` | `005b9230` | `return 0` — delivered out-of-band (like MoveInventoryItem) |

## Mode-gated readers

### FeatIIR (`0x28DA68A0`, `FUN_01152af0`)
```
[u8 type]
 type == 1                 → (nothing)
 type ∈ {3,4,5,6}          → [u32] + sub-record FUN_004d4ef0
 type ∈ {7,10}             → [u32]
 type ∈ {8,9,11}           → [u32][string]
 default                   → (nothing)
```

### AutoToggleCombatIIR_c (`0x107E921F`, `FUN_0148b510`)
```
[u8 flags]   # bit1 → field 0x2c, bit2 → field 0x2d (toggle on/off bits)
```

### ThreatMeterDataIIR_c (`0x8753247D`, `FUN_00512a50`)
```
[u32][u32][u32][u32][u8 hasEntry]
 hasEntry != 0 → [u32][u32][u32] + sub-record FUN_007b7460   # an allocated meter entry
```

## List / variable-length readers

| sub | type | reader | structure |
|---|---|---|---|
| `0x504B7924` | `MainQuestUpdateIIR_c` | `918560` | `[u32×3][u32 n]( [u32][u32] )×n [string][u32×3][u16]` |
| `0x8BB2799C` | `QuestRewardNotifyIIR_c` | `91b2d0` | `[u32][u32 n1]( reward(0x40) + [u32] )×n1 [u32 n2]( reward + [u32] )×n2 [u32×4]` |
| `0xE9EB9694` | `LootSpecialLootUpdateIIR_c` | `7a5540` | `[u32 n]( [u32][u32][u32] )×n` |
| `0xE6C3CCEF` | `ScriptTextIIR_t` | `933390` | `[string][u8 ver][u32][u32]` (ver>1 short-circuits) |
| `0x78BB8C2E` | `TeleportFailedIIR_c` | `5bf830` | `[u32 reason][string]` |

## Sub-reader-delegated (body in a shared helper, not expanded here)

| sub | type | reader | delegate |
|---|---|---|---|
| `0xD558FF16` | `SkillIIR_t` | `7a9a40` | `FUN_004d5900` (skill-list reader) |
| `0xF7BF33DC` | `ClonedFeatsIIR_c` | `79c4d0` | `FUN_0079cfd0` |
| `0x6AC7B325` | `LootDeleteItemsIIR` | `7a5c90` | `FUN_004d4ef0` + `[string]` |
| `0x51BEF2CB` | `LootUpdateAllowedLootersIIR` | `7a4c70` | `FUN_007a4df0` (looter-list reader) |

## Status

Readers recovered (this doc). These are **not implemented or byte-validated** — the retail captures
never exercise them, so they're low priority for the emulator until a feature needs one (groups,
feats/skills, master-looter, PvP threat meter, quest rewards). When that happens, the grammar above
is the starting point; capture or in-client validation is still required before shipping a builder.
