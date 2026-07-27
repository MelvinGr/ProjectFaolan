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

The loader address finds the table but does not name it. The RTTI walk closes that: an MSVC vtable is preceded
by a pointer to its Complete Object Locator, whose `+0xC` is the type descriptor, whose `+8` is the decorated
name. So going the other way — find the data word holding the loader's address, walk back to the head of that
vtable, read the COL — names the class. `RDBComboLookup_c` (1000513) was recovered exactly this way: its vtable
at `0207a534` carries `0x846490` in slot 11, and the COL one word before it resolves to `.?AVRDBComboLookup_c@@`.

Both halves together are one script, and it names the whole set in a single run:

```
HELPER=104d760 analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe \
  -noanalysis -scriptPath tools/ghidra -postScript DumpDataTableRegistry.java
```

⚠️ `LoadStaticData` is virtual on some of these classes and not on others. It IS a vtable slot on
`RDBComboLookup_c`; on the two tables the sweep cannot name it is not, and dumping their vftable shows only the
`DataTable_c<T>` scaffolding. So the vtable is a route in, not *the* route in — a miss there says nothing.

## Registry

Every row below is from that one sweep, so the class names are the client's own RTTI rather than an inference
from what the table appears to hold.

| RDB type | loader | class |
|---|---|---|
| 1000042 | `FUN_00788e20` | `Feat_t` — the wire-id ↔ template-id map + prereq graph (#467) |
| 1000043, 1000099, 1000506, 1065101 | `FUN_0078de40` | `FeatClassTable_c` |
| 1000044 | `FUN_00844890` | `FeatPointsTable_c` — the FEATPOINTS per-level table |
| 1000071 | `FUN_00d8fab0` | `TexScriptMeshGroupValues_c` (named elsewhere; not in a vtable) |
| 1000076 | `FUN_0103f2d0` | `RDBMorphNameIDs_c` — the morph-target registry |
| 1000080, 1000081 | `FUN_0100d220` | **(unnamed)** — not in a vtable, so RTTI cannot reach it |
| 1000505–1000508 | `FUN_00708690` | `RDBPlayerClassData_c` |
| 1000509, 1000510, 1000512 | `FUN_00911930` | `PlayfieldPvPData_c` |
| 1000511 | `FUN_0113e170` | `HitsManaAndStamina_c` — the per-class/level vitals curve (#3) |
| 1000513 | `FUN_00846490` | **`RDBComboLookup_c`** — the per-class/level combo DPS + stamina curve (see below) |
| **1000514** | `FUN_0145eb70` | **`ArmorMitigation_c`** (#401) — **EMPTY on this build** |
| 1000517 | `FUN_0057e330` | `GuildGoverningFormDataTable` |
| 1000518 | `FUN_0057edb0` | `GuildRankDataTable` |
| 1000520 | `FUN_00813d20` | `CoreTokenHolder` |
| 1000521 | `FUN_013b1a10` | `GuildRenownRewardDataTable` |
| 1000625 | `FUN_00847890` | `TokenStateTable` |
| 1000626 | `FUN_006cb5f0` | `CoreChatFilterDataTable` |
| 1000627 | `FUN_013b21c0` | `GroupFinderActivitiesTable` |
| 1065103 | `FUN_008379e0` | `PvPConRating_c` |

Two corrections the sweep made to the names this table used to carry, both from guessing at a loader by what it
sat next to: 1000042 is `Feat_t`, not `FeatTable_t`, and 1000044 is `FeatPointsTable_c` — the `FeatClassTable_c`
name belongs to the 1000043 loader, not to it, so there was never anything ironic about a "class table" holding
feat points.

`RDBComboLookup_c` is the only `*Lookup_c` in the binary; the rest of the `RDB*` RTTI names (there are ~70) are
asset classes — meshes, textures, playfields, heightmaps — not data tables.

## Record layout

The first three words are always `[u32 type][u32 id][u32 version]`, and for most tables the fourth is the entry
count — a **16-byte** header, then `count` entries:

```
[u32 type][u32 id][u32 version][u32 count]  then count entries
```

⚠️ The header is not uniformly 16 bytes and the count is not always the fourth word. Four tables carry one
extra word AFTER the count (a 20-byte header), and 1000099 and 1000511 put the count in the FIFTH word instead
(1000511's header reads `[1000511][0][1][2][95]`). So always **solve** the shape — the one interpretation where
`header + count × stride == length` with a sane stride — rather than assuming a header size:

| type | class | count | header | stride | note |
|---|---|---|---|---|---|
| 1000042 | `Feat_t` | 6593 | 20 B | 48 B | first two words are `(wireFeatId, templateId)` |
| 1000043 | `FeatClassTable_c` | 9916 | 20 B | 40 B | |
| 1000044 | `FeatPointsTable_c` | 71 | 16 B | 12 B | `[level 10..80][points][tsPoints]` |
| 1000071 | `TexScriptMeshGroupValues_c` | 1220 | 20 B | 8 B | |
| 1000080 | (unnamed) | 8 | 16 B | 8 B | |
| 1000081 | (unnamed) | 100 | 16 B | 12 B | |
| 1000099 | `FeatClassTable_c` | 3096 | 20 B | 12 B | count is the FIFTH word |
| 1000505 | `RDBPlayerClassData_c` | 17 | — | — | does not divide — variable-width |
| 1000506 | `RDBPlayerClassData_c` | 27 / 16 | 16 B | 8 B | two entries (id 0 and id 1) |
| 1000507 | `RDBPlayerClassData_c` | 48 / 56 | 16 B | 8 B | two entries |
| 1000508 | `RDBPlayerClassData_c` | 109 | — | — | does not divide — variable-width |
| 1000509 | `PlayfieldPvPData_c` | 243 | 20 B | 12 B | |
| 1000510 | `PlayfieldPvPData_c` | 13 | 16 B | 84 B | |
| 1000511 | `HitsManaAndStamina_c` | 95 | 20 B | 56 B | 5 races × 19 class slots, 14 × i32 each; count is the FIFTH word |
| 1000512 | `PlayfieldPvPData_c` | 5 | 16 B | 8 B | |
| 1000513 | `RDBComboLookup_c` | 960 | 16 B | 24 B | 12 classes × 80 levels — decoded below |
| 1000514 | `ArmorMitigation_c` | 0 | 16 B | — | empty (see below) |
| 1000520 | `CoreTokenHolder` | — | — | — | the count word is not a count — some other shape entirely |
| 1000625 | `TokenStateTable` | 1 | 16 B | 4 B | |
| 1065101 | `FeatClassTable_c` | 49 | 16 B | 16 B | |
| 1065103 | `PvPConRating_c` | 200 | 16 B | 8 B | |

String-bearing tables are **variable-width** - 1000517 (7 entries, 137 B), 1000518 (38 entries, 1145 B) and
1000626 (9921 B) do not divide evenly, so they cannot be walked by stride.

Only 1000513 of the above is decoded past its shape; the rest are counts and strides only. Open work is tracked
in Gitea #469.

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
