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
dotnet run --project Faolan.Importer -- --report rdb-quest-catalog --Content:RdbDir <rdb-dir>
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
| `MainQuest.first_main_quest_template_id` / `last_main_quest_template_id` | 1791 / 1791 | populated but **degenerate** — see below |
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

### 1000104 is the ONLY RDB type that carries quest names — swept, with a control

Asked directly: is there quest data in some *other* RDB type? Swept with
`--report rdb-text-scan --report-arg "<names>:local"` over **all 411,233 local records, 0 unreadable**:

| needle | records | types |
|---|---:|---|
| `Freeing the Captured` (quest 13480) | 1 | **1000104 only** |
| `The Scavenger's Key` (quest 13563) | 1 | **1000104 only** |
| `Threat of the Vanir` *(control — a region label, not a quest name)* | 10 | 1030002 ×7, 1000053, 1000055, 1000104 |

Two real quest names occur in the catalog and **nowhere else in the client**. The control is what makes that
a finding rather than a failed search: a string that *is* present in several tables is found in all of them, so
the two single-hit results are the tables' silence, not the scan's.

The control also sharpens the [bounded-area](bounded-areas-1000053.md) question. It appears as the
`<name>` `QUEST Threat of the Vanir` (1000053, and its `Spawn-area / camp names` twin 1000055 at the same
record id 1020, plus the localized-string table 1030002), while the catalog's line is named
`[Field_of_the_Dead] The Threat of the Vanir`. **The two strings differ** — the region label is a designer's
shorthand for the line, so the link is real to a human reader and cannot be made by exact match. That is why the
51 `quest-goal` bounded areas are not a harvest source: wiring them to goals would be editorial, not a rule.

⚠️ `:local` skips CDN-only records, so this is "not in the local packs". The types that could carry quest text
at all are local, so the conclusion holds; a CDN-inclusive sweep is simply much slower.

### The chapter-GROUP pointers are a self-echo, not a grouping

`first_main_quest_template_id` / `last_main_quest_template_id` (fields 11 and 12) sit on 1791 chapters and read
like the chapter-level twin of `first_quest_template_id`/`last_quest_template_id` — those bound a chapter's
*steps*, so these should bound a *group of chapters*. They were populated, and read by nothing: absent from the
census, the importer and the model alike, which is exactly the shape a missing row hides in.

Measured (`--report rdb-quest-catalog`, "chapter GROUP pointers"), the reading does **not** survive:

| check | result |
|---|---|
| both ends resolve to a real chapter | 1791 / 1791 |
| `first` == the row's **own** id | **1788** |
| `first` == `last` (a group of one) | **1786** |
| genuine multi-chapter groups in the whole table | **3** — `{2495, 2496} → 2549`, `{1741, 2908}`, and `3279` (one of the two duplicated ids) |

So the pair is a self-reference on 99.7% of rows. `tier` does separate the members where a group is real, but
three groups is not a feature worth a column. Left as a report line, deliberately **not** imported.

### "Dead" is two different things, and only one of them is an absent field

That column counts rows whose value is **non-zero**, which conflates a field retail never emits with one it
emits as zero. Walking the record as raw protobuf — tallying `(field number, wire type)` per submessage rather
than reading it through the schema — separates them:

| | fields |
|---|---|
| **never emitted at all** | `MainQuest.condition_script_id` (6), `Quest.start_scry_script_id` (5) |
| **emitted on every row, carrying zero** | `MainQuest.type` (7), `Quest.tier` (6), `Quest.start_new_tier` (7), `MainQuest.xp_reward`/`cash_reward` (15/16 — present on 2,181 of 2,183) |

Only the first pair is genuinely absent from the shipped data. The distinction matters for the same reason
[protobuf presence](../protocol/README.md) does anywhere else: an unset optional and a zero read back
identically through a generated getter, so "0 populated" on its own cannot tell you which you are looking at.

### The layout is fully accounted for — there is no undecoded region

The same raw walk consumes the **whole** payload: every submessage parses to its own end, the top level parses
to EOF, and no byte is left over. Every field number that appears is declared in
[`aoc-client-protos.proto`](../../Faolan.Core/Protobuf/aoc-client-protos.proto), and no undeclared one appears
on any row. `--report rdb-quest-catalog` prints this census on every run, cross-checking each observed tag
against the tags the generated classes declare — so a field we parse straight past would show up as
`⚠️ UNDECLARED` rather than going unnoticed.

[V1: Faolan.Importer/Cli/ReportCli.RdbQuestCatalog.cs — top level is `version` ×1, `main_quest` ×2183,
`quest` ×5050, `quest_goal` ×7327 and nothing else; 0 unparseable submessages, 0 trailing bytes.
`QuestGoal` carries exactly fields 1–5 on **all 7,327** rows — `template_id`, `hash`, `name`,
`quest_template_id`, `phase`.]

⚠️ That is the answer to "could the catalog supply a goal's missing definition?" — **no**, and mechanically so
rather than by not having looked. A `QuestGoal` row is identity + name + parent + phase; it carries no verb, no
target, no required count and no marker block, and there is no unaccounted space in the record for them to hide
in. The two `Partial` goals of #527 therefore need a capture that HOLDS them across a login, not another pass
over this table.

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

**Step order WITHIN a line is not in this table** — no field carries it, and neither does the row order.
`Quest.tier` and `start_new_tier`, the two fields shaped for exactly that, are empty. A line gives you its
first and last step and its membership; the order of the ones between is still hand-authored
(`QuestGoalWiring.Chain`).

The row order deserved a test of its own, because a protobuf's repeated field preserves the order it was
written in and no *field* being present does not mean no *order* is. It fails. `--report rdb-quest-catalog`
scores the catalog's row order per line against the captured `JournalSequence` (the FullChar completed-quest
journal's explicit `[seq][questId]` pairs), over the 724 lines where every step has a captured sequence:

| ordering | lines matching the captured order |
|---|---|
| catalog row order | 381 / 724 (52.6%) |
| ascending `template_id` (control) | 646 / 724 (89.2%) |
| reversed row order (control) | 233 / 724 (32.2%) |

Row order loses to *sorting the ids*, which settles it. The length breakdown is what makes the result
unambiguous rather than a wash: 2-step lines are a coin flip and carry the headline (574 of the 724), and as
lines get longer row order collapses — 23.5% at 3 steps, 5.9% at 5, and **0 of 20 at 6+ steps**, where chance
is negligible. Ascending id decays too (93.6% → 50.0% over the same buckets), so it is a *correlation* from ids
having been handed out roughly in design order, not a rule: Shipwrecked (line 241) runs
`10238, 12369, 12373, 10320, 12371, 10286, 10287, 10915`, where a later revision inserted `12xxx` steps between
the original `10xxx` ones. It is not imported, and an 89% guess must not be written into a column that reads as
fact.

**So the step order is server-side data.** Retail shipped it on the wire and kept it out of the client's
catalog, which is consistent with the client never needing it offline. Captures are the only source, and they
carry it in THREE places (#926): the completed journal states a finished step's sequence, the quest INDEX block a
character gets for every quest it HOLDS carries its whole line's `(questId, sequence)` list, finished or not, and
the `MainQuestUpdateIIR` a grant sends mid-session fills the same `MainQuest_c` pair vector (`LineOrderExtractor`
reads the last two; `LineOrderProducersTests` gates that they agree). None disagree (233 steps against the journal,
0 conflicts), and the block orders a line from a character who merely picked up a quest on it - 113 sequences and
47 lines the journal alone had not reached, 771 fully-ordered multi-step lines as of 2026-09-03. The mid-session
list added 2 steps and no rows in this corpus (every captured grant was followed by a relog), so it pays on the
next capture. More coverage means a capture of a character who holds, accepts or completed those lines, not more
RDB work.

### ⭐ On a PARTIAL line, the `1` is a real head marker and the `0`s are merely unwritten

A line can carry a sequence on some members and 0 on the rest. Twenty-four do. Those 0s are not a position:
checked against an independent step listing ([`quest-steps-wiki.md`](quest-steps-wiki.md)), on every line where
exactly one member is at `JournalSequence = 1` and all others are 0, that member is the listed first step —
**5 of 5, none against**.

⚠️ So anything ordering a line's members must sort **unknown LAST**. Sorting 0 ahead of 1 reverses exactly these
lines, silently, because the fallback looks like a valid ordering. `QuestMarkerResolver.LineReached` is already
right (it skips the gate when either side is 0); this is a note for anything written next. The 24 partial lines
are also the cheapest to finish, since each already names its own first step.

## The reward fields do NOT name the journal record's scalars

`MainQuest` carries exactly one reward pair, `xp_reward` (15) and `cash_reward` (16), populated on only 23 and
18 of its 2181 lines.

⚠️ **This document previously claimed that pair NAMES the `Quest_c` record's `@+0xf0`/`@+0xf8`.** An in-client
test disproved it — the journal's `XP:` comes from `@+0x28`, and `@+0xf0`/`@+0xf8` drive neither `XP:` nor
`Money:`. See [questc-region-decode.md](../re/questc-region-decode.md).

### …and the magnitude coincidence that motivated it is explained (#759)

⭐ **`45800` is a ZONE figure that appears on several lines, not an identity.** The three lines carrying it are
`[Thunder River] Voyage of the Dead` (1961), `Turning Point` (1963) and `Pools of Blood` (1971) — and **none of
them is the line either quest 13895 or 14427 sits on** (1734 and 1976). Both of those quests are Thunder River
content too, so the two numbers share a design budget and nothing else.

The catalog cannot settle the reading either way, and the report now says why rather than leaving it hanging:
its reward field is populated on **26 of 2,181** lines, our 31 `@+0xf0` carriers sit on 31 *other* lines, and
the two sets are **disjoint — which is what chance predicts** (31 × 26 / 2181 = 0.4 expected overlap). So
`0/31` agreement is an underpowered test, not a refutation. `--report rdb-quest-catalog` prints the whole
populated set so the next reader does not re-derive it.

[V1: Faolan.Importer/Cli/ReportCli.RdbQuestCatalog.cs — the `Quest_c @+0xf0/@+0xf8 vs the catalog's LINE reward`
block.]

What the same pass *did* establish is positional, and it is the complement of `@+0x28`'s law: over the 14
carriers whose line is fully ordered by a captured `JournalSequence`, the carrier is the **FIRST** step of its
line 12 times, the **LAST** 0 times, and a middle step twice. `XpReward` (`@+0x28`) sits on the LAST step (25 of
26 captured cases). And the value is **not repeated** on the line's other captured steps: 0 siblings carry it,
4 carry 0. So it is single-step like `XpReward`, at the other end of the line — not a line attribute stamped on
every member.

⭐ **That measurement now runs over ALL 31 carriers**, not the 14 a captured sequence orders: taking the line's
own chain ends (`QuestLines.FirstQuestId` / `LastQuestId`, which the catalog gives for every line) the carrier
is the line's FIRST quest **27 times, a middle step 4 times, and its LAST quest ZERO times**. `XpReward` over
the same measure is the LAST quest **20 of 21**. No exception in either direction.

⛔ **But they are opposite ends of DIFFERENT lines, which is what "at each end of a line" gets wrong** (#802).
`TwoPairExclusion` counts the two pairs per LINE rather than per quest, and the reading that a line advertises
at its head and pays at its tail predicts BOTH on nearly every reward-bearing line:

```
per QUEST  over the 100 captured: @+0xf0 only 30, @+0x28 only 20, BOTH 1   (chance predicts 6.5 overlaps)
per LINE   over the  95 captured: @+0xf0 only 30, @+0x28 only 20, BOTH 1
```

So a line carries **one** reward, in one slot pair or the other; the positional law says *which slot a line
uses*, and correlates with where in the line that line puts it. What decides the choice is the open question.

[V1: Faolan.Importer/Cli/ReportCli.RdbQuestCatalog.cs — the `TwoPairExclusion` block.]

⭐ **And the one record that carries BOTH pairs holds the same number twice.** `The Scavenger` (13479, the head
of the Casilda line) is the only row in the catalog with `@+0x28` and `@+0xf0` both populated, and
`XpReward == FieldF0 == 80`. Its `@+0x20` money slot is 0 while `@+0xf8` is 7 — a 0.0875 ratio, which is what
the `BandLevel`-keyed formula gives at `BandLevel = 3`. So on the single available comparison the two pairs are
**the same quantity stated twice**, which is the strongest evidence yet for the line-reward reading and rules
out their being different magnitudes that merely share a formula.

⚠️ It is ONE row. It cannot distinguish "the line's reward restated at its head" from "the step's reward
written into two slots on a quest that happens to be both".

⛔ The remaining candidate the ticket named — the quest-giver's **advertised** reward, surfacing as
`QuestGiverList.MainQuestReward` — is answered negatively by the catalog itself: that list is the **item**
reward set (imported as `QuestLineReward`, its `type` the item's `SearchCategory` on 1,635 of 1,635 resolvable
rows, populated on 1,098 lines required + 619 optional). It carries no xp or money amount, so an advertised
money+xp pair cannot surface there.

The low-level cash/xp ratio agreement stands (catalog 715/60, 1000/157, 455/36 → 0.08–0.16 against the wire's
150/15, 370/44, 550/66 → 0.10–0.12), and it is a statement about the FORMULA both are generated by — the one
keyed on `BandLevel` — not about the fields being the same field.

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
