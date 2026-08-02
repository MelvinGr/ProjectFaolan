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
| 1065103 | `FUN_008379e0` | `PvPConRating_c` — a per-level con rating (decoded below) |

## Decoded: 1000505 `RDBPlayerClassData_c` — the class registry

Its parse callback names itself in an error string (`RDBPlayerClassData_c::PlayerClassMapDataReady(): To many
entries: `), which is also where the record shape comes from — `FUN_00708770`, reached from loader
`FUN_00708690`. After the 12-byte RDB-Id header: `[u32 count]` (asserted `< 1001`), then `count` records of
`[u32 classId][string name][u32 tier]`. Keys `0x1A` and `0x2A` are special-cased — the string is read into a
scratch and the value discarded.

17 records, and the ids are exactly our `ConanClass` values:

| tier | ids | |
|---|---|---|
| **1** — archetypes | `0x00`–`0x04` | Commoner, Soldier, Priest, Rogue, Mage |
| **3** — playable classes | `0x12` `0x14` `0x16` `0x18` `0x1C` `0x1D` `0x1F` `0x22` `0x27` `0x29` `0x2B` `0x2C` | Barbarian, Guardian, Conqueror, Priest of Mitra, Tempest of Set, Bear Shaman, Dark Templar, Assassin, Ranger, Necromancer, Herald of Xotli, Demonologist |

So this is the authoritative source for the class id space, and it confirms `ConanClass` independently: Barbarian
`0x12`, Assassin `0x22`, Necromancer `0x29`, Herald of Xotli `0x2B`, Demonologist `0x2C` all match.

⚠️ **The registry row above over-claims its range.** `1000505–1000508` is written as a span, but only **1000505**
carries this string-keyed shape. `1000506` is listed *again* on the `FeatClassTable_c` row and does not parse as
class data; `1000507` is a different shape entirely — plain `[u32][u32]` pairs whose second word is a class id and
whose first runs 1, 2, 3, 10, i.e. a race × class combination table (a lead, not a decode). Treat the loader→type
mapping as per-type, not as a range.

## Decoded: 1000507 — the RACE x CLASS combination table

Plain `[u32 raceId][u32 classId]` pairs after `[12-byte RDB-Id header][u32 count]`, and the arithmetic pins it:
`12 + 4 + 48*8 = 400` and `12 + 4 + 56*8 = 464`, the exact sizes of its two records. No strings, so it is NOT
the `RDBPlayerClassData_c` shape the registry's range implies.

Record 0 (48 pairs, four races) reads as the playable set:

| race | classes |
|---|---|
| **1** | Commoner, Soldier, Priest, Rogue, Barbarian, Guardian, Conqueror, Bear Shaman, Dark Templar, Assassin, Ranger |
| **2** | Commoner, Soldier, Priest, Rogue, Mage, Barbarian, Guardian, Conqueror, Priest of Mitra, Dark Templar, Assassin, Ranger, Necromancer |
| **3** | Commoner, Priest, Rogue, Mage, Tempest of Set, Dark Templar, Assassin, Ranger, Necromancer, Herald of Xotli, Demonologist |
| **10** | Commoner, Soldier, Priest, Rogue, Mage, Guardian, Bear Shaman, Dark Templar, Assassin, Ranger, Necromancer, Herald of Xotli, Demonologist |

Record 1 is a wider set including ids the class registry (1000505) does not name — `0x13`, `0x15`, `0x17`,
`0x19`, `0x1A`, `0x1B`, `0x1E`, `0x21`, `0x23`, `0x24`, `0x2A` — so it is probably the DESIGN-time table and
record 0 the shipped one. Undecided which the client actually uses. [V0]

### ⚠️ It disagrees with our `Race` enum, and we cannot settle it from our own data

`Faolan.Core/Enums/Race.cs` is `Aquilonian = 1, Cimmerian = 2, Stygian = 3`.

- **Race 3 is confirmed Stygian**, both ways: the table gives it Tempest of Set / Necromancer / Herald of Xotli /
  Demonologist, and all four shipped characters are race 3 carrying `0x22`/`0x29`/`0x2B`/`0x2C`, every one of
  which the table allows for race 3.
- **Races 1 and 2 are SWAPPED**, and our own hand-written table corroborates it. `StartingData
  .RaceArchClassCombinations` lists the class set per race, and matching those sets to 1000507's ignores the
  names entirely:

  | our label | our classes | 1000507 |
  |---|---|---|
  | Aquilonian | Priest of Mitra, Conqueror, Dark Templar, Guardian, Barbarian, Ranger, Assassin | **race 2** (+ Necromancer) |
  | Cimmerian | Bear Shaman, Conqueror, Dark Templar, Guardian, Barbarian, Ranger | **race 1** (+ Assassin) |
  | Stygian | Tempest of Set, Ranger, Assassin, Herald of Xotli, Demonologist, Necromancer | **race 3** (+ Dark Templar) |

  The sets we hand-authored are right; the ids the enum attaches to them are not. `Race.Aquilonian` should be 2
  and `Race.Cimmerian` 1.
- **A captured character confirms it, so this is settled and the enum has been flipped.** `Dikketest`
  (`Dikketest_rogue-barbarian_create`) is female, carries head mesh **1142847** and race byte **1** — and that
  head occurs in exactly one appearance list, the female heads under CHARACTERCREATOR's `<cimmerian>` element.
  That ties wire race 1 to Cimmerian without going through our enum at all. `CaptureCharacterImporterTests`
  asserted `Aquilonian` here purely because the expectation had been written from the enum; it is now the gate on
  the corrected value.
- **Race `10` is a fourth race we do not model at all** — Khitan, by its class set (Rise of the Godslayer).

## Decoded: the two GUILD tables (1000517 / 1000518)

Both parse callbacks assert `version == 1` and complain through the client's own `Community/Guild` channel on a
mismatch (`GuildGoverningFormDataTable::ReadRDBData: Unexpected version number %i.`), which is what confirms the
class each loader belongs to independently of the RTTI sweep.

Recovered with `tools/ghidra/DecompileWithCallees.java` (`FNS=0057e330,0057edb0 DEPTH=2`); both files carry the
usual 12-byte RDB-Id header, then `[u32 count][u32 version=1]`, then `count` records.

### 1000517 `GuildGoverningFormDataTable` — 7 records, `[u32 id][string]`

The seven guild governing forms, and the id is the one `CreateGuild(name, i_nGoverningForm, tax, lookingForMember)`
carries on the wire:

| id | 0 | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|---|
| | Cimmerian | Acheron | Stygian | Aquilonian | Nemedian | Hyborian Milita | Harem |

(`Hyborian Milita` is the client's own spelling.)

### 1000518 `GuildRankDataTable` — 38 records, `[u32 rank][u32 form][u32 limit][u32 permissions][string]`

The rank LADDER per governing form — the names the guild window shows. The object stores the four words
permuted (`[0]=form, [4]=rank, [8..35]=name, [36]=permissions, [40]=limit`, stride `0x2C`); the wire order is the
one above.

- **`form`** indexes 1000517 exactly: values 0–6, seven groups, no gaps.
- **`rank`** is a dense 0-based index within its form (Cimmerian 0–4, Acheron 0–5, …).
- **`permissions`** is a BITMASK, and the observed values are strictly NESTED —
  `0 ⊂ 30 ⊂ 286 ⊂ 6430 ⊂ 7518 ⊂ 0x7FFFFFFF` (each is a superset of the one before), which is what a rank ladder
  looks like. The top rank of **every** form carries `0x7FFFFFFF`, i.e. everything. Which bit is which right is
  not decoded.
- **`limit`** takes 0, 1, 5, 10, 20 and 1000 and reads as a per-rank member CAP with 0 meaning uncapped — six of
  the seven leader ranks carry 1. It is not clean, though: `Strategos` (Nemedian leader) carries 10 and `Madam`
  (Harem leader) carries 0, so either the reading is wrong or those two forms genuinely allow several leaders.
  [V0]

Cimmerian, in full, as the shape of every form:

| rank | limit | permissions | name |
|---|---|---|---|
| 0 | 0 | 0 | Comrade |
| 1 | 0 | 0 | Kinsman |
| 2 | 0 | 0 | Elder |
| 3 | 0 | 7518 | War Chief |
| 4 | 1 | `0x7FFFFFFF` | Chieftain |

The other six: **Acheron** Blood Initiate / Sacrificer Apprentice / Ritualist Acolyte / Infernal Adept /
Deathbringer Envoy / Chaos-lord · **Stygian** Initiate / Serpent Devotee / Disciple of Set / Emissary of Set ·
**Aquilonian** Citizen / Partisan / Stateman / Patrician / Ambassador / Potentate · **Nemedian** Aspirant /
Vassal / Squire / Knight / Commandant / Lord · **Hyborian Milita** Conscript / Chiliostys / Phalanx / Hyparchos /
Taxiarchos / Strategos · **Harem** Trollop / Harlot / Concubine / Courtesan / Madam.

Nothing reads this yet — our guilds carry a numeric `GuildRank` with no names and no per-form ladder. Importing
it is guild-subsystem work (#104).

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

Only 1000511 and 1000513 of the above are decoded past their shape; the rest are counts and strides only. Open
work is tracked in Gitea #469.

## 1000511 `HitsManaAndStamina_c` — the class BASE vitals curve

95 records of 14 × i32 (5 races × 19 class slots), 20-byte header with the count in the FIFTH word. Words `[0]`
and `[1]` are the race and the class; `[2..4]` are the health/mana/stamina **base** in hundredths and `[5..7]`
their **growth** in thousandths. The remaining six (`[8..13]`) are the **attribute weights** — see below. Dump it
with `--report rdb-class-vitals`.

```
base(level) = baseWord + growthWord * (level - 1) / 10
```

The `/10` is because growth is stored one decimal finer than the base. The result is already in the wire's ×100
units, so it is the value that goes on the wire unscaled.

**This is retail's own number, verified rather than fitted.** It reproduces the max in **all 16** captured retail
level-up pushes — the `0x96B8DC59` stat block that follows a `NewLevelIIR` — exactly, across six classes and
levels 2–5, including the Guardian's non-integral stamina (`172.5 + 25.3 = 197.8` → the captured `19780`).
[V3: ClassBaseVitalsCurveTests.ClassBase_ReproducesTheCapturedRetailLevelUpPush]

| class | health | /level | mana | /level | stamina | /level |
|---|---|---|---|---|---|---|
| Guardian | 120 | 40 | — | — | 172.5 | 25.3 |
| Conqueror | 160 | 36 | — | — | 172 | 25 |
| Dark Templar | 120 | 37 | 275 | 15 | 172 | 25 |
| Assassin | 90 | 31 | — | — | 230 | 23 |
| Barbarian | 110 | 37 | — | — | 172 | 28 |
| Ranger | 120 | 26 | — | — | 207 | 28 |
| Priest of Mitra | 80 | 10 | 165 | 28 | — | — |
| Tempest of Set | 80 | 10 | 165 | 28 | — | — |
| Bear Shaman | 100 | 20 | 165 | 26 | 172 | 17 |
| Herald of Xotli | 80 | 33 | 240 | 30 | 115 | 13 |
| Necromancer | 80 | 29 | 240 | 30 | — | — |
| Demonologist | 80 | 29 | 240 | 30 | — | — |

An em dash is a pool the class does not have — the base is a literal 0 and the client hides that bar.

The table is keyed by (race, class) and is race-INVARIANT with one exception: race 4's Guardian carries a health
growth of **59** where every other race carries 40. Nothing else differs across the five races.

⚠️ **The base is NOT the displayed maximum.** Retail routinely sends a base *below* the current in the same
packet, and the client tops it up: an L23 Assassin whose character sheet reads 1270 health has a class base of
772. So this curve is one term of the sum, not the sum, and swapping it in as the max would LOWER every
character's ceiling. `CharacterMaxStats.ClassBaseHundredths` exposes it and says so; the whole sum is
`CharacterMaxStats.For`.

Independent corroboration that this word IS the same quantity our snapshot has always carried as the "HP base"
slot: all twelve of `CharacterMaxStats`'s hand-anchored level-1 HP-base values, read off retail login snapshots
long before this table was decoded, equal this table's health base exactly.

### Words `[8..13]` — the attribute weights

The trailing six are the per-attribute weights in **thousandths**, one column per attribute a pool scales off:
health takes one (`8000` for every class), mana two (`3000,3000`), stamina three (`2000,2000,2000`), and a class
without a pool carries zeroes in that pool's columns (an Assassin's row is `8000,0,0,2000,2000,2000`). A term is
`weight × attributeScore / 10`, which lands in the same hundredths as the base.

```
pool(level) = base(level) + Σ weight[i] × attribute[i] / 10 + the class ClassAttributeBases pool row
```

✅ **Both the sets and the column ORDER come from the client (#587), not from inference.** The table alone shows
neither, because every class's weights are equal within a pool. But each pool has its own accessor, and the stat
ids it reads *are* its attribute set:

| accessor | reads | lookup | returns |
|---|---|---|---|
| `FUN_0111aa30` health | `0x326` | `FUN_0113e730` | `w[8]/10 × Constitution` |
| `FUN_0111ab60` mana | `0x328`, `0x32A` | `FUN_0113e7d0` | `w[9]/10 × Intelligence + w[10]/10 × Wisdom` |
| `FUN_0111ad20` stamina | `0x324`, `0x326`, `0x32E` | | Strength + Constitution + Dexterity |

Each accessor converts a raw stat to a display score the same way — `(value − 10) × 0.1`, the class row's flat 10
wire being 1 display — and divides the result by 100 on the way out.

So **health = Constitution**, **mana = Intelligence + Wisdom**, **stamina = Strength + Dexterity + Constitution**,
and the weight columns are `[8]` Constitution, `[9]` Intelligence, `[10]` Wisdom.

⚠️ Mana used to be the one term with slack, and this is what removed it: every captured caster happens to carry
equal Wisdom and Dexterity (110/110 on both level-80 mages, 33/33 on the Herald), so the corpus could not separate
"mana scales off Wisdom" from "mana scales off Dexterity" — and there is no `0x32E` anywhere in the mana accessor.
The captures still corroborate the sets independently (AssassinL23's 448-point health gap is 8 × 56 exactly; their
310-point stamina gap is 2 × 155, the only triple of the five that makes it).

**Verified against every anchor we hold.** All twelve classes' level-1 character sheets (where the attribute base
is a flat 1, so each pool is base + 8 / + 6 / + 6 plus the `ClassAttributeBases` HealthMax row — Guardian 130,
Conqueror 168, Dark Templar 128/281/178, Assassin 98/236, Barbarian 118, Ranger 133, the priests 88/171, Bear
Shaman 108/171/178, Herald 89/246/121, the mages 89/246, including the Guardian's 178.5 stamina rounding to the
captured 179); and, at level, the four captured characters' world-entry pushes — AssassinL23 (Assassin L23)
1270/1148/74, Hakotep (Herald L24) 1414/759/1584, NecroL80 (Necromancer L80) 6341 health / 6523 mana, DemoL80
(Demonologist L80) 5830 mana. Ten of the eleven measurable pools land exactly; the eleventh (DemoL80's health) is
177 low because our item data for that character is short.

⚠️ That 177 is now narrowed, and it is not one thing (#588). `ActiveWeaponSet` is never set by the character
importer, so every imported character defaults to `Primary` — and DemoL80's SECONDARY set (Jagged Cut +12
Constitution and Fetish of Caged Fiends +35, against Mindraker's +34) is worth 13 more Constitution, i.e. 104 more
health, taking the shortfall to **73**. The remaining 73 is **not** Constitution — 73 is not a multiple of the
health weight 8, so no attribute total produces it — so it is a flat `HealthMax` record our item data does not
carry. It is not the vanity slots either, which is worth recording because they look like the obvious candidate:
DemoL80's Canvas Belt (+4 Con) and Northfur Cloak (+23 Con) would add 216 health, overshooting by 143, so excluding
them is right. Feats were ruled out separately (#589).

Which weapon set was actually drawn is **not recoverable from the corpus**: the swap is observable (the server
stamps the newly-active main slot on a `SwitchWeaponsButton`) but a login carries no such event, and the client
derives its own display from the equipment container. So the 177 → 73 is arithmetic that fits, not a measurement.
[V3: CharacterMaxStatsTests.EveryClass_AtLevelOne_ReproducesItsCapturedSheet]
[V3: EntityVitalsGearTests.AssassinL23_AtLevel23_MatchesTheCapturedRetailPools]

This is also what explains the old hand-read offsets: the mana and stamina bases sat "6 below the sheet" for
every class because at level 1 every attribute is 1, and 3+3 and 2+2+2 are both 6.

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

## Decoded: 1065103 `PvPConRating_c` — a per-LEVEL con rating, and the shipped file neutralises itself

200 records of `[u32 level][u32 conRating]` after the ordinary 16-byte header (`[1065103][1][1][200]`). The parse
callback `FUN_00837a40` (from loader `FUN_008379e0`) names the first word itself, in the client's own error string:

```
"Tried to load PvPConRating data for an invalid level: %d"     CoreCharacter\Data\PvPConRating.cpp:0x23
```

It bounds that word to `1..512` and stores the second one **indexed by it**, into a flat array:

```c
if (level - 1U < 0x200)  vector[level - 1] = conRating;      // else the error above
```

⚠️ **Each level appears TWICE, and the second entry wins.** The file is interleaved per level — first a
decade-BANDED value, then a flat `9`:

| levels | banded value (first entry) | second entry |
|---|---|---|
| 1–19 | 4 | 9 |
| 20–29 | 5 | 9 |
| 30–39 | 6 | 9 |
| 40–49 | 7 | 9 |
| 50–59 | 8 | 9 |
| 60–100 | 9 | 9 |

Because the store is indexed rather than appended, the flat pass overwrites the banded one, so **on this build the
loaded table is a constant 9 for every level 1–100**. The banded curve ships but is dead. Whether that is intended
(the band is legacy, disabled by shipping a flat second pass) or a data bug is not decidable from the file. [V0]

**No consumer is reachable statically.** The table's singleton (`DAT_03c03114`) is referenced by exactly two
functions — this parse callback and the singleton's own constructor — so every reader goes through an accessor on
the object rather than touching the global, and the RTTI route that named the class does not reach them. What the
rating MEANS is therefore open; the name plus a per-level array of 4–9 reads like the level spread over which PvP
counts, but that is a reading, not a decode.

Nothing imports it, deliberately: a table whose effective contents are one constant and whose consumer is unknown
would be a column nothing could act on. Reproduce with:

```zsh
dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1065103 --out <dir> --RdbDir <rdb-dir>
```

## ⚠️ ARMOR_MITIGATION is empty

Type 1000514 is 16 bytes: `[1000514][0][1][0]` - a valid header declaring **zero entries**. The table ships
but carries no rows on this build.

This CONFIRMS #401's blocker rather than removing it. `CombatResolver` notes the client uses its closed-form
mitigation formula "when its ARMOR_MITIGATION table has no matching row"; there are no rows at all, so that
path is always taken, and the formula's `C1/C3/C4` coefficients really do have to come from the runtime
balance config (a live Frida dump). The table route is closed, and now provably so rather than by assumption.
