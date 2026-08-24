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

**And a miss there is not the end of it.** The two tables RTTI cannot reach (1000080 / 1000081) were named from
their parse callbacks' ASSERTS instead, which carry the client's own source path (`RDBHairReplacement.cpp`). Every
`FUN_004a6a30(msg, "<file>.cpp", <line>, ...)` call is an assert site, and the file name is the class. That route
works wherever a callback validates anything, which most of them do.

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
| 1010203, 1010501 | `FUN_00603d70` | **`BCCTree_c`** — the behaviour-control-centre trees. ⭐ **1010203 is plain-text XML**, 1010501 its merged binary twin; `Load` tries the binary and falls back to the text. 29 trees, `<state>`/`<substate>` with `id`/`name`/`priority` — the substate table the BCC IIR's wire phases `0x0B`/`0x0C` index into (#810, #812, `--report bcc-trees`) |
| 1000080, 1000081 | `FUN_0100d220` | **`RDBHairReplacement`** — named from its asserts' source path, not RTTI (decoded below) |
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
| 1000627 | `FUN_013b21c0` | `GroupFinderActivitiesTable` — the group-finder activity catalog (decoded below) |
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

## Decoded: 1000508 — the CREATURE RACE table

The 109 names behind `CharacterStats.race`, which every NPC carries as a bare number (`NpcStatCore.Race`,
`NpcSpawn.Race`). A different domain from the three-value player `Race` enum above. Dump with
`--report creature-races`.

```
[u32 type][u32 id][u32 version][u32 count]        count = 109
count × [u32 raceId][u32 nameLength][utf8 name]   the id PRECEDES its name
```

Parses to EOF in exactly 109 entries with zero trailing bytes, and the ids are a dense `1..109` with no gaps.

### ⚠️ The first decode of this was wrong, and it parsed cleanly

Worth recording, because the failure is not one a "does it parse?" check can catch. Reading the entry as
`[nameLength][name][raceId]` from offset 20 also runs to EOF and also yields sensible-looking `id → name` pairs —
but it pairs every name with the id of the entry **after** it, shifting the whole table by one row (`Crocodile`
took Hyperborian's 9, `Dog` took Living Plant's 66). It additionally swallowed the first entry's `raceId` (96,
Panther's) as the record's count, which is where the since-retracted claim that "the declared count under-reports
by 12, parse to EOF rather than to the count" came from. The count was a fact all along; the anomaly was ours.

**What settled it is an external join, not the parse.** A `Race`-category spawn group in RDB 1010506 carries a
numeric race id *and* a human-readable group name, from different places in that record, so folding the name onto
a race name and comparing ids can falsify a grammar:

| grammar | entries | groups whose declared id matches their own name |
|---|---:|---|
| `[nameLength][name][raceId]` @20 | 108 | **0 / 872** |
| `[raceId][nameLength][name]` @16 | 109 | **844 / 872 (96.8%)** |

[V1: Faolan.Importer/Cli/ReportCli.CreatureRaces.cs — `Badger 1` declares 79 = Badger, `Chicken 1` declares
61 = Chicken, `Rabbits` declares 72 = Rabbit, `Rats` declares 56 = Rat, `Elk 4` declares 74 = Elk. The report
prints this cross-check and the declared-count/trailing-byte agreement on every run.]

The 28 residual disagreements are genuine content, not decode error — a group named for one creature tagged with
a neighbouring race (`Deer 1` declares 74 = Elk).

**It is not the creature catalog #518 wants**: a race carries no level, health, faction, name or equipment, so it
cannot stand a creature up. What it is is the vocabulary the open-world spawn groups are named in — see
[npc-spawn-layout-1010506.md](npc-spawn-layout-1010506.md).

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
  looks like. The top rank of **every** form carries `0x7FFFFFFF`, i.e. everything.

  ⭐ **And the bits are named** (#799). The client's guild-management view builds its permission list by pairing
  each bit with the LDB token that labels it, handing the bit itself to `com.GameInterface.Guild.GuildPermission`
  as `m_permissionId` — so the constant and the name come out of one instruction window:

  | bit | right | | bit | right |
  |---|---|---|---|---|
  | `0x001` | `CanDisbandGuild` | | `0x080` | `CanChangeGoverningForm` |
  | `0x002` | `CanInviteOthers` | | `0x100` | `CanChangeMotd` |
  | `0x004` | `CanKickOthers` | | `0x200` | `CanChangeName` |
  | `0x008` | `CanPromote` | | `0x400` | `CanChangeRecruitmentMode` |
  | `0x010` | `CanDemote` | | `0x800` | `CanChangeVulnerabilityWindow` |
  | `0x040` | `CanWithdrawMoney` | | | |

  [V1: tools/ghidra/DumpGuildRankPermissions.java]

  ⚠️ `0x020` is **absent from the list and set in the shipped masks**, so it is a right the management window does
  not render rather than an unused bit — as is everything above `0x800` inside the leader's `0x7FFFFFFF`. Do not
  read an unnamed bit as unused.

  Crossed against the masks, `WithdrawMoney` is in `7518` and `0x7FFFFFFF` and in neither `6430` nor below: **the
  top two rungs may withdraw on six forms, and on Stygian the leader alone** (its second rung is `6430`).
  Invite/kick/promote/demote are in every non-zero mask, so they reach one to two rungs further down. Transcribed
  into `Faolan.Core.Constants.GuildRankLadder` and enforced by `GuildService`.
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

`GuildRankLadder` carries the names and the permission column; the `limit` column is still unread (nothing
enforces per-rank member caps).

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
| 1000042 | `Feat_t` | 6593 | 20 B | 48 B | 12 words, 2 of them read and DISCARDED — decoded below |
| 1000043 | `FeatClassTable_c` | 9916 | 20 B | 40 B | |
| 1000044 | `FeatPointsTable_c` | 71 | 16 B | 12 B | `[level 10..80][points][tsPoints]` |
| 1000071 | `TexScriptMeshGroupValues_c` | 1220 | **20 B** | 8 B | `[i32 groupValue][u32 meshRdbId]` — decoded below |
| 1000080 | `RDBHairReplacement` (default) | 8 | 16 B | 8 B | |
| 1000081 | `RDBHairReplacement` (override) | 100 | 16 B | 12 B | |
| 1000099 | `FeatClassTable_c` | 3096 | 20 B | 12 B | count is the FIFTH word; the feat POINT COST table — decoded below |
| 1000505 | `RDBPlayerClassData_c` | 17 | 16 B | — | variable-width `[id][len][name][tier]`, 380/380 B — decoded below |
| 1000506 | `FeatClassTable_c` (not `RDBPlayerClassData_c`) | 27 / 16 | 16 B | 8 B | the archetype → class MEMBERSHIP table — decoded below |
| 1000507 | `RDBPlayerClassData_c` | 48 / 56 | 16 B | 8 B | two entries |
| 1000508 | the CREATURE RACE table (not `RDBPlayerClassData_c`) | 109 | 16 B | — | variable-width: `[id][len][name]`, decoded above |
| 1000509 | `PlayfieldPvPData_c` | 243 | 20 B | 12 B | |
| 1000510 | `PlayfieldPvPData_c` | 13 | 16 B | 84 B | |
| 1000511 | `HitsManaAndStamina_c` | 95 | 20 B | 56 B | 5 races × 19 class slots, 14 × i32 each; count is the FIFTH word |
| 1000512 | `PlayfieldPvPData_c` | 5 | 16 B | 8 B | |
| 1000513 | `RDBComboLookup_c` | 960 | 16 B | 24 B | 12 classes × 80 levels — decoded below |
| 1000514 | `ArmorMitigation_c` | 0 | 16 B | — | empty (see below) |
| 1000520 | `CoreTokenHolder` | 29 | 14 B | variable | the count is a BYTE, not a word — decoded below |
| 1000625 | `TokenStateTable` | 1 | 16 B | 4 B | one record, and its only column is `0` — no content on this build |
| 1065101 | `FeatClassTable_c` | 49 | 16 B | 16 B | |
| 1065103 | `PvPConRating_c` | 200 | 16 B | 8 B | |

String-bearing tables are **variable-width** - 1000517 (7 entries, 137 B), 1000518 (38 entries, 1145 B), 1000520
(29 entries, 760 B) and 1000626 (9921 B) do not divide evenly, so they cannot be walked by stride.

Decoded past their shape: 1000042 (layout), 1000043, 1000071, 1000099, 1000505, 1000506, 1000507, 1000508, 1000509, 1000510, 1000511, 1000512, 1000513,
1000517, 1000518, 1000520, 1000521, 1000626, 1000080/1000081, 1065101 and 1065103 - see the sections below. The rest are counts and strides only, and open
work is tracked in Gitea #469.

⭐ **`--report data-table --report-arg <type>[:<id>]` renders any of the fixed-width ones as ROWS**, with
`raceId`/`classId`/`archetypeId` columns resolved to names through 1000508 and 1000505. It SOLVES the count word
and the header length rather than assuming them, because the two vary independently: most tables are
count-in-word-3 with a 16-byte header, 1000511 is count-in-word-4 with 20, and **1000071 is count-in-word-3 with
20**. ⚠️ A table where no candidate divides the body is REFUSED rather than rendered - 1000505 does not divide
because it is variable-width, and printing it as fixed-width rows yields a plausible table of nonsense.

## Decoded: 1000071 `TexScriptMeshGroupValues_c` — a mesh GROUP membership list

1220 rows of `[i32 groupValue][u32 meshRdbId]` after a **20-byte** header. The second column resolves in the RDB
index as type **1010001** (meshes) - checked, not assumed. The first is a small NEGATIVE i32, and it is the
*group*, not a per-mesh property: **890 distinct meshes over 1220 rows, and 224 of them appear under more than one
value** (`793114` under -1, -3 and -4). So the table is a group → members adjacency list, the same shape 1000506
has for archetypes.

| group | meshes |
|---:|---:|
| -4 | 649 |
| -3 | 256 |
| -1 | 199 |
| -2 | 70 |
| -5 | 45 |
| 0 | 1 |

⚠️ **What the five groups MEAN is not decoded** - that needs the consumer side (the TexScript the class is named
for), which is this page's step-3 method rather than its step-1 one.

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

Which weapon set was actually drawn is **not recoverable from the corpus**, and that is now MEASURED rather than
asserted (`--report weapon-set-drawn`, #588). The swap is observable — the server stamps the newly-active main slot
on a `SwitchWeaponsButton` — but a login carries no such event, and the obvious second route does not work either:
a spawn's worn list is what every other client renders the character from, so if it carried one set at a time it
would state the answer. It does not. Across the 10,990 captured spawns carrying a weapon slot, **138 carry a
primary (6/8) and a secondary (20/21) at once**, and DemoL80's own spawns are among them (`[6, 20, 21]`). The list
is the whole equipment container, exactly as the client-side-display reading says.

⚠️ The same report explains why NecroL80 landing exactly validates nothing: **NecroL80's spawns carry slot 6 and
nothing else**, so she owns no second set and the `Primary` default is right for her by construction. So the
177 → 73 is arithmetic that fits, not a measurement.
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
| `dpsModifier` | "DPS modifier" (`FUN_007d7cc0`) | `dpsModifier/100 × BCTCombo::GetComboAnimationLength(combo) × 0.01` — a **tooltip** number, see below |
| `secondary` | — | parsed, cached, and **read by nothing on this build** |
| `staminaDivisor` | "stamina lookup duration" (`FUN_007d7ba0`) | `staminaCost = (int)(MaxStamina(0x1F9) × comboStaminaFactor × animLength ÷ staminaDivisor)` |

`FUN_007d5930` gates starting a combo on that stamina cost being `<=` current stamina (0x1F8), so the divisor
is a real gameplay constraint and not just a tooltip number. Both consumers substitute **1.0** and log a
`MISSING …` line when their value is `<= 0`.

⚠️ **The two halves are not equally load-bearing, and the call graph says which is which.** `FUN_007d7cc0` (the DPS
chain) has **exactly one caller** — `FUN_007d6c10`, which computes it, computes the stamina cost beside it, and
hands both to `FUN_005efbc0`, the builder that emits the `ComboStarter` / `ComboFinisher` description lines; and
`FUN_007d6c10`'s own callers are the combo-book list and `GetComboTooltip`. So the DPS number is **displayed, never
applied**: nothing in the client spends it on a hit. `FUN_007d7ba0` (the stamina chain) has **seven** callers,
including the start gate above. The multiply itself is `dpsModifier(cached, already ÷100) × animLength × 0.01`,
with both float constants read straight off the binary (`0x01f27500` = 1.0 fallback, `0x01f2800c` = 0.01) — which
also settles the arithmetic the two-way "/100 … ×0.01" phrasing left ambiguous. Combo DAMAGE is the server's, and
in our implementation it is the combo's own spell (`Combo.GameActionId`, see `docs/subsystems/combos.md`).

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

## Decoded: 1000080 / 1000081 — `RDBHairReplacement`, the two tables RTTI could not name

The registry lists these as **(unnamed)** because their loader is not in a vtable, so the RTTI walk cannot reach
them. The parse callbacks name the class anyway — they carry the client's own **source file** in their asserts:

```
"nHairType < uint32(nRows)"  /  "Illegal read of hair type.  Hairtype should be sequential"
        RDBHairReplacement.cpp:0x2c
"ReadDefaultFromRDB must be called before this function"
        RDBHairReplacement.cpp:0x36
```

That second assert also orders them: **1000080 is the DEFAULT table and is read first**, 1000081 is the override.

### 1000080 — the per-hair-type default, 8 records of `[u32 hairType][u32 replacementMesh]`

`FUN_0100d330` pre-sizes a vector to the record count, then per record reads the hair type, bound-checks it against
that count (the first assert above) and stores the second word at that index. Eight hair types, 0–7, and the file
lists them out of order (6, 5, 7, 4, 1, 2, 3, 0) — which is exactly why the store is indexed rather than appended:

| hair type | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| replacement | `0xFFFFFFFF` | 0 | 208255 | 0 | `0xFFFFFFFF` | 0 | 208255 | 0 |

208255 resolves to a **type 1010001 RenderMesh** (`--report rdb-id 208255`), i.e. a real geometry blob, so the
non-zero entries name an actual replacement mesh. What 0 and `0xFFFFFFFF` distinguish is not decoded — "leave the
hair alone" and "hide it entirely" is the obvious reading of a hair-replacement table, and is a reading. [V0]

### 1000081 — the per-head override, 100 records of `[u32 hairType][u32 headAsset][u32 replacementMesh]`

`FUN_0100d6c0` reads THREE words per record and stores only the third, into a container keyed on the first two —
a `map[(hairType, headAsset)] = replacementMesh`. Every shipped row carries hair type **3**, so only one hair type
has overrides at all.

Both id columns resolve to RDB assets: the replacement is always a **1010001 RenderMesh** (566205, 565876, 208252,
3068336), and the head asset is either a 1010001 mesh or a **1010030** look (4188728, 4189150, 3394697 …).

So the subsystem is "what your hair turns into when something is on your head" — the standard helmet-flattens-hair
mechanism — with a default per hair type and a table of exceptions per (hair type, head asset).

Nothing imports it. It is client-side appearance: the client already loads both tables and owns the mesh swap, and
the server never names a hair mesh. Recorded here because #469 asks what the tables ARE, and because these two were
the only ones with no name at all.

## ⭐ Decoded: 1065101 + 1000043 `FeatClassTable_c` — the feat-tree registry, and the ALTERNATE ADVANCEMENT trees

Two tables of the `FeatClassTable_c` cluster, and together they are the **whole feat-tree structure** — including
the AA trees Gitea #150 had been unable to find anywhere.

```
1065101   49 records   [u32 treeId][u32 classId][u32 0][u32 0]
1000043 9916 records   [rowId][classId][level][wireFeatId][autotrain][treeId][column][flag][tier][pointsRequired]
```

⚠️ **1000043's columns were mis-assigned here until #862**, and the mistake mattered: word 0 was read as the
feat id and word 3 as a "line id". Word 0 is the row's OWN id — which is why it "matches neither `Feats.Id` nor
`FeatIdMaps.WireFeatId`, 0 of 9,916" — and **word 3 is the wire feat id**, resolving on 9,907 of 9,916. Word 4
was read as a constant zero because the AA rows this section was written from are uniformly trainable; over the
whole table it is the **auto-train flag** (`FeatClassEntry.Autotrain`, true on 3,839 rows). See
[feats.md](../subsystems/feats.md) for the full column table and what the flag settles.

`1065101` is the **registry**: 48 trees plus one zero row, and exactly **4 trees per class** across the twelve
playable `ConanClass` values. `1000043` is the **membership**, and its own `classId` agrees with the registry's
on every row.

### ⭐ Twelve of the 48 are the AA trees, and they separate on three independent axes

| | the other 36 | trees `1000177`–`1000190` |
|---|---|---|
| rows | 54–88 | **240**, every one |
| line ids | 120,000–431,901 | **≥ 1,020,000** |
| level gates | 10 / 15 / 20 / 25 / 30 | **20 and 80** |

⭐ **80 is the level cap**, which is exactly what post-80 progression means — and there are twelve of them, one
per playable class:

```
1000177 DarkTemplar   1000178 Guardian     1000179 Conqueror    1000180 Barbarian
1000181 BearShaman    1000182 Demonologist 1000183 Assassin     1000184 Necromancer
1000186 TempestOfSet  1000188 HeraldOfXotli 1000189 PriestOfMitra 1000190 Ranger
```

### ⭐⭐ And each AA tree's 240 lines reproduce the client's three PERK LINES, from the data

The client asserts `"PerkLine is not 1, 2 or 3 on spell id %d"` and names them `PerkGeneral` / `PerkArchetype` /
`PerkClass` (#150). Bucketing each tree's line ids by prefix and asking **who else has the same set** produces
exactly that, with no fitting:

| bucket | lines | shared by | reads as |
|---|---:|---|---|
| `144xxxx` | **105** | all **12** classes | `PerkLine 1` — **General** |
| `140xxxx` | **62** | the **3** classes of one archetype — Assassin/Barbarian/Ranger, Conqueror/DarkTemplar/Guardian, BearShaman/PriestOfMitra/TempestOfSet, Demonologist/HeraldOfXotli/Necromancer | `PerkLine 2` — **Archetype** |
| `10xxxxx` | **73** | **one** class (`102xxxx` Guardian, `107xxxx` DarkTemplar, `112xxxx` Barbarian, …) | `PerkLine 3` — **Class** |

`105 + 62 + 73 = 240`. ⭐ The four archetype groups are AoC's own Rogue / Soldier / Priest / Mage, three classes
each, recovered here from nothing but which line ids two trees have in common.

⚠️ **Which bucket is PerkLine 1 versus 3 is an inference** from the client's naming (`1 = PerkGeneral`,
`3 = PerkClass`) laid over the sharing structure. The structure is measured; the numbering is read onto it.

⭐ **The perk names ARE reachable** — this section used to say they were not, on the mis-assigned column above.
Resolving word 3 through `FeatIdMap` names **all 2,880** AA rows (268 distinct perks), and words 8/9 place each
one in its tree: `Electric Wards`, `Fire Wards` and `Pressing Strikes` sit at tier 1 columns 2/3/4 of the
Necromancer's `1000184`, five ranks each. RDB **1000099** ("feat point cost info") then gives each of those rows
its point cost — 2,880 placements, priced in the two AA point pools at 1..50 — and it covers the AA trees and
*nothing else*. All of that is imported: `FeatClassEntry` for the placement, `FeatPerkCost` for the price. So
#150 has the tree structure, the names, the tier gates and the costs; what it does not have is a capture of the
AA UI or of a perk being bought.

### ⭐ And the AA CURRENCY is named too — by the tuning sheet

The half the tree tables do not carry is how a perk is *paid for*, and RDB **1000516** states it outright
(`--report rdb-tweaks --report-arg AlternateAdvancement`):

```
AlternateAdvancement_XPCap                  1000000     the AA experience pool's ceiling
AlternateAdvancement_GenericTokensAwarded         1     what a full pool pays out …
AlternateAdvancement_SpecificTokensAwarded        3     … alongside this
AlternateAdvancement_UnusedGeneralTokensCap     100     how many unspent tokens may be held
AlternateAdvancement_UnusedPvETokensCap         100
AlternateAdvancement_UnusedPvPTokensCap         100
AlternateAdvancement_PvPModifier                  5
```

So the economy reads: fill a **1,000,000**-point pool, receive **1 generic + 3 specific** tokens, spend them
against the 1..30 costs of 1000099, holding at most **100** unspent of each kind.

⭐ **And one of the pool stats is CAPTURED.** `AAPvEXP` (stat **1000007**) occurs in the corpus — 136 records
over 12 (capture, handle) runs, all on the one level-80 character, running **535,035 … 862,514**, i.e. inside
that 1,000,000 ceiling, which corroborates the cap being this stat's. Its eight AA siblings
(`AACurrentFeatPerfected`, `AAFeatPerfectionTimeStamp`, `AATimeSpentPerfectingFeats`,
`AAFeatPerfectionTimeRemaining`, `AAPvPXP`, `PerkLine`, `PerkSize`, `AATokensGainedSeasonal`) are **absent from
the corpus entirely** — every capture is a low-level character except that one.

⚠️ What is still missing is therefore narrower than "the wire": it is **how much a kill pays into the pool**
(the observed within-session steps are 2, 27, 27, 57, 91, 258 on one character, with nothing in the corpus
naming what was killed for each), and the purchase message itself. Both need a capture of an endgame character.

## Decoded: 1000627 `GroupFinderActivitiesTable` — the group-finder activity catalog

**12,012 bytes, decoded BY HAND from the bytes** (#469) — the parse callback resists a static read
(`FUN_013B21C0` drives a reflective `std::tr1::_Bind` walk with no transcribable `read()` sequence), so the
grammar below comes from aligning the records against each other, and the SEMANTICS from every id resolving in
our own `Maps` table (26 of 26 distinct candidates).

```
[12-byte RDB header]
[u8 1][u32 4][4 x [u32 i][u32 0x0EDDE941..44]]     a 4-entry id prelude (ids resolve in no LDB category)
[u32 81]                                            record count
81 records:
  [u32 seq (1-based)][u32 index (0-based)]
  [u32 kind]            0 = an activity row; 2/3 = a SECTION HEADER (rows 1, 12, 66)
  [u32 idB]             0x0A2087xx / 0x020879xx / small-hash style; sequential within a section
  [u32 idC]             0x01F5xxxx on the raid rows, else 0
  [u32 D][u32 E]        see below
  [u32 F][u32 G][u32 H] zeros; on TP_Start rows H = 1,3,5..0x1D (an odd-numbered slot)
  [u32 len][len bytes]  a LANDMARK name ("LFRTeleport", "TP_Start", "Frost_Swamp_Zone_In",
                        "Teleport_From_Kheshatta", "StartPlace") - or EMPTY, and then:
  [f32 x][f32 y][f32 z][u32 heading°]   an EXPLICIT destination (headings 45/90/180/270 in the data);
                        zeros when the name carries the destination instead
  [u32 x6]              the PARTY SHAPE: [24, 4, 14, 6, 18, 12] on every raid row,
                        [6, 1, 4, 1, 0, 2] on every dungeon row - 24-man raid vs 6-man dungeon
  [char[4] "UCM\0"][3 flag bytes]
[u32 91][91 x [u32 index][u32 b][u32 c]]   (b,c) takes 4 values: (2401,0x9FE2CB) x53, (2468,0) x28,
                                            (2469,0xD50D890) x9, (2420,..) x1 - per-index attributes, unnamed
[u32 94][94 x [u32 activity][u32 b][u32 c]] the finder WINDOW's category/child layout - see below
[352 u32 tail]                              un-walked
```

⚠️ **The record is LITTLE-endian**, unlike most of the wire. An earlier reading of this table quoted its words
byte-swapped (`0x0EDDE941` for the prelude's `0x41E9DD0E`); the field VALUES above are the LE ones.

### ⭐ The ActivityId is the record's 1-based SEQUENCE — and the capture, not the table, is what proves it

The scry script that starts an entry carries `CategoryId=4;ActivityId=38`, and the natural reading of the
94-entry section is that an activity is indexed *within* its category. **The corpus refutes that.** Record
**seq 38** is playfield **1092** (The Amphitheatre of Karutonia) carrying the destination `(112, 70, 39)` —
the exact arrival the captured `N3TeleportIIR` produced. Counting 38 within category 4's own block instead
lands on record 65, *Oasis of Zaara* (3204), which is a landmark-addressed row with no coordinate at all. So
the two readings disagree on the one case there is evidence for, and the evidence picks the **flat sequence**.
[V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json the 20:22 entry]

That makes the 94-entry section a *secondary* structure — the finder window's category/child layout, i.e. what
the UI lists under each tab — rather than the id→record mapping, and the server needs none of it. Shipped as
`GroupFinderActivityCatalog` (ten coordinate-carrying activities, all ten resolving in our own `Maps`), gated
by `GroupFinderActivityCatalogTests`, including the rival-keying control.

⚠️ The catalog holds only the rows that state an explicit `[x][y][z][heading]`. The rest of the six-man block
addresses its destination by a **landmark name** (`Frost_Swamp_Zone_In`, `Imirian_Ravine_Zone_In`,
`Oasis_of_Zaara_Zone_In`, `StartPlace`), which needs a per-playfield landmark table we do not hold — those
activities are left out rather than entered at an invented point.

The content, every id resolved through `Maps`:

* **Rows 2–11 — the RAID FINDER list** (`LFRTeleport`, 24-man): Thoth-Amon's Stronghold Lower/Upper (33032/
  33042), Temple of Erlik (37040), The Jade Citadel Courtyard/The Jade Citadel (36420/36421), Chamber of Lost
  Dreams (36427), The Palace of Cetriss ×3 (33082–84), Black Ring Citadel (33045) — `D = E =` the playfield.
* **Rows 13–27 (E=20000 Kuthchemes) and 67–81 (E=20010 Skull Gate Pass)** — fifteen `TP_Start` entries each,
  H = the odd slot 1..0x1D: the two dungeons' internal start points.
* **Rows 28–65 — the six-man GROUP FINDER dungeons**: named zone-ins (Frost Swamp 1214, Imirian Ravine 2205,
  Oasis of Zaara 3204) and coordinate teleports into Toirdealbach's Tomb (1024), Cradle of Decay (1026), Halls
  of Eternal Frost (1082), The Amphitheatre of Karutonia (1092), The Cistern (2030), The Main System (2032),
  The Catacombs (2033), Sanctum of the Burning Souls (2085), Black Castle (3001), Caravan Raider's Hideout
  (3033), The Scorpion Cave (3035) — `E` = the playfield, `D` = a 5,078,5xx location id (unresolved), the
  destination in the record's own `[x][y][z][heading]`.

⚠️ Left unnamed: `idB`/`idC` (no LDB category resolves them; the style matches the prelude ids), the
5,078,5xx `D` ids on coordinate rows, the exact meaning of the six party-shape words past "24-man vs 6-man",
and the 352-word tail. Naming those needs the reflective consumer at runtime — content for the group-finder
tier (#669's neighbourhood), not a table decode. ⭐ The `activity -> teleport` mapping that used to sit on this
list is **no longer open**: it is the record sequence, established above.

## ⭐ Decoded: 1000521 `GuildRenownRewardDataTable` — the guild-city reward ladder, in retail's own words

**57 records × 12 bytes**, a 20-byte header with the count in word 3:

```
[u32 rewardId][u32 guildRenownLevel][u32 ldbDescriptionId]
```

⭐ **The third column is an LDB id in category `10021`**, which is what turns a table of numbers into content: it
is the reward's own description text. All **57 resolve**, and the scan behind that was complete over the
plaintext LDB (0 unreadable, and category 10021 is not among the encrypted ones), so this is the whole ladder
rather than the part that happened to resolve.

Levels run **1..20** with 2–4 rewards each. It is the guild-city / renown progression the client shows in its
renown window, and it names mechanics nothing else in the data does — bodyguards for hire from the Barracks,
decoration tiers, Border Kingdom PvP vendors, per-archetype companions, the arena and sparring areas.

| renown level | reward id | LDB 10021 | what the client says it unlocks |
|---:|---:|---:|---|
| 1 | 1 | 227020769 | Vendors sell various social items. |
| 1 | 2 | 227020770 | Your city Barracks hire out bodyguards. |
| 2 | 3 | 227021026 | Vendors sell seasonal headwreaths. |
| 2 | 4 | 227021025 | Vendors sell purified, longer-lasting potions. |
| 2 | 5 | 227021027 | Your city supports small decorations, and decoration vendors move to your city. |
| 3 | 6 | 227017185 | Vendors sell men and women's clothing and the recipe for a guild city Tavern. |
| 3 | 7 | 227017186 | Vendors sell convenient three-in-one potion restoratives. |
| 3 | 8 | 227017187 | Border Kingdom vendors sell items to boost PvP experience gain. |
| 3 | 9 | 227017188 | Some decoration vendors upgrade their wares. |
| 4 | 10 | 227017441 | Vendors sell improvised hand-to-hand weapons. |
| 4 | 11 | 227017442 | Vendors sell colorful varieties of two cloak styles. |
| 4 | 12 | 227017443 | Some decoration vendors upgrade their wares. |
| 5 | 14 | 227017698 | Vendors sell charms to ward off the curse of death. |
| 5 | 15 | 227017699 | You may compete in the Khemi Sparring Area. |
| 5 | 16 | 227017700 | Some decoration vendors upgrade their wares. |
| 5 | 17 | 227017953 | Vendors sell chicken, pig, and sheep companions. |
| 6 | 18 | 227017954 | Hub city vendors sell sneezing powder to prevent enemy stealthing. |
| 6 | 19 | 227017955 | Your city supports centerpiece decorations, and decoration vendors move to your city. |
| 6 | 42 | 142657777 | Your city gains a cultural retreat under the Keep. |
| 7 | 13 | 227017697 | Vendors sell horses with new varieties of colored armor. |
| 7 | 21 | 227018210 | Vendors sell contracts to summon traveling merchants. |
| 7 | 22 | 227018211 | Some decoration vendors upgrade their wares. |
| 8 | 23 | 227018465 | Quests to gain companions. |
| 8 | 24 | 227018466 | Hub city vendors sell lodestones to weigh you down. |
| 9 | 25 | 227018721 | Vendors sell colorful varieties of two more cloak styles. |
| 9 | 26 | 227018722 | Hub city vendors sell lodestones to lighten your opponents. |
| 9 | 58 | 227018723 | Dance masters across the world will teach you other races' dances. |
| 10 | 27 | 142658289 | Vendors sell a swift Shemite horse. |
| 10 | 28 | 142658290 | Vendors sell recipes to build Megaliths and Astrolabes. |
| 10 | 29 | 142658291 | You may quest to gain recipes for Cimmerian wards. |
| 11 | 30 | 142658545 | Vendors sell recipes for building a guild city Tavern II. |
| 11 | 31 | 142658546 | Border Kingdom vendors sell morale-boosting items. |
| 12 | 32 | 142658801 | Vendors sell vittles to help your fortune in resource harvesting. |
| 12 | 33 | 142658802 | Border Kingdom vendors sell improved PvP experience gain items. |
| 13 | 34 | 142657009 | Vendors sell horse tricks and Tavern plans. |
| 13 | 35 | 142657010 | Hub city vendors sell itching powder to prevent enemy Path-using. |
| 13 | 36 | 142657011 | Some decoration vendors upgrade their wares. |
| 14 | 37 | 142657265 | Vendors sell living rats, scorpions, snakes, and spiders to wear. |
| 14 | 38 | 142657266 | Vendors sell recipes to weave the Soldier's Haversack. |
| 14 | 39 | 142657267 | Some decoration vendors upgrade their wares. |
| 15 | 40 | 142657521 | Quest to gain the Stygian Halfbreed horse mount. |
| 15 | 41 | 142657522 | You may compete in the Coils of the Serpent arena. |
| 16 | 43 | 142657778 | Vendors sell recipes to build improved Megaliths. |
| 16 | 44 | 142657779 | Some decoration vendors upgrade their wares. |
| 16 | 48 | 142656241 | Vendors sell recipes for building a guild city Tavern III. |
| 17 | 45 | 142655985 | Quest to gain a companion unique to your race. |
| 17 | 46 | 142655986 | Border Kingdom vendors sell improved PvP experience gain items. |
| 17 | 47 | 142655987 | Some decoration vendors upgrade their wares. |
| 18 | 49 | 142656242 | Vendors sell charms to resurrect your friends. |
| 18 | 50 | 142656243 | Hub city vendors sell resonators with which to detect hidden enemies. |
| 18 | 51 | 142656244 | You may quest to gain recipes for exquisite marble statues. |
| 19 | 52 | 142656497 | Quest to gain one of three companions unique to your archetype. |
| 19 | 53 | 142656498 | Border Kingdom vendors sell improved morale-boosting items. |
| 19 | 54 | 142656499 | Master architects may quest to learn to build superior Megaliths. |
| 20 | 55 | 142662385 | Quest to gain the Tarpani Stallion horse mount. |
| 20 | 56 | 142662386 | Vendors sell recipes to weave the Commander's Haversack. |
| 20 | 57 | 142662387 | Some decoration vendors upgrade their wares. |

⚠️ The reward is a DESCRIPTION, not a grant: the row carries no item, recipe or spell id, so what actually
becomes available is stated in prose and would have to be wired per line. That is a content statement about the
table, not a decode gap — there is no fourth column.

Related: #147 (guild cities), #335 (the renown block on the guild full-update).

## Decoded: 1000520 `CoreTokenHolder` — the alternate-CURRENCY catalog

The one table whose *shape* the stride sweep could not even state, because its header is not the usual one: the
sweep read the fourth word as a count and got `202875138`. The parse callback (`FUN_00813E80`, off loader
`FUN_00813D20`) says why — after seeking past the 12-byte RDB header it reads **two BYTES**, a version and a
count, and every record is variable-width:

```
[u32 type][u32 id][u32 version]          # the usual 12-byte RDB-Id header
[u8 fileVersion = 2][u8 count = 29]
count × [u8 tokenId][u16 nameLen][name][u8 group][u32 ldbNameId][u32 ldbDescriptionId]
```

⚠️ **The name is gated on `fileVersion`** — the callback reads it only `if (1 < version)`, so a v1 file has no
names at all and a reader that assumes them would desync on one. This build ships v2.

⭐ **The two u32s are LDB ids, and they are COMPUTED from the name**: `elf28("<name>_Name")` and
`elf28("<name>_Description")`, the same 28-bit hash the bark tokens use (see
[ldb-string-index.md](ldb-string-index.md)). It reproduces **26 of the 29 rows exactly**, and the three misses
are the AltAdvancement family, whose LDB tokens are the SHORT spelling — `AAPvEToken`, `AAPvPToken`,
`AAGeneralToken` — which then hit exactly too. **29 of 29.** The client's own `SimpleTrophy1_Name` /
`RareRelic_Name` / … string block at `0203E5DC` is the independent confirmation that those tokens are real.
(They do not resolve against our decrypted LDB pages, so the display TEXT is still out of reach; the ids and
their preimages are not.)

### The 29 tokens

`tokenId` runs 1–62 with gaps — 29 of 62 slots are used, so the id space has retired entries and an id is not an
index. `group` is an unnamed classification byte, and its clusters are legible:

| group | tokens (`name` (id)) |
|---|---|
| 1 | `VeteranPoints` (1) |
| 2 | `AltAdvancementPvPToken` (2), `AltAdvancementPvEToken` (3), `AltAdvancementGeneralToken` (4) |
| 3 | `SimpleRelicIII` (5), `RareRelic` (6), `SimpleRelicI` (7), `SimpleRelicII` (8), `SimpleRelic` (17), `DragonTear` (19), `SimpleRelicV` (33), `ShardOfPureIce` (34), `EmeraldEssence` (35), `SimpleRelicVI` (36) |
| 4 | `WorldPvPToken` (9), `PvPMinigameToken` (10), `VictoryToken` (30), `PortentRelic` (31) |
| 5 | `SimpleTrophy1` (11), `SimpleTrophy2` (12), `SimpleTrophy3` (13), `RareTrophy` (14), `OnslaughtToken` (61), `SkullgateToken` (62) |
| 6 | `EsteemToken` (15), `MarkOfAcclaim` (16), `LotteryToken` (32) |
| 7 | `TeleportCoins` (50) |
| 8 | `AtlanteanShard` (60) |

⚠️ The group byte is the one field with no independent evidence for its MEANING. The loader stores it at the map
entry's `+0x1c` and the clusters read as a kind (AA tokens together, relics together, trophies together, the PvP
currencies together) — but "kind" is a description of the grouping, not a decode of the column.

What the neighbouring exe strings say the subsystem does: `YouReceivedQuantityOfTokens` and
`YouReceivedQuantityOfTokensMembershipBonus` are its award messages, and a `Subscriber<Token>BonusPercent` tweak
exists per token (`SubscriberSimpleTrophy1BonusPercent`, `SubscriberEsteemTokenBonusPercent`, …) — i.e. a
subscription multiplies token income per currency. Nothing on our side models tokens yet; this is the catalog a
model would key on.

## Decoded: 1000626 `CoreChatFilterDataTable` — the per-language profanity list

The other variable-width table the stride sweep could not walk. It is a two-level list, and it parses to the
byte with nothing left over:

```
[u32 type][u32 id][u32 version]              # the usual 12-byte RDB-Id header
[u32 fileVersion = 1][u32 languageCount = 6]
languageCount × [str langCode][u32 wordCount][wordCount × str]

str = [u32 byteLength][bytes]                # ⚠️ UTF-8, not Latin-1
```

⚠️ **The strings are UTF-8 and u32-length-prefixed**, both unlike the wire (`[u16][Latin-1]`). The length is a
BYTE count rather than a character count — `Abscheißer` is 10 characters and 11 bytes — and the 707 Korean
entries settle the encoding beyond the one German case.

| language | words |
|---|---:|
| `de` | 55 |
| `en` | 60 |
| `es` | 19 |
| `fr` | 29 |
| `ko` | 707 |
| `pl` | 50 |
| **total** | **920** |

The list itself is not reproduced here. Nothing on our side filters chat; this is the catalog a filter would
read, and its shape is what #469 wanted.

## Decoded: 1000509 / 1000510 / 1000512 `PlayfieldPvPData_c` — playfield → PvP RULESET

Three tables off one loader (`FUN_00911930`), and the client's own assert says how they relate:

```
"PlayfieldPvPData_c: Data is borked! Points to a non-existing PvP ruleset: "   FUN_00911830
```

So two of them are **lookups** and one is the **ruleset table** they point into.

| type | records | record |
|---|---:|---|
| `1000509` | 243 | `[u32 playfieldId][u32 rulesetId][u32 alternateRulesetId]` — `0xFFFFFFFF` for "none" |
| `1000512` | 5 | `[u32 playfieldId][u32 rulesetId]` |
| `1000510` | 13 | the RULESETS, 21 × u32 each |

⭐ **The foreign key holds**: every ruleset id either lookup names — `1, 2, 3, 6, 7, 8, 11, 12` from 1000509 and
`4, 5, 13` from 1000512 — exists in 1000510, which is exactly the condition that assert fires on. And **235 of
1000509's 243 playfield ids are in our `Maps`** (`1010` Conall's Valley, `1020` Field of the Dead, `2020`
Tarantia Common District, …), which is what says column 0 is a playfield rather than an index.

⭐ **1000512's five are the PvP MINIGAME maps** — `5201` Blood Ravine, `5006` The Call of Jhebbal Sag, `5012`
Lost Temple, `5005` Hallowed Vaults, `5002` Totem Torrent — and they are the only three rulesets (4, 5, 13) that
1000509 never names. So it is the minigame table, not an override of the same rows.

⚠️ The **alternate** column is structural, not named. 186 of the 243 rows are ruleset `7` with no alternate, 36
are ruleset `1` with alternate `8`, and the rest have none. Rulesets 1 and 8 are the only pair that shares a
trailing `[20] = 1`, so the alternate is a variant of the same rule rather than an unrelated one — a PvE/PvP
dimension split is the obvious reading (the login handshake does offer `Saga of Blood` beside `Crom`) and is
**not** established here.

The 13 rulesets, columns `[1]`, `[5..7]`, `[9..13]` and `[15..19]` being zero on every row:

```
 ruleset   [2]  [3]  [4]   [8]  [14]      [20]
    1       2    6      0   1        0     1
    2       0    0      0   1        0     3
    3       0    0      0   1        0     2
    4       2   12   2400   1  4230307     3
    5       2    6   1200   1   269317     3
    6       0    0      0   0        0     4
    7       0    0      0   0        0     0     <- the default: 186 playfields, everything off
    8       0    0      0   1        0     1
    9       0    0      0   1   269317     3
   10       0    0      0   0        0     5
   11       0    0      0   1        0     3
   12       0    0      0   1        0     3
   13       2    6   1200   1   144407     3
```

`[4]` is `[3] × 200` wherever both are set, so one is derived from the other (6 / 1200 and 12 / 2400 read as a
duration in two units). ⚠️ Ruleset **1** — the ordinary open-world one — also carries `[3] = 6` with `[4] = 0`,
so `[3]` is not minigame-specific and neither column is named here. `[14]` is a reference of some kind —
`269317` twice, `144407` and `4230307` once each — and it resolves to **nothing**: not a `Spells` or `Items`
row, and `--report rdb-id` finds **0 entries** for any of the three anywhere in the RDB index. So it is not a
resource id, and naming it needs the accessor's consumers rather than the table.

### ⭐ What the rulesets turn out to BE, read through the playfields that use them

Resolving every playfield id against `Maps` partitions the 13 rulesets along exactly the PvP zone types
(Gitea #146 / #503):

| ruleset | used by |
|---:|---|
| **7** | the default — 186 playfields, and its row is all-zero |
| **1** (alternate **8**) | the 36 ordinary open-world zones: Conall's Valley, Field of the Dead, the Tarantia districts, Khopshef, Kheshatta, the Khitai zones, Thunder River, … |
| **6** | `Border Kingdoms, Cimmerian End` (11040), `Border Kingdoms, Aquilonian End` (12070), `Border to Kush` (13050), `Massive Pvp Test` (89), `Minigames (General)` (38) — **the massive-PvP ruleset** |
| **4 / 5 / 13** | **only the five minigame playfields**, via 1000512, and nothing in 1000509 names them |
| **2** | `Lacheish Plains PvP` (1041), `Hiving Heaven` |
| **11** | `The Pit Master's Arena (Fighting Pits)` (1043) |
| **12** | `Khemi's Sparring Arena`, `Coils of the Serpent`, `Feast Hall`, `Council Forum`, `Ritual Chambers`, `The Lion's Maw` |

⚠️ Three playfields that look like they belong here are in **neither** table: `5003 PvP - TEAM DEATHMATCH`,
`5200 PvP CTF 1` and `5100 Border Kingdoms - Massive PvP`. The shipped rules cover the five live minigame maps
and the Border Kingdoms ends, not those.

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
dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1065103 --out <dir> --Content:RdbDir <rdb-dir>
```

## ⚠️ ARMOR_MITIGATION is empty

Type 1000514 is 16 bytes: `[1000514][0][1][0]` - a valid header declaring **zero entries**. The table ships
but carries no rows on this build.

This CONFIRMS #401's blocker rather than removing it. `CombatResolver` notes the client uses its closed-form
mitigation formula "when its ARMOR_MITIGATION table has no matching row"; there are no rows at all, so that
path is always taken, and the formula's `C1/C3/C4` coefficients really do have to come from the runtime
balance config (a live Frida dump). The table route is closed, and now provably so rather than by assumption.


## ⭐ Decoded: 1000506 `FeatClassTable_c` — the archetype → class MEMBERSHIP table

The registry table above files 1000506 under `RDBPlayerClassData_c` because 1000505–1000508 is written there as a
span. It is not: `DumpDataTableRegistry` puts 1000506 on the **`FeatClassTable_c`** loader (`FUN_0078de40`), whose
own bail-out names it — *"Could not load classmembership info. This will cause gui to loose parent class feats"*.

```
[u32 type][u32 id][u32 version][u32 count]
count * [u32 parentClassId][u32 childClassId]
```

The parse callback (`FUN_0078e870`) checks `length >= count * 8` and appends each row's **second** word to the
vector belonging to the class named by its **first** — so it is a parent → children adjacency list over 1000505's
class ids.

**Two entries ship, and they are not versions of each other.**

| id | edges | what it is |
|---:|---:|---|
| 0 | 27 | the full **authored** hierarchy, including eleven classes this build never shipped |
| 1 | 16 | the **shipped** set |

Entry 1, in full:

```
  0 Commoner  -> 1 Soldier, 2 Priest, 3 Rogue, 4 Mage
  1 Soldier   -> 20 Guardian, 22 Conqueror, 31 Dark Templar
  2 Priest    -> 24 Priest of Mitra, 28 Tempest of Set, 29 Bear Shaman
  3 Rogue     -> 18 Barbarian, 34 Assassin, 39 Ranger
  4 Mage      -> 41 Necromancer, 43 Herald of Xotli, 44 Demonologist
```

Entry 0 adds `21, 23, 30` to Soldier, `25, 26, 27` to Priest, `19, 33, 35, 36` to Rogue and `42` to Mage — ten ids
1000505 does not name, i.e. cut classes.

⭐ **This retires a wiki citation.** `ConanArchetypes` stated that "the client ships the archetype IDS (1000505 tier
1) but no MEMBERSHIP table … so the membership is taken from the game's own published documentation". The
documentation was right, and it is now corroboration rather than the source: all twelve placements agree with entry
1 exactly. `--report class-hierarchy` re-runs that comparison and fails loudly on any drift.

## ⭐ Decoded: 1000099 `FeatClassTable_c` — the feat POINT COST table

Named by its loader's two error strings — *"Could not load feat point cost info."* and, in the parse callback
(`FUN_0078e560`), *"Invalid class point cost data size -- Expected[…] Got[…]"*. The callback also rejects any
version but `1`, and requires `length >= count * 0xC`.

```
[u32 type][u32 id][u32 version][u32 ?][u32 count]      <- count is the FIFTH word
count * [u32 key][u32 kind][u32 cost]
```

All three words are kept verbatim — the callback's per-record tail (`FUN_0078f7f0`) is a plain
`vector<12-byte struct>::push_back`, so nothing is dropped or scaled, unlike 1000513.

Measured over the shipped 3,096 records:

| word | shape |
|---|---|
| `key` | 2,880 distinct in a **dense** 734,733–737,752 range; the vector is sorted on this word alone |
| `kind` | `2` or `3`, exactly 1,548 of each |
| `cost` | 1–50, 26 distinct values |

### ⭐ `kind` is the AA POINT POOL, and the client names it

The consumer settles it. `GetPointCost` — the `com.GameInterface.FeatWindows.AAFeatTree` API, `FUN_01506700`
— walks this exact vector (`for (p = begin; p != end; p += 0xC)`), reads the **cost at `+8`** and switches on
the **`kind` at `+4`** into three GUI keys:

| `kind` | `GetPointCost` writes | `AAFeatPlannerPointType` | `TokenPool` (RDB 1000520) |
|---:|---|---|---|
| 2 | `PvPPoints` | `e_PvP = 2` | `AltAdvancementPvPToken` |
| 3 | `PvEPoints` | `e_PvE = 3` | `AltAdvancementPvEToken` |
| 4 | `GeneralPoints` | `e_Generic = 4` | `AltAdvancementGeneralToken` |

The middle column is the client's own enum, registered by the same GameInterface (`FUN_01504bf0`) with those
literal values — so the three ids are the AA half of the token table, not a coincidence of small numbers.

⚠️ **This corrects an earlier reading here.** The 216 keys that appear **twice**, once under each `kind` and
at the **same cost**, were taken as proof that *"`kind` is not a currency — a currency would give every feat both
rows"*. That premise is wrong: a perk priced in one pool only is a perk that pool's players alone may buy, which
is exactly what the split looks like. Of the 22 perks every class carries, the crowd-control resistances
(`Root` / `Snare` / `Stun` / `Fear` / `Stagger Resistance`, `Quick Steps`, `Precise Strikes`, `Field of War`) are
priced in **PvP** points and the elemental wards (`Fire` / `Frost` / `Electric` / `Unholy` / `Holy Wards`,
`Pressing` / `Decisive Strikes`, `Immeasurable Empowerment`) in **PvE** ones. The dual rows are the handful
either side may buy, and their equal cost means the pool is a restriction on the buyer, never a discount.

⚠️ The shipped table never uses `kind` 4, though the client handles it — no perk is priced in the generic
token, which is consistent with `AlternateAdvancement_GenericTokensAwarded` being 1 against 3 specific.

Imported as `FeatPerkCost` (#150), keyed `(FeatClassEntryId, PointType)` and gated by `AaPerkCostTests`.

⭐ **The key is 1000043's ROW ID** (#862) — its word 0, the column that section used to read as a feat id. All
3,096 rows resolve, and joining through it names every one: the key is a `(class, level, feat, tree, tier)` row,
so a cost is attached to a feat's PLACEMENT in a tree rather than to the feat. It joined to nothing before
because the search was for a feat id, and word 0 is not one.

⭐ **And what it covers is the answer to "where does a feat's point cost come from?"** — every one of the 2,880
rows it names sits in the twelve level-20/80 ALTERNATE-ADVANCEMENT trees (`treeId` 1000177–1000190 — the
range is not dense; 1000185 and 1000187 do not exist), and no ordinary tree
feat has a row at all. Ordinary feats have no cost table because they do not need one: they cost **one point per
rank**, which reproduces all three captured characters' point budgets exactly (see
[feats.md](../subsystems/feats.md)). Costs of 1–30 could not be per-feat point costs anyway under a level-80
budget of 79.


## 1000042 `Feat_t` — the layout, from the loader's own parse callback

The loader is `FUN_00788E20` (its failure log names it: *"FeatTable_t::LoadStaticData() Unavailable Feat data
%d:%d"*, with `0xF426A` = 1000042). Its parse callback — the second argument to `FUN_0104D760` — is
`FUN_00788EF0`, and it states the record layout exactly rather than leaving it to be inferred from the bytes.

It opens with `(*this+0x18)(0xc, 0, 1)`: **twelve** columns. Then two header reads, a COUNT and a **MODE** word,
and a per-record loop of ten unconditional reads plus two conditional ones:

| word | what the callback does with it |
|---:|---|
| 0 | kept, and **also divided by 100** into a second slot |
| 1–6 | kept |
| **7** | ⚠️ **read and discarded** |
| 8 | kept |
| **9** | ⚠️ **read and discarded** |
| 10, 11 | read as a pair; **which one is used depends on the header's MODE word** — mode 1 takes word 11, mode 2 takes word 10, and the taken value **replaces** word 0's `/100` |

so nine of the twelve reach `FUN_0078B490`, the row constructor, and two never leave the stream.

⭐ The arithmetic corroborates the column count independently: the decompressed resource is **316,484 B**, and
`20 + 6593 × 48` is exactly that — 12 words per record, 6,593 records, a 20-byte header.

⚠️ Two consequences for anyone reading this table by stride. The `/100` slot means **word 0 is not in the units it
looks like**, and the mode-selected pair means **the last two columns are not both live** — one of them is dead on
any given build, and which one is not a property of the record at all. `RdbFeatImporter.ReadIdMapAndPrereqs`
currently reads words 0, 1 and 10 (`(wireId, templateId)` and the base index) and is unaffected by either.

What the nine kept columns MEAN is still open — that is step 3 of the method above (`DumpXrefsTo` on the
accessor, then the consumers' log strings), and it is what Gitea #469 tracks for this table.
