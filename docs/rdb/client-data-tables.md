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

### Naming the class behind a loader

The loader address is enough to find the table but not to name it. The RTTI walk closes that: an MSVC vtable is
preceded by a pointer to its Complete Object Locator, whose `+0xC` is the type descriptor, whose `+8` is the
decorated name. So going the other way — find the data word holding the loader's address, walk back to the head
of that vtable, read the COL — names the class. `RDBComboLookup_c` (1000513) was recovered exactly this way: its
vtable at `0207a534` carries `0x846490` in slot 11, and the COL one word before it resolves to
`.?AVRDBComboLookup_c@@`.

⚠️ `LoadStaticData` is virtual on some of these classes and not on others. It IS a vtable slot on
`RDBComboLookup_c`; on `ArmorMitigation_c` it is not, and dumping that vftable shows only the `DataTable_c<T>`
scaffolding. So the vtable is a route in, not *the* route in — a miss there says nothing.

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
| 1000513 | `FUN_00846490` | **`RDBComboLookup_c`** — the per-class/level combo DPS + stamina curve (see below) |
| **1000514** | `FUN_0145eb70` | **`ArmorMitigation_c`** (#401) — **EMPTY on this build** |
| 1000517 | `FUN_0057e330` | `GuildGoverningFormDataTable` |
| 1000518 | `FUN_0057edb0` | `GuildRankDataTable` |
| 1000520 | `FUN_00813d20` | (unnamed) |
| 1000521 | `FUN_013b1a10` | `GuildRenownRewardDataTable` |
| 1000625 | `FUN_00847890` | `TokenStateTable` |
| 1000626 | `FUN_006cb5f0` | `CoreChatFilterDataTable` |
| 1000627 | `FUN_013b21c0` | `GroupFinderActivitiesTable` |

## Record layout

The first three words are always `[u32 type][u32 id][u32 version]`, and for most tables the fourth is the entry
count — a **16-byte** header, then `count` entries:

```
[u32 type][u32 id][u32 version][u32 count]  then count entries
```

⚠️ The header is not uniformly 16 bytes. 1000042 and 1000511 carry one extra word (1000511's count is the
FIFTH word, not the fourth: its header reads `[1000511][0][1][2][95]`), so always solve the stride from the
count and the payload length rather than assuming a header size:

| type | count | header | stride | note |
|---|---|---|---|---|
| 1000042 | 6593 | 20 B | 48 B | first two words are `(wireFeatId, templateId)` |
| 1000044 | 71 | 16 B | 12 B | `[level 10..80][points][tsPoints]` |
| 1000511 | 95 | 20 B | 56 B | 5 races × 19 class slots, 14 × i32 each |
| 1000513 | 960 | 16 B | 24 B | 12 classes × 80 levels — decoded below |
| 1000514 | 0 | 16 B | — | empty (see below) |

String-bearing tables are **variable-width** - 1000517 (7 entries, 137 B) and 1000518 (38 entries, 1145 B)
do not divide evenly, so they cannot be walked by stride.

## 1000513 `RDBComboLookup_c` — the combo damage/stamina curve

960 rows of six little-endian u32, one per (class, level) over all 12 classes × levels 1..80. The client's
parser (`FUN_00846580`) reads all six words but keeps five — it consumes and **discards** the third, which is
zero in every shipped row — and divides words 4 and 5 by 100 on the way in. The five survivors become a
`std::map` keyed on `(class, level)`:

```
[u32 classId][u32 level][u32 discarded=0][u32 dpsModifier ×100][u32 secondary ×100][u32 staminaDivisor]
```

`classId` is the ordinary `ConanClass` byte (18 Barbarian … 44 Demonologist), which is what identifies the
table's shape without any string to anchor on.

**How the client uses it** — the lookup is `FUN_007d7dd0`, keyed on `(stat 0x43 = class,
min(comboLevel, stat 0x3C5 = ApprenticeLevel))`, and it logs
`GameCode/Combobook: MISSING combo lookup data for class %u, level %u.` on a miss:

| value | client's own name for it | used as |
|---|---|---|
| `dpsModifier` | "DPS modifier" (`FUN_007d7cc0`) | `dpsModifier/100 × BCTCombo::GetComboAnimationLength(combo) × 0.01` |
| `secondary` | — | parsed, cached, and **read by nothing on this build** |
| `staminaDivisor` | "stamina lookup duration" (`FUN_007d7ba0`) | `staminaCost = (int)(MaxStamina(0x1F9) × comboStaminaFactor × animLength ÷ staminaDivisor)` |

`FUN_007d5930` gates starting a combo on that stamina cost being `<=` current stamina (0x1F8), so the divisor
is a real gameplay constraint and not just a tooltip number. Both consumers substitute **1.0** and log a
`MISSING …` line when their value is `<= 0`.

What the data says:

- **`dpsModifier` is zero for all 80 levels of four classes** — Priest of Mitra, Tempest of Set, Necromancer,
  Demonologist. They have no combo scaling data at all and always take the 1.0 fallback. For the other eight it
  rises strictly with level (Barbarian 3.38 → 275.59, the steepest; Dark Templar 2.25 → 125.65, the flattest).
- **`staminaDivisor` is a step curve**: 32 at levels 1–9, dropping one per decade to 24 at level 80 — so a combo
  costs a rising fraction of your stamina bar as you level. A flat 20 for the four classes above.
- **`secondary` is non-zero only for the three soldier classes** and is exactly linear there: Guardian 500/level,
  Conqueror and Dark Templar 475/level. Since nothing reads it back, its meaning is open.

Shipped as `ClassComboScaling` (raw, unscaled — a consumer applies the ÷100 itself), imported by the opt-in
`rdb-combo-scaling` module and gated by `ClassComboScalingTests`.

## ⚠️ ARMOR_MITIGATION is empty

Type 1000514 is 16 bytes: `[1000514][0][1][0]` - a valid header declaring **zero entries**. The table ships
but carries no rows on this build.

This CONFIRMS #401's blocker rather than removing it. `CombatResolver` notes the client uses its closed-form
mitigation formula "when its ARMOR_MITIGATION table has no matching row"; there are no rows at all, so that
path is always taken, and the formula's `C1/C3/C4` coefficients really do have to come from the runtime
balance config (a live Frida dump). The table route is closed, and now provably so rather than by assumption.
