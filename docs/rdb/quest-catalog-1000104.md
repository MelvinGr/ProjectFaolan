# RDB 1000104 — `RDBQUESTGIVERS`, the client's quest catalog

<!-- Gitea #569 (the catalog) · #570 (the still-unnamed @+0x14 bits) -->

The client ships its whole quest catalog as one RDB entry: **type `1000104`, id 0**, 728,765 bytes
decompressed. It is a **12-byte RDB header followed by a raw `QuestGiverList` protobuf** — the schema this
repo already carried in [`Faolan.Core/Protobuf/aoc-client-protos.proto`](../../Faolan.Core/Protobuf/aoc-client-protos.proto)
but never read. It is one of the 25 named `CoreTables` data tables the client logs at startup
(see [client-data-tables.md](client-data-tables.md)); the name `RDBQuestGivers` appears in the quest module's
string pool, next to `RDBQuestGivers::ReloadData: Cannot parse protocol buffer`.

Dump it with

```zsh
dotnet run --project Faolan.Importer -- --report rdb-quest-catalog --RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report rdb-quest-catalog --report-arg 1054   # one line, as a tree
```

## What it contains

| list | rows | distinct ids |
|---|---:|---:|
| `main_quest` (`MainQuest`) | 2183 | 2181 |
| `quest` (`Quest`) | 5050 | 5042 |
| `quest_goal` (`QuestGoal`) | 7327 | 7301 |
| `quest_giver`, `quest_giver_pos` | 0 | 0 |

The model is **`MainQuest` (a quest LINE) → `Quest` (a step) → `QuestGoal`**, and it maps onto our tables
one-for-one:

| ours | client |
|---|---|
| `QuestLine.QuestLineId` | `MainQuest.template_id` |
| `QuestLine.Magic` | `MainQuest.hash` |
| the `QuestLineTitle` text slot | `MainQuest.name` |
| `QuestDefinition.QuestId` | `Quest.template_id` |
| `QuestDefinition.QuestLineId` | `Quest.main_quest_template_id` |
| `QuestGoalDefinition.GoalId` | `QuestGoal.template_id` |

[V1: Faolan.Importer/Cli/ReportCli.RdbQuestCatalog.cs — `MainQuest.hash` is the `ACGHash` line token, and all four tokens recovered independently from captures
match the catalog exactly — 241→`W6QT`, 1054→`RRL3`, 1772→`B0DN`, 1834→`3734`. The report prints this
cross-check on every run; the two sources share nothing but the claim that our `QuestLineId` is the client's
`MainQuest.template_id`, so agreement on four of four is what establishes that identity.]

A **quest line is a storyline, not a place.** Sizes run 1..13 steps (450 lines hold one step, 1230 hold two);
every name carries a bracketed dev tag, e.g. `[Eiglophian_Mountains] Feast of Coltranach` grouping the steps
*Rumors of the Dragon* → *The Feast of Coltranach* → *Through the Cave*. Only 29 of the 450 single-step lines
reuse their step's name, so the line name is genuinely its own text.

## ⚠️ Which fields are actually populated

Several right-shaped fields are **empty in the shipped data**. The report prints this census on every run
precisely so nobody plans a decode around a column of zeroes.

| field | populated | note |
|---|---:|---|
| `MainQuest.first_quest_template_id` / `last_quest_template_id` | 2183 / 2183 | the line's endpoints |
| `MainQuest.tier` | 1791 | |
| `MainQuest.reward` / `optional_reward` | 1098 / 619 | item template ids, as `templateId:type` |
| `MainQuest.prev_template_id` | **394** | see ordering below |
| `MainQuest.next_template_id` | **3** | effectively dead |
| `MainQuest.xp_reward` / `cash_reward` | **23 / 18** | NOT a harvest source — see below |
| `MainQuest.type` | **0** | dead |
| `MainQuest.condition_script_id` | **0** | dead |
| `Quest.main_quest_template_id` | 5050 | line membership, complete |
| `Quest.tier` / `start_new_tier` | **0 / 0** | dead |
| `Quest.start_scry_script_id` | **0** | dead |
| `QuestGoal.phase` | 7327 | |

**Duplicate template ids are real and are not all identical.** 2 `MainQuest` ids, 8 `Quest` ids and 26
`QuestGoal` ids repeat; of those, 2 lines and 15 goals have rows that **differ**. A "first record wins" dedup
would silently drop a definition — the same trap as
[repeated wire records](../protocol/README.md). The report counts them and names the differing ids.

## Ordering: `prev`/`next` order LINES, and only `prev` is real

`prev_template_id` and `next_template_id` live on `MainQuest`, so they order **lines, not quests**. They are
not a doubly-linked list: `prev` is populated on 394 of 2181 lines, `next` on 3, and only 2 of the 394 `prev`
links have a reciprocal `next`. `prev` is a **single-parent prerequisite pointer** — "this line opens after
that one" — forming trees up to 10 deep. Two targets dangle (798, 806).

[V1: Faolan.Importer/Cli/ReportCli.RdbQuestCatalog.cs — walking `prev` back from `[Kara Korum][JS] On the Origin of Species` yields Telltale Signs → Brethren of
Bone → The Obsidian Cult → Matters of Trust → The Passage North → The Wonders of the Wasteland → Entrenched
Hatred → The One Forsaken → Immortal Vengeance → Test of Endurance → On the Origin of Species — a coherent
narrative order, which is what says the pointer is a prerequisite rather than an arbitrary grouping.]

**Step order WITHIN a line is not in this table.** `Quest.tier` and `start_new_tier`, the two fields shaped
for exactly that, are empty. A line gives you its first and last step and its membership; the order of the
ones between is still hand-authored (`ImportOps.Chain`).

## The reward fields name the journal record's scalars

`MainQuest` carries exactly one reward pair, `xp_reward` (15) and `cash_reward` (16). They are populated on
too few rows to harvest from, but they are what NAMES two of the `Quest_c` journal record's long-unnamed
words — see [questc-region-decode.md](../re/questc-region-decode.md).

[V1: Faolan.Importer/Cli/ReportCli.RdbQuestCatalog.cs — `[Thunder River] Voyage of the Dead`, `Turning Point` and `Pools of Blood` each carry `xp_reward =
45800`, which is exactly the captured `Quest_c` `@+0xf0` of quests 13895 and 14427; and the catalog's
low-level cash/xp ratios (715/60, 1000/157, 455/36 → 0.08–0.16) match the wire's (150/15, 370/44, 550/66 →
0.10–0.12).]

## Importing it

```zsh
dotnet run --project Faolan.Importer -- --only rdb-quest-catalog --RdbDir <rdb-dir>
```

An **opt-in RDB module** like the rest, `Insert`/`Upsert`, no dependencies. It **adds and names; it never
overwrites captured wire data** — a quest that already has a row keeps every harvested scalar, and only
`Name`, its line membership (when it has none) and the line's own catalog columns are written. Where we have
the wire, the wire is the better evidence. `Verified` rows are skipped entirely, as everywhere else.

What it landed on first run:

| table | before | after |
|---|---:|---:|
| `QuestLines` | 973 | 2209 |
| `QuestDefinitions` | 2037 | 5048 |
| `QuestGoalDefinitions` | 170 | 7317 |
| `QuestLineRewards` | 0 | 3943 |

Three gaps in the client's own data are recorded rather than papered over:

- **20 placeholder lines.** 63 catalog quests point at a line id the catalog's `main_quest` list does not
  define. A placeholder row carries the id and nothing else, which keeps those quests instead of dropping
  them to satisfy an FK — the same pattern `ImportOps` uses for a quest whose Map is not in the catalog.
- **2 goals with no quest**, counted in the module's output rather than skipped silently.
- **17 rows of 14560 have a duplicate id whose content DIFFERS** (2 lines, 15 goals). The importer takes the
  last, because nothing in the data separates them; the report names the ids so the choice is visible.

Two model consequences worth knowing:

- **`QuestDefinition.MapId` is now nullable.** The catalog carries no map, and the column was a NOT NULL FK
  whose "0 = none" case never occurred *because the importer skipped any quest whose map it could not
  resolve*. Nullable is both the honest model and what lets a mapless quest exist.
- **`QuestGoalDefinition.Separator` (the kind tag) is now nullable.** NULL means "never seen on the wire",
  which is every catalog goal. It is deliberately not 0: there is no kind 0, and 0 was the un-harvested
  default that #548 removed — reusing it would resurrect that exact ambiguity.

## ⚠️ The catalog is a SNAPSHOT, and it lags the live content

It is not "every quest in the game" — it is every quest **as of the patch the RDB was built from**. Its
ceilings are quest id **18533** and goal id **12885**; the captures carry content above both.

Everything the DB holds above those ceilings came from the wire and the catalog knows nothing about it — and
the split is exact, with no other gaps: **6 quests** (19314, 19363, 19364, 19365, 19522, 19620) and
**18 goals** (13962–14446) are the *only* rows in the whole DB with no catalog `Name`, and every one of them
is above the ceiling. They are the World-Boss Mastery meta, Anniversary 2014, Halloween 2015 and Onslaught.

[V1: Faolan.Importer/Cli/ReportCli.RdbQuestCatalog.cs — five of those six are exactly the quests carrying
`Quest_c` `@+0x11c == 1` (the sixth, 19363, has no captured journal record at all, so the wire never stated a
value for it), which is an independent signal picking out the same content: `@+0x11c` was
recorded as "1 for the newest quests, meaning open", and the catalog's ceiling now gives that observation a
mechanism rather than a coincidence. n = 6, so it is a lead and not a decode.]

So: treat a missing catalog name as "newer than this RDB", not as "the client has no name for it".

## Cross-checked against the encrypted LDB — 152 of 152 exact

The catalog's `QuestGoal.name` and the encrypted LDB category **50304**
(`quest_goal_templates.goal_name`) are the SAME field reached two entirely independent ways: a protobuf data
table on disk, versus a ciphertext page unlocked by a key the *server* sent us and we captured.

[V1: Faolan.Importer/Cli/ReportCli.LdbQuestText.cs — on every goal where both are available the two strings
are **byte-identical**: 152 agree, 0 differ, with no normalisation needed. `--report ldb-quest-text` now runs
this comparison on every invocation and prints the tally, so a crossed key, a wrong page or a bad catalog
dedup shows up as a diff instead of going unnoticed.]

Two consequences:

- It **validates both paths at once** — the catalog import and the LDB decryption independently agree.
- The catalog name is a usable stand-in for the objective text, so `--report ldb-quest-text` falls back to it
  where no key was captured. That takes the report from **170** readable objective lines to **7317**. Lines
  sourced that way are marked `[catalog]` so the provenance stays visible in an output whose whole purpose is
  to be the evidence other typings are taken from.

## Finding the next such table

The sweep that found this one is worth repeating for the other named data tables: iterate
`RdbReader.TypeCounts()`, take every type with a small entry count, and try `Serializer.Deserialize<T>` **at
byte offsets 0..20** for each candidate schema. The 12-byte header is why parsing at offset 0 only reported
no hits on the first attempt.
