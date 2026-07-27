# The client's CoreTables data tables

The client loads a set of named `DataLoader::DataTable_c<T>` tables at startup and prints each one
(`IMPORTANT: DataTable - Loaded <NAME> ...`). They are ordinary RDB resources, and several carry data that
open issues want.

## How to find any of them

Every table's `LoadStaticData` calls the same loader helper, `FUN_0104d760(&spec, <parseCallback>, &out)`, and
passes its RDB **type** as an immediate. So the whole set enumerates in one step:

```
ADDR=104d760 analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe \
  -noanalysis -scriptPath tools/ghidra -postScript DumpXrefsTo.java
```

19 callers, each one a table's loader. This beats the string-anchor route (`DumpStringXrefs` on the table
NAME), which only works for the tables whose loader happens to log an error - `ArmorMitigation_c` has no such
string, only RTTI.

`LoadStaticData` is **not** virtual, so the vtable is not the way in: dumping `<Table>_c::vftable` shows the
`DataTable_c<T>` scaffolding, not the loader.

## Registry

| RDB type | loader | class |
|---|---|---|
| 1000042 | `FUN_00788e20` | `FeatTable_t` — the wire-id ↔ template-id map + prereq graph (#467) |
| 1000043, 1000099, 1000506 | `FUN_0078de40` | (unnamed) |
| 1000044 | `FUN_00844890` | `FeatClassTable_c` — despite the name, the FEATPOINTS per-level table |
| 1000071 | `FUN_00d8fab0` | `TexScriptMeshGroupValues_c` |
| 1000076 | `FUN_0103f2d0` | morph-target registry |
| 1000080, 1000081 | `FUN_0100d220` | (unnamed) |
| 1000505–1000508 | `FUN_00708690` | (unnamed) |
| 1000509, 1000510, 1000512 | `FUN_00911930` | (unnamed) |
| 1000511 | `FUN_0113e170` | `HitsManaAndStamina_c` — the per-class/level vitals curve (#3) |
| 1000513 | `FUN_00846490` | (unnamed) — 960 entries × 24 B |
| **1000514** | `FUN_0145eb70` | **`ArmorMitigation_c`** (#401) — **EMPTY on this build** |
| 1000517 | `FUN_0057e330` | `GuildGoverningFormDataTable` |
| 1000518 | `FUN_0057edb0` | `GuildRankDataTable` |
| 1000520 | `FUN_00813d20` | (unnamed) |
| 1000521 | `FUN_013b1a10` | `GuildRenownRewardDataTable` |
| 1000625 | `FUN_00847890` | `TokenStateTable` |
| 1000626 | `FUN_006cb5f0` | `CoreChatFilterDataTable` |
| 1000627 | `FUN_013b21c0` | `GroupFinderActivitiesTable` |

## Record layout

A 20-byte header then the entries:

```
[u32 type][u32 id][u32 version][u32 count][u32 ...]  then count entries
```

Fixed-stride for the numeric tables, and the stride solves from `count` and the payload length:

| type | count | stride | note |
|---|---|---|---|
| 1000042 | 6593 | 48 B | first two words are `(wireFeatId, templateId)` |
| 1000044 | 71 | 12 B | `[level 10..80][points][tsPoints]` |
| 1000511 | 95 | 56 B | 5 races × 19 class slots, 14 × i32 each |
| 1000513 | 960 | 24 B | undecoded |

String-bearing tables are **variable-width** - 1000517 (7 entries, 137 B) and 1000518 (38 entries, 1145 B)
do not divide evenly, so they cannot be walked by stride.

## ⚠️ ARMOR_MITIGATION is empty

Type 1000514 is 16 bytes: `[1000514][0][1][0]` - a valid header declaring **zero entries**. The table ships
but carries no rows on this build.

This CONFIRMS #401's blocker rather than removing it. `CombatResolver` notes the client uses its closed-form
mitigation formula "when its ARMOR_MITIGATION table has no matching row"; there are no rows at all, so that
path is always taken, and the formula's `C1/C3/C4` coefficients really do have to come from the runtime
balance config (a live Frida dump). The table route is closed, and now provably so rather than by assumption.
