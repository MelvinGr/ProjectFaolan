# Faolan.Importer — Module-Based Import

`Faolan.Importer` rebuilds `faolan.db` from the capture sources (the DB is the single source of truth; the
server carries no embedded data blobs). It is **module-based**: every importable subsystem is a selectable
`IImportModule`. `Program.Main` delegates straight to `ImportCli.RunAsync`; `--list` shows every module and
`--dry-run` writes nothing (it runs the real module code in a rolled-back transaction and prints the exact
per-table delta).

`WriteMode` is honoured at the orchestration level (npcs/conversations/map-loot); the leaf `CaptureX`
importers still do their inherent upsert for `insert`/`upsert`. Modules advertise their `SupportedModes`
and warn when a requested mode is not fully threaded through.

## Project layout

`Program.cs` is the entry point only. The rest is organised by role (all one `namespace Faolan.Importer`;
the folders are organisational):

| Folder | Holds |
|---|---|
| `Cli/` | `ImportCli` (parse + load + migrate), `ImportRunner` (transactional run + delta), `ModuleRegistry`, `ImportModule`/`ImportContext` abstractions, `ReportCli`, `TableSnapshot`. |
| `Modules/` | The `IImportModule` definitions (one class per `--only`/`--skip` token). |
| `Captures/` | Capture reading (`TsharkCaptureReader`, `NamedCapture`), the shared `ImportOps` orchestration, and the per-subsystem `CaptureX` importers. |
| `Extractors/` | Packet-grammar extractors (conversations, NPC spawns/spells, patrol paths, quests, world objects, per-map loot pools, item-hash harvesting). |
| `Rdb/` | Client-RDB readers/importers (opt-in, require `--rdb`). |

## Module abstraction

Every import is a self-describing module:

```csharp
enum ImportSource { Captures, Rdb, Tsv, Db }   // what inputs the module needs

interface IImportModule
{
    string Name { get; }                       // CLI token, kebab-case, e.g. "npcs", "world-objects"
    string Description { get; }                 // one line for --list / dry-run header
    ImportSource Source { get; }                // gates the module if its input is absent
    bool InDefaultSet { get; }                  // false = opt-in (RDB, mounts)
    IReadOnlyList<string> DependsOn { get; }    // module names that must run first
    IReadOnlySet<WriteMode> SupportedModes { get; }
    Task<ImportResult> RunAsync(ImportContext ctx);
}

sealed record ImportResult(string Summary);     // the one-line "X seen, Y new, Z updated" each module prints
```

`ImportContext` carries the shared per-run state: the tracked `DatabaseContext`, the captures (read once),
the captures/RDB dirs, the raw packet bytes, the chosen `WriteMode`, and the `DryRun` flag.

`ModuleRegistry.All` lists every module in default-run order. The runner resolves the requested set (plus
dependencies), topologically sorts by `DependsOn` (registration order is the stable tie-break), and runs them
in order, printing each `Summary`.

⚠️ `buff-durations` is registered **last of all**, after the RDB group, and that placement is load-bearing:
`rdb-spells` writes `Spells.DurationMs` unconditionally, so a capture-sourced duration for the rows its property
run leaves empty has to be applied after it. `RdbSpellsModule.RequiredAfter` names it for the same reason, so
`--only rdb-spells` pulls it in rather than leaving those rows back at 0/1 ms.

## Module registry (one row = one `--only`/`--skip` token)

Capture-sourced (default group, run in dependency order):

| Module | Depends on |
|---|---|
| `listings` | — |
| `stock-phrases` | — |
| `items` | — |
| `maps` | — |
| `spells` | — |
| `dot-effects` (per-tick delta + the measured tick interval) | `spells` |
| `spell-effects` (pet summons + the fatality proc; DB-sourced, needs no captures) | — |
| `abilities` (+ the rehomed ability-slot seed) | — |
| `class-spells` | — |
| `characters` | `items` |
| `conversations` (clean rebuild, overwrite-only) | — |
| `character-state` (wallets, hotbar) | `characters` |
| `learned-spells` | `characters`, `spells` |
| `character-abilities` (+ combo books) | `characters`, `abilities` |
| `character-quests` | `characters`, `quests` |
| `character-bank` | `characters`, `items` |
| `shops` | — |
| `npcs` (spawns + types + patrol + npc-spells + spell links) | `items`, `spell-effects` |
| `world-objects` | — |
| `world-teleports` (zone-line door destinations: captured use→teleport sequences, then the rest by name against the playfield table — [teleport-changemap.md](../subsystems/teleport-changemap.md)) | — |
| `map-loot` (per-map loot pool, overwrite-only) | `items` |
| `quests` (definitions + the hand-decoded seed steps) | — |
| `buff-durations` (the duration retail STATES, for rows whose RDB record leaves property field 8 empty — #659) | `spells` |

Opt-in capture-sourced (run only when named via `--only`): `mounts`, `pets`, `recipes`. ⚠️ `mounts` harvests
the mount CREATURE models and selects them against the mount CATALOG, which `rdb-spells` imports — so run
that first on an empty database, or the module fails with that instruction rather than silently finding
nothing ([mounts.md](../subsystems/mounts.md)).

RDB-sourced (require `--rdb`, **not** in the default group — opt-in via `--only`):
`rdb-quest-catalog` (the client's quest catalog, RDB 1000104 — quest lines/quests/goals + their plaintext
names; see [quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)),
`rdb-inventory` (read-only type-count report), `rdb-morphs`, `rdb-appearance`,
`rdb-spells` (the spell catalog + the 155-row mount catalog, [mounts.md](../subsystems/mounts.md)),
`rdb-abilities`,
`rdb-combos`, `rdb-maps`, `rdb-respawns` (+ CDN fetch), `rdb-items` (template decrypt),
`rdb-hazards` (lava volumes), `rdb-regions` (the bounded-area trigger volumes a subsystem consumes — depends on
`rdb-respawns`), `class-attributes` (the per-class/level attribute + health BASE, #444 — see below).

TSV-sourced: `starter-spells` — the create-flow starter kit, registered late so it lands after the catalog.

⚠️ **The scrape modules are gone** (`ellatha-levels`, `ellatha-combos`, `wiki-class-spells`,
`wiki-spell-levels`). The client leads, and it turned out to carry both facts they stood in for: a spell's class
MEMBERSHIP is its class-encoded `GuiStackingLine` (`rdb-spells`, all twelve classes) and a combo's REUSE TIME is
the combo XML's own `cooldown` (`rdb-combos`). See Gitea #296.

### Seed data has an explicit home

The hand-decoded constants the extractors can't recover live **inside their owning module** as commented code
constants: the quest seeds (`SeedBeachQuestGoals`, `SeedCasildaChain`, `SeedCasildaQuestSnapshot`) run as
sub-steps in the `quests` module; `UniqueNpcHomes` lives in `npcs`; the 6 ability-slot constants live in
`abilities` (rehomed from the deleted archive importer); the pet-summon roster and Frozen Hatred's fatality
proc live in `spell-effects`. Externalising them to `data/seeds/*.json` is a possible later refinement, not done.

## Write modes (`--mode`)

The write strategy is an explicit, selectable dimension — orthogonal to *which* modules run:

| Mode | Semantics |
|---|---|
| `insert` | Add only rows whose key is **not** already present; never modify or delete. Safe to re-run on a populated DB. |
| `upsert` *(default)* | Update existing rows in place by key **and** insert new ones; never delete. |
| `overwrite` | Replace the module's **owned** data wholesale for the keys it touches: delete-then-rebuild, then insert. |

Rules and edge cases:

- **Per-module support.** Not every module supports every mode (`SupportedModes`). Conversations are *always*
  a clean rebuild (handle-keyed stale rows must be purged), so it advertises `overwrite` only. Characters
  are **insert-only** today (`SupportedModes = {insert}`, existing names never overwritten); adding
  `upsert` and an `overwrite` guarded by `--force` (it cascades to 12 child tables incl. live player
  progress) is the tracked step-2a follow-up.
- **Scope of `overwrite` = the module's own tables only.** It must never touch another module's data. The NPC
  reseed models this (it wipes only `NpcSpawns`/morphs/equipment/patrol, leaving `NpcSpells` and the other
  modules' tables intact). The committed seeds (e.g. the beach burst 4010) stay exempt.
- **Per-module override.** Global `--mode` sets the default; `--mode npcs=overwrite,items=insert` overrides
  individual modules in one run.
- **Default stays `upsert`** so a no-flag run reproduces the committed-DB workflow with no surprises.

## CLI specification

```
dotnet run --project Faolan.Importer -- [paths] [selection] [modifiers]

Paths
  --Database:ConnectionString <s>  override the connection; default: faolan.json Database:ConnectionString (e.g. Data Source=faolan.db)
  --captures <dir>     default: <repo>/Captures
  --rdb <dir>          client RDB dir (enables rdb-* modules)

Selection (mutually exclusive; default = every capture + tsv module)
  (no flag)            the default set: every InDefaultSet module (capture + tsv). RDB and the opt-in
                       capture modules (mounts/pets/recipes) are NEVER auto-included — name them with --only.
  --only a,b,c         run just these modules (+ their dependencies), comma-separated
  --skip a,b,c         run the default set minus these
  --list               print every module name + description, then exit

Modifiers
  --dry-run            run everything, print the per-table delta, write NOTHING
  --mode <m>           insert | upsert (default) | overwrite — global write strategy
                       per-module override: --mode npcs=overwrite,items=insert
  --force              permit overwrite on guarded modules (e.g. characters)
                       (parsed onto ImportContext.Force; no module consumes it yet — step-2a)
  --rdb-proxy <url>    Faolan.Web.Content mirror base for on-demand (le.idx file==0xFF) RDB resources
                       (default http://127.0.0.1:7081/gameupdate/upm/rdb) — every rdb-* module fetches
                       CDN-only records through it, and the proxy store-and-forwards so pulls are archived
  --dump-templates <d> rdb-items: dump decrypted templates (module-specific)

Reports (read-only, bypass the import runner)
  --report <name>      e.g. conversation-states | anim | worldobj | spell-fields | rdb-assets
  --report-arg <v>     the target a report needs (NPC name / id / ...), e.g. --report anim --report-arg Casilda
```

## The `--dry-run` mechanism

The runner runs the **real** module code inside a DB transaction and rolls it back, so there's zero per-module
dry plumbing and the delta is exact:

```csharp
var before = await Snapshot(db);
await using var tx = await db.Database.BeginTransactionAsync();
foreach (var module in plan) Console.WriteLine(await module.RunAsync(ctx).Summary);
var after = await Snapshot(db);
PrintDelta(before, after);          // the before/after table
if (dryRun) await tx.RollbackAsync();
else        await tx.CommitAsync();
```

- SQLite honours the rollback across every module, including the `ExecuteSqlRaw` DELETEs used by the NPC
  reseed, so `--dry-run --only npcs --mode npcs=overwrite` accurately shows "removed N, added M" then reverts.
- `TableSnapshot` counts **all** EF-mapped tables (so new tables are picked up automatically) — the delta is
  complete.
- The two modules with a side effect outside the DB guard on `ctx.DryRun` and skip the disk write under
  `--dry-run`: the CDN-fetching `rdb-respawns` (writing blobs to the CDN mirror store) and `rdb-items` (dumping decrypted
  templates to `--dump-templates`). These are the only two places a module reads the dry-run flag.

### `class-attributes` — the class/level attribute base (#444)

Fills `ClassAttributeBases` from RDB type 1000020, where the base lives as 80 PLAINTEXT templates per class — one per
level. The level comes from the template, not from its id: each names itself `"<Class> Class Feat <NNN>"` where `NNN`
is the first level of a ten-level block, and rank-by-attribute-sum inside the block gives the rest.

⚠️ **`templateId = classBase + (level − 1)` is wrong** — the eight blocks sit inside a class's id range in an
arbitrary per-class order. That rule was the first cut and it silently imported the Priest of Mitra's level 61–70
values as levels 11–20. The full decode is in the remarks on `ClassAttributeBaseModule` and in
[`docs/subsystems/player-stats.md`](../subsystems/player-stats.md).

All 12 classes resolve at all 80 levels, and the module verifies its result against every hardcoded **client
observation** (`ClientObservations`: a class, a level, and the template that character was seen loading) rather than
using them as an input. A missing `(class, level)` is treated as unknown, not zero, and world entry logs a warning
naming the character. `--report class-attribute-runs` prints the per-class block layout and the observation checks.

## Diagnostics (`--report`)

Read-only inspection modes are **not** imports and don't share the import runner. They are routed through
`--report <name>` (`conversation-states`, `anim`, `worldobj`, `spell-fields`, `rdb-assets`,
`class-attribute-runs`, `item-stat-scaling`, `rdb-dump` (takes `<type>` or `<type>:<id>`),
`bcc-attack-tail` (the attack tail's middle block word by word, under the Command slot the client's reader
puts each in, with the direction ability resolved — #661) ·
`bcc-census` (every BehaviorControlCenterIIR bucketed by command, with the tail lengths each carries — the census
[behaviorcontrolcenter-grammar.md](../re/behaviorcontrolcenter-grammar.md) quotes; `--report-arg <phase hex>` also
dumps every record of one phase in full),
`player-actions` (every `PerformSpecialAction` 0x205A selector in the corpus, NAMED out of the `Abilities`
catalog — the selector is the ability's grant `Slot` — with the server's answering BCC records and IIRs.
`--report-arg <selector hex>` narrows to one and prints those answers in full, which is the spec a server-side
stance has to reproduce; see [player-actions.md](../subsystems/player-actions.md)),
`combo-swings` (every melee-combo swing in the corpus, resolved against the `Combos` catalog — the census
[combos.md](../subsystems/combos.md) quotes: which combo, which step, the `bcc_action_id`→direction map and the
player/NPC split. `--report-arg <file>` also writes the matched packets whole, for `--report fixture-set`),
`npc-chatter` (the ambient-bark catalog's reach, #331 — the category-50015 page census, every stored `NpcChatter`
row resolved to its TEXT through the client's own token hash, and a per-group SWEEP of `sayinchat(N)` listing every
line the client ships for that group with the ones the DB lacks marked, split by whether they carry a `{Slot}`;
see [chat.md](../subsystems/chat.md)),
`bounded-areas` (the client's trigger-volume survey — no arg prints the census, `--report-arg <bucket>` lists one
bucket and cross-checks it against what we already hold; see
[bounded-areas-1000053.md](../rdb/bounded-areas-1000053.md)),
`field-stability` (for every field of the captured NPC `CoreCharacter` message: is its value a property of the
TYPE, of the PLACEMENT, or of neither? Two `GROUP BY` tables over retail's own login-burst spawns — by
`npc_stats.TemplateId`, then by physical map+position. A field no more stable at placement scope than at type
scope is rolled per SPAWN EVENT and no column can hold it, which is how `VoiceSet` (9.7% / 11.7%),
`AnimIdleTimer` (50.2% / 45.2%) and `MovementMode` (59.0% / 60.6%) were told apart from the vitals block
(73.7% / 90.8%), whose `NpcSpawn` columns are the right home. A third table then gives the VALUE DISTRIBUTION of
the fields the first two condemn — knowing a field is rolled per event says a column cannot hold it, not what we
should send instead. ⚠️ The key is the TemplateId and not the name — a name spans several templates, and a field
cannot be less stable than the key it is grouped by. ⚠️ Read the third table by FREQUENCY, never by distinct-value
count: 92 of `AnimIdleTimer`'s 94 distinct values lie strictly between 0 and 1, which reads as a continuous
animation phase, but they carry only 24% of sightings — the field rests at exactly 0 or 1 and is merely caught
mid-blend, and flattening it to one value cost 167 byte-exact rebuilds before the frequency view caught it),
`field-predictors` (the question `field-stability` raises but does not answer, for the fields it condemns: a value
rolled per spawn event belongs in no column, but it still has to be SENT — so is it COMPUTED from something else the
same spawn already carries? `--report-arg <field>`, default `MovementMode`. Groups every sighting by each candidate
and scores the share that agree on the target, against the baseline a constant would score. Candidates are the
character message's fields PLUS synthetic ones off the spawn's MOVE TASK, which is the only part of a spawn that says
what the entity is DOING. This is what found that the post-IIR header's third word IS `movement_mode` — 5,738 of
5,739 NPC spawns and 209 of 209 player ones — retiring the duplicate `MinimalPostIirWord2` column and taking
`--report spawn-diff` from 336 to 508 byte-exact rebuilds (#605). ⚠️ Read purity together with CARDINALITY: purity
rises for free with the number of distinct candidate values, so a candidate as fine-grained as the sample scores 100%
while explaining nothing. The table marks those `(fine)`; a real answer is COARSE and near-100%. ⚠️ Both this
report and `field-stability` read PRESENCE through `ShouldSerializeX()`, not off the property: protobuf-net
generates `get => __pbn__X.GetValueOrDefault()`, so an unset optional field reads back as 0 and would be scored as
one retail sent. Correcting that is what showed `npc_stats.spawn_animation` is never sent as 0 at all — it is
`(unset)` on 5,265 spawns and `1` on 474, a presence flag rather than a number, #607),
`scaffold-heap` (the RETAIL SERVER's own memory, harvested out of the NPC spawn scaffold: retail writes 3 of the
53 scaffold words on an NPC spawn and sends the other 50 carrying whatever was there, so 17,400 captured spawns
ship 3.3 MB of server-side data. Yields the script engine's ScriptGroup catalogue and C++ source-file inventory,
retail's `Universe[1]/Territory[1]/Dimension[N]/GameServer[M]` config tree and its Oracle SQL, plus the odd LDB
string — `--RdbDir` is OPTIONAL and only decrypts the strings it finds. `--report-arg runs` lists every distinct
string, `dump` writes a hex dump of every block. ⚠️ Counts are taken per PRINTABLE RUN and anchored on a closing
delimiter, because a regex run over the whole 228-byte buffer crosses the gaps between leaked strings and
manufactures roughly half its findings; see [retail-server-heap-leak.md](../re/retail-server-heap-leak.md)),
`rdb-quest-catalog` (the client's OWN quest catalog, RDB type 1000104 — 2181 quest lines / 5042 quests / 7327
goals with the designers' names in plaintext. No arg prints a summary, a populated-field census and three flat
TSV sections; `--report-arg <line or quest id>` prints that line as a tree. It also cross-checks `MainQuest.hash`
against the four `ACGHash` line tokens recovered from captures, which is what establishes that our `QuestLineId`
IS the client's `MainQuest.template_id`; see [quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)),
`shield-rose` (every `CombatShieldIIR` timed against its own defender's spawn — which is what turns "no capture
covers a catch-up send" into a measurement: of 71 first-roses, none arrives within 2 s of that handle's spawn, so
retail sends none either, #622),
`dynel-cycle` (every world-object spawn/despawn BURST with the player's position at the time. Rules a single
interest RADIUS out one-sidedly — `R` must satisfy every `farthest-spawned ≤ R ≤ every nearest-despawned`, and
the corpus despawns at 3.5 u while spawning at 1,834 u — then scores candidate CELL sizes against the right null,
the same statistic over every position the player walked. ~32 u fits, #529),
`chain-kill` (every `ChainKillIIR` with the gap to the previous one and to the kill counters `106` / `1000069` /
`1000070`. Gives the chain's whole grammar — counts 2/3 and never 1, every non-zero closed by a 0 a median 8.97 s
later — and shows the counters never move with it, #446),
`dot-trains` (the EVIDENCE behind every measured DoT tick interval: each `(spell, target, caster)` tick train, its
length, and the gaps it contributes — the exact input `DotIntervalEstimator` is handed. Train LENGTH is what the
pooled gap list hides: a length-2 train contributes one gap that is a re-application phase offset as often as it
is the period, so a spell whose gaps come mostly from those has no cadence whatever the pooled list looks like.
`--report-arg <name substring or id>` drills one spell and prints its train lengths and gaps, #524; `--out <file>`
writes the same input at full precision as one `(spell, gap)` row per line, which is what makes "re-run a candidate
acceptance rule over every spell at once" a diff rather than an argument — the gate #657 asks any new rule to pass),
`buff-durations` (every captured `MagicBuffIIR` apply grouped by spell, with the durations retail stated and the
`Spells.DurationMs` we build from — the evidence view behind `MagicBuffCorpusTests`' duration gate. It separates
the two populations a total hides: a SHORTFALL that decreases across a capture is retail re-sending a long buff
with its remaining time (#601), while an EXCESS cannot be a remainder and is therefore a wrong row (#659).
`--report-arg <name substring or id>` drills one spell and prints each apply with its capture and offset),
`spell-refs` (`--report-arg <spellId>[,...]`: every spell record whose GameAction element region NAMES the given
spell, printed by key — the inverse of `spell-template`. A buff whose own record is nearly empty has its mechanics
stated by whatever APPLIES it, and that applier is only findable by scanning the corpus for the id), … — the report's
target comes from `--report-arg`), read captures or the DB, and never write — independent of `--dry-run`.

## Decisions

1. **RDB modules are opt-in.** Not in the default set / `--all`; only run when named via `--only` (and they
   require `--rdb`). Matches the deliberate one-off RDB workflow.
2. **Seeds stay inline.** The hand-decoded quest/NPC constants live in their owning modules as code constants;
   externalising to JSON is a possible later refinement, out of scope.
