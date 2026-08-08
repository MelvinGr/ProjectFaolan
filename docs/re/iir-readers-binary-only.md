# Binary-only IIR readers (decode batch 4, Gitea #233)

Wire grammars for IIR records that exist in `AgeOfConan.exe` but **never appear in our retail
captures** — so the readers are recovered authoritatively from the decompile (vtable-xref,
`tools/ghidra/DumpVtableReaders.java`, slot 8), but **cannot be byte-validated against samples**.
Treat the field counts as decompile-accurate and the framing as the standard
`[len][sub][0xC350][handle][u8 env][reader]` (unverified env size for these specific types).

> **Note (SHIPPED since batch 4):** two rows below have since surfaced in captures and are now
> implemented byte-exact — `NewLevelIIR_t` (`NewLevelCodec`, golden `NewLevelCodecTests`) and
> `MainQuestUpdateIIR_c` (`MainQuestUpdateCodec`, golden `QuestTests`). Their
> rows carry an inline SHIPPED annotation; the rest remain binary-only.

Primitives: `FUN_0048f900` = `ReadU32` (BE); `FUN_00cba1b0(&x,N)` = `ReadN` (N=1 byte / 2 u16 /
4 u32, byte-swapped per the `+0x1a` endian flag); `FUN_0048f840` = `ReadString` (length-prefixed);
`FUN_005d5150` = a 0x40-byte sub-record reader; sub-readers noted inline.

## Fixed-size readers

| sub | type | reader | body |
|---|---|---|---|
| `0x6D314743` | `NewLevelIIR_t` | `79bb60` | `[u32×7]` — new level + stat/derived deltas. **SHIPPED:** now byte-exact vs the combat-levelup capture — `NewLevelCodec` / `NewLevelCodecTests` |
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
| `0x504B7924` | `MainQuestUpdateIIR_c` | `918560` | `[u32 op][u32 areaId][u32 trackedQuestId][u32 n]( [u32 ordinal][u32 questId] )×n [string][u32×3][u16]` — **SHIPPED** as `MainQuestUpdateIIR` (`BKDR-31("MainQuestUpdateIIR_c")==0x504B7924`): byte-exact wire in `MainQuestUpdateCodec`, golden `QuestTests`, and every captured body round-trips since #545. The reader validates **nothing**; the leading word is the `Activate()` (`FUN_00918cf0`) switch selector — see below |
| `0x8BB2799C` | `QuestRewardNotifyIIR_c` | `91b2d0` | `[u32][u32 n1]( reward(0x40) + [u32] )×n1 [u32 n2]( reward + [u32] )×n2 [u32×4]` |
| `0xE9EB9694` | `LootSpecialLootUpdateIIR_c` | `7a5540` | `[u32 n]( [u32][u32][u32] )×n` |
| `0xE6C3CCEF` | `ScriptTextIIR_t` | `933390` | `[string][u8 ver][u32][u32]` (ver>1 short-circuits) |
| `0x78BB8C2E` | `TeleportFailedIIR_c` | `5bf830` | `[u32 reason][string]` |

### `MainQuestUpdateIIR_c::Activate()` — the leading word is an OPERATION [V1: tools/ghidra/StrXrefs.java]

`FUN_00918cf0` switches on the first field the reader stores (`+0x2c`), and each arm logs its own name to the
`GameCode/Quest` channel. The strings ARE the enum:

| value | client log line | fields the arm uses |
|---|---|---|
| 0 | `Activate() was called with Invalid type!` | — |
| 1 | `Activate() was called to add a new MainQuest.` | `areaId`, `trackedQuestId`, the entry pairs, and all three trailing words, forwarded into the character's quest holder (`FUN_00810840`, which stores two of them at holder `+0x48` / `+0x50`) |
| 2 | `Activate() was called to remove a MainQuest.` | `FUN_00810950` |
| 3 | `Activate() was called to update the StartTemplate of a MainQuest.` | `FUN_008109a0` |
| 4 | `Activate() was called to update the timeout.` | the trailing **u16** only: `0` shows `"Entering mission area"`, non-zero `"Leaving mission area"` |

The Add arm locates the tracked quest by scanning the entry-pair vector for one whose **second** u32 equals
`trackedQuestId` — which is what proves the pair is `(ordinal, questId)` and not the reverse. Captures carry only
Add (every populated list) and Remove (every empty one), and `Remove` is why a "leading constant 1" rejected 8 of
the 46 captured lists until #545.

## Sub-reader-delegated (body in a shared helper, not expanded here)

| sub | type | reader | delegate |
|---|---|---|---|
| `0xD558FF16` | `SkillIIR_t` | `7a9a40` | `FUN_004d5900` (skill-list reader) — **SHIPPED**: the reader is the whole body, `[ListFrame(n)]( [u32 statId][i32 delta] )xn` after the base record, and it is the skillpoint SPEND. See [`../subsystems/skillpoints.md`](../subsystems/skillpoints.md) |
| `0xF7BF33DC` | `ClonedFeatsIIR_c` | `79c4d0` | `FUN_0079cfd0` |
| `0x6AC7B325` | `LootDeleteItemsIIR` | `7a5c90` | `FUN_004d4ef0` + `[string]` |
| `0x51BEF2CB` | `LootUpdateAllowedLootersIIR` | `7a4c70` | `FUN_007a4df0` (looter-list reader) |

## Batch 5 — remaining wire types (PvP / construction / tradeskill / tool / stat-template)

### Fixed-size

| sub | type | reader | body |
|---|---|---|---|
| `0x7057225B` | `PVPDropItemIIR_t` | `8d6690` | `[u32×6]` |
| `0x1C4528A7` | `FanaticIIR_t` | `663400` | `[u32×3]` (2nd/3rd default to a stored pair if both 0) |
| `0x09012E82` | `FloatStatIIR` | `4e6da0` | `[u32 statId][u32 value]` — the float-typed stat record |
| `0xA2F53610` | `Int32StatIIR` | `4e6f10` | `[u32 statId][u32 value]` — the int-typed stat record |

### String-bearing / variable

| sub | type | reader | structure |
|---|---|---|---|
| `0xC415E6ED` | `StringStatIIR` | `4e70f0` | `[u32 statId][string]` — the string-typed stat record |
| `0x3188C31B` | `ToolMessageInternalIIR_t` | `5be880` | `[u32][u32][string]` |
| `0x8AD083D9` | `ConstructionOrderIIR_c` | `6d9030` | `[string][u32][string][u8 bool]` |
| `0x5D7884EE` | `n3PlayfieldFullUpdateIIR_t` | `13d0a20` | `[u32×3]` + PlayfieldProxy stream + `[u32]` + sub `FUN_00cbf5d0` |

### Empty / no-op (delivered out-of-band)

| sub | type | reader | note |
|---|---|---|---|
| `0x16294E79` | `GameCodeDynelFullUpdateIIR` | `005b9230` | `return 0` — content via the `ProtoGameCodeDynel` protobuf |
| `0x156687CC` | `MassivePvPDamageLocationIIR_c` | `005b9230` | `return 0` |
| `0x87A3B396` | `n3ToServerUnBlockIIR_t` | `13ce1a0` | `return 0` |

### Sub-reader-delegated / large bodies (reader located, body not expanded)

`MassivePvpScoreIIR_c` (`6d5590`→`6d5800`), `ConstructionMenuIIR_c` (`6d7ae0`, 2.4 KB),
`TradeskillReplyIIR_c` (`7aab30`), `TradeskillRequestIIR_c` (`7aa600`), `FanaticCommandIIR_t`
(`663860`), `ToolMessageIIR_t` (`5be2f0`), `ScryLuaIIR` (`9c26a0` — the Scry/Lua tutorial driver),
`CoreFullPlayerCharacterIIR` (`797440` — a player full-update kin to `FullPlayerCharacterIIR`).

> The `*StatIIR` trio (`FloatStatIIR`/`Int32StatIIR`/`StringStatIIR`) are the per-type stat-record
> readers behind the count-prefixed stat blocks in [player-stats.md](../subsystems/player-stats.md): each is
> `[u32 statId][value]` where value is a u32 (float/int) or a length-prefixed string.

## Batch 6 — debug/editor classes + the `Proto*` protobuf types (catalog closeout)

### Debug / editor / GM readers

These are dev/GM tooling, not normal gameplay traffic — recovered for completeness.

| sub | type | reader | body |
|---|---|---|---|
| `0x45ECFD79` | `FlushRDBCachesIIR_c` | `661890` | `[u32]` |
| `0xBF12AA7C` | `GMStatIIR_c` | `8d4750` | `[u32×2]` (GM stat set: id + value) |
| `0xAFE243E6` | `ServerPosDebugInfoIIR_c` | `a33da0` | `[u32×9]` (debug position/orientation dump) |
| `0x52076086` | `IIRDebugView` | `adf280` | empty stub — a UI/debug-view class, no wire body |
| `0xAC1383D3` | `HiveDebugIIR_c` | `6d63f0` | large debug reader (body not expanded) |
| `0x9F2C283B` | `NPCDebugManagerIIR_c` | `794db0` | large debug reader |
| `0xEE7F5776` | `PlayfieldDebugManagerIIR_c` | `8f2b20` | large debug reader |
| `0x09F159E4` | `RequestDebugInfoIIR_c` | `6dcc40` | large debug reader |
| `0x9D5F1DDC` | `IIRStatListViewItem` | `c07c90` | UI list-view item (editor stat inspector) |

### `Proto*` types are protobuf, not IIR stream readers

The 17 `Proto*` catalog entries (and their nested submessages) are **protobuf message types**, fully
specified by the recovered client schema at
[`Faolan.Core/Protobuf/aoc-client-protos.proto`](../../Faolan.Core/Protobuf/aoc-client-protos.proto)
— so they are "decoded" by the schema, not by a hand-rolled `ReadSubClass`. (Their vtable slot 8 is the
generated protobuf serialize/parse virtual, not the IIR stream reader.) Covered there:

`ProtoMoveInventoryItemIIR`, `ProtoDeleteItemIIR`, `ProtoFullCharacterIIR`, `ProtoSimpleDynelIIR`,
`ProtoLootBagIIR` (+ `SpecialLootOwnerEntry`), `ProtoGameCodeDynelIIR` (+ `RunningEffects`),
`ProtoScryScriptIIR` (+ `Variable`), `ProtoCoreCharacterIIR` (+ `CharacterStats` / `PlayerStats` /
`NpcStats` / `InventoryEntry` / `InventoryEntry.GemSlot` / `CharacterBcc`).

## Decode coverage

Readers recovered (this doc). These are **mostly not implemented or byte-validated** — the retail
captures never exercise them (groups, feats/skills, master-looter, PvP threat meter, quest rewards).
(Exceptions: `NewLevelIIR_t` and `MainQuestUpdateIIR_c` have since surfaced in captures and shipped
byte-exact — see their SHIPPED-annotated rows.) When a feature needs one of the rest, the grammar
above is the starting point; capture or in-client validation is still required before shipping a
builder. Wiring the recovered-but-unused builders to live triggers is tracked in Gitea #405.

**Catalog closeout:** with this batch every one of the 141 IIR classes is accounted for — on-wire
types byte-exact decoded, binary-only readers recovered, debug/editor readers recovered, and the
`Proto*` types covered by the protobuf schema. Nothing in the client's IIR catalog is now unmapped.
