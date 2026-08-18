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
| `Captures/` | Capture reading (`TsharkCaptureReader`, `NamedCapture`), the shared `ImportOps` orchestration, `QuestGoalWiring` (the post-import pass that decides each quest goal's trigger/count/target), and the per-subsystem `CaptureX` importers. |
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
| `chat-itemref` (item identities + TEA keys from `itemref://` chat links, #751) | `items` |
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
| `class-spells-known` (class↔spell MEMBERSHIP derived from the captured spellbooks — the third source, for the ~85% of spell records carrying no stacking line, #851) | — (registry position orders it; naming `characters` would drag its destructive rebuild into an `--only` run) |
| `character-abilities` (+ combo books) | `characters`, `abilities` |
| `character-quests` | `characters`, `quests` |
| `character-bank` | `characters`, `items` |
| `shops` | — |
| `npcs` (spawns + types + patrol + npc-spells + spell links) | `items`, `spell-effects` |
| `npc-placements` (pool the stationary spawn rows the corpus proves are one NPC seen twice — #718) | — |
| `npc-combo-class` (the NPC combat ARCHETYPE — which `combo_npc_<class>_*` family a template swings, from the captured combo swings — #675) | — |
| `npc-ambient-animations` (per-creature idle animations from the captured `Emote` records — #766) | — |
| `npc-follow-pairs` (which creature TYPE walks with which, from the captured mode-07 formation MoveTasks — #741) | — |
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
`rdb-achievements` (the achievement catalog behind `DispatchAddTag` — LDB 55701 titles + 55700 descriptions,
keyed by TAG id; see [achievements.md](../subsystems/achievements.md)),
`rdb-combos`, `rdb-maps`, `rdb-respawns` (+ CDN fetch), `rdb-items` (template decrypt),
`rdb-regions` (the bounded-area trigger volumes a subsystem consumes — discovery pads, quest goal regions, area
banners and the lava hazards; depends on `rdb-respawns`), `class-attributes` (the per-class/level attribute + health BASE, #444 — see below).

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
  a clean rebuild, so it advertises `overwrite` only — the fold is a global partition over every harvested walk
  (which screens merge, and which need a `Discriminator`, is decided by comparing them against each other), so
  folding one capture against a database that no longer remembers the rest cannot reproduce the classification.

  ⭐ **How badly that actually bites is measured, not assumed** — `--report conversation-fold-stability` (#643)
  folds the corpus, then folds it again with each capture held out in turn, and counts the node ids that MOVE
  when it is added back. Today: **37 of 37 dialogue-carrying captures are append-stable, zero ids move.**

  ⚠️ And the reason is a fact about the DATA, not about the fold. A node's id is
  `ComputeId(template, contentKey, discriminator)` and the discriminator is the class's **index** within its
  `(template, contentKey)` group, so an append can only move an existing id by renumbering a group that holds
  more than one class. The corpus has **zero** nodes above discriminator 0 — the mechanism that forces
  overwrite-only has never fired. An incremental importer could be built on that, but it would want this report
  in its acceptance path (or a discriminator derived from the class rather than from its position, which removes
  the failure mode outright). Characters advertise
  `insert` (the default: add what is missing, never touch an existing row) and `overwrite`, which is the
  **refresh** (#753): a character whose row carries `Character.ImportedFromCapture` is deleted and rebuilt from
  the current corpus, so a re-dumped capture can update an imported bag / worn set / stat sheet that would
  otherwise stay frozen at whichever dump first built it.

  ⚠️ **The marker is the whole safety rule, and the two obvious alternatives both fail.** "The name appears in a
  capture" exempts nothing (the importer only ever iterates names that do), and the ACCOUNT cannot discriminate
  either, because imported characters deliberately sit on the normal play account. Only the importer sets the
  marker, so a character created in-game cannot carry one and is exempt by construction - which is how a blanket
  re-import lost `Adada` (level 4, local), and why the level threshold that was supposed to be the guard is not
  one. It still discards a player's own rearrangement of an *imported* character's items, so it is guarded by
  `--force` - the first consumer that flag has had.
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
  --force              permit overwrite on guarded modules — `characters` is the one that consumes it,
                       because its overwrite rebuilds every capture-imported character (#753)
  --rdb-proxy <url>    Faolan.Web.Content mirror ADDRESS for on-demand (le.idx file==0xFF) RDB resources —
                       just the host, e.g. http://127.0.0.1:7081; RdbReader owns the /gameupdate/upm/rdb route.
                       Defaults to the shared `Cdn` config (PublicAddress, else the local mirror on its
                       ListenPort - never ListenAddress, which is an interface), so a mirror on
                       another box is one line in the gitignored faolan.user.json rather than a flag on every
                       command. Every rdb-* module AND every
                       --report fetches CDN-only records through it, and the proxy store-and-forwards so
                       pulls are archived. ⚠️ A reader built WITHOUT it degrades to the local records only,
                       which for several types is a minority of the game (577 of the 817 NPC spawn layouts,
                       580 of 653 in-game map tiles, 276 of 520 tilemaps). Gate on `rdb.CanRead(entry)`,
                       never on `entry.IsLocal`.
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
`--report <name>` (
`codec-sweep` (#809: EVERY codec against EVERY captured record of the IIR type it declares, across all the dumps —
`Parse`, re-`Build`, byte-compare. Two findings and they differ: **REFUSED** = the codec models a shape retail does
not send; **MISMATCH** = it parsed the body without accounting for all of it, so a field is being dropped. This is
the whole-corpus form of the `--report timeline` footer that found #809's four, and it is a REPORT rather than a
test on purpose — `CodecSurfaceConventionTests.BuildParseRoundTripsOverTheCorpus` asks the same question of
`fixtures.bin`'s frozen slice and would fail the moment a capture is added. `--report-arg <IirTypeName>` narrows to
one type and dumps every offending body in full),
`conversation-states`, `anim`, `worldobj`, `spell-fields`, `rdb-assets`,
`class-attribute-runs`, `item-stat-scaling`, `rdb-dump` (takes `<type>` or `<type>:<id>`),
`spell-flags` (#632: sweeps the spell record's FLAGS word bit by bit against what the DB knows, so "which bit says
X" can be asked rather than argued. Bit 2 is printed as the CONTROL - it is a known predicate and scores 100.00%,
which is the yardstick every other row is read against),
`spell-element-lists` (#785: scores the element-LIST grammar over the whole spell catalog - a spell record's
elements come in count-framed groups, `[2][listId][(n+1) * 0x3F1]`, and this is the gate on that. It cannot be an
xUnit test because the suite runs from a clone with no RDB. `--report-arg kinds` adds the per-id kind breakdown),
`rdb-word-scan` (every RDB record containing a given 32-bit word, by type - the scan our property readers CANNOT do,
since they all bound a field id to <= 2000 and anything above is invisible to them by construction. LOCAL records
only unless `:cdn` is passed, because a CDN entry is an HTTP fetch and a whole-RDB sweep would pull ~250k of them),
`acg-pair` (#752: every ProtoAcgItem in the dumps, tabulated by how template2 relates to template1, with BOTH halves
of each differing pair decrypted and diffed field by field - they differ only in LEVEL, so the pair is a
low/high endpoint range),
`gem-slots` (#754: the gem model out of the item template - `NumGemSlots` (114), `GemSize`, the four
`GemSlot<N>Color` fields and the gem's own `GemColor`/`GemTier` - cross-checked against every socket the dumps show.
Three items carry more gems than they declare, which is retail's own LEGACY overflow state, not a decode failure -
the client has a `GemOverflow` token and link for exactly it),
`spawn-item-level` (#755: what LEVEL a character spawn puts on each worn item, measured against `Items.Level` over
the dumps - 125,721 of 125,722 entries agree, so the spawn carries the item's own level exactly as the snapshot
entry does),
`item-binding` (#756: the client's own bind-on-equip/pickup rule evaluated over every decryptable item template,
the `Flags` bit census, and the wire's arrival `Flags` from the DUMPS against the template's own — the report the
[item-template-stats](../rdb/item-template-stats.md) binding section is measured with),
`bcc-attack-tail` (the attack tail's middle block word by word, under the Command slot the client's reader
puts each in, with the direction ability resolved — #661) ·
`npc-pose-check` (#729: does every shipped `NpcStatCores` row carry the pose and idle animation the type is USUALLY
caught in? A `bcc_state` / `anim_idle_id` is the MODE over a type's sightings, so a type whose one captured
representative was mid-emote ships that emote as its permanent pose — the Citizen shipped one its own sightings
outnumbered 57:1. ⭐ It is a report and not a test *because* it compares importer OUTPUT against importer INPUT: as a
test it had to read the input through `fixtures.bin`'s frozen corpus half, and the two populations diverge the moment
captures are added — on 2026-08-08 the extract called the Knight's shipped `anim_idle_id` an outlier at 12:2 from a
sample of fourteen while the shipped value came from a run over captures the extract had never seen. Exit code 1 when
something is flagged, so it can gate a re-import) ·
`spell-level-ladder` (`--report-arg <family substring>` for the per-rank detail: which spell FAMILIES ship a per-rank level ladder and which ship one level for several ranks,
so that a single ding teaches two, three or six ranks of the same spell at once. `Spells.Level` comes from RDB field
125, which states the ladder for 103 of the 122 learnable rank families and states none at all for a few — every
rank of `Chill` read 7, `Lifestrike`'s five read 2/3/4/4/4, and a level-4 Necromancer was handed Lifestrike ranks 3,
4 and 5 in one ding. `RdbSpellImporter.RankLadderFromEffectBands` repairs the families where the record answers a
second time (the `InterpolatedHit` ramp's level band) completely and monotonically; this counts the residue it
cannot, rather than leaving it invisible. ⭐ A report and not a test for the #729 reason — both sides are shipped
DATA — so the populations PRINT and the one assertion is the direction that can only be a defect: a family the rule
WOULD separate whose shipped rows are still collapsed, i.e. `faolan.db` predates the rule. ⭐ It also DIAGNOSES each
residual collapse against the TEACHING FEATS, which split the 19 into two structural shapes — an *untaught top rank*
(no feat grants it while every rank below has one, and it repeats the level below; all nine sit in one late id block
≥ 4487431, and the captured level-80s know the rank below but never it) and a *rank 1 from a class feat* (rank 1's
only grant is a `<Class> Class Feat`, which states no band, so field 125 copied band-granted rank 2's level onto it).
See the field-125 section of [spell-record-fields.md](../rdb/spell-record-fields.md)) ·
`spell-grant-check` (does the level path offer a captured character the spellbook retail gave them? Runs the
SHIPPING queries — `GetStartingSpells` and `GetStartingBarSpells` — for every character in the DB against its own
captured known-spell list, and splits the errors: EXTRA (offered, not known) is printed because the population moves
with the corpus; MISSING is split into *never on offer* (absent from `ClassSpells`, or `Level 0` — a catalog gap)
*held below their stated level* (retail granted it earlier than the client's record says), *no teaching feat* (a
feat-harvest gap) and *bought in the feat TREE* (a different mechanism) — leaving DROPPED, which is the only
direction that can only be a defect. It is the standing measurement behind the per-class feat gate in
[combos.md](../subsystems/combos.md). ⚠️ It CANNOT be a test even in principle: `faolan.sql` excludes every
`Character*` table as per-developer play state, so the suite's fixture holds zero `CharacterSpells` rows and any
such gate there passes vacuously. It caught a real one on its first run — the default bar seeded
`Pestilential Blast (Rank 1)` for a level-4 Necromancer whose book did not hold it, because the bar re-derived its
own candidate set instead of seeding from the book. ⭐ It also carries the three **captured-record exclusions**
(#849), each of which used to be a test that could not fail: no captured spellbook holds a combo's GameAction or a
book spell's effect, and no captured feat list holds a progression track. Those are ASSERTED rather than printed —
a count moves with the corpus, but a combo turning up in a retail spellbook does not move, it refutes the exclusion
rule itself. An empty oracle returns non-zero rather than reporting success) ·
`feat-autotrain-check` (#862: does the client's own auto-train flag agree with what retail puts in a feat list, and
does one point per rank reproduce the budget those lists spend? Both sides are data neither of which is ours - the
client's featclass table (`FeatClassEntry`, RDB 1000043) against the feat lists harvested off the captured player
spawns. Currently OK on all three captured characters, each spending its level budget EXACTLY (79/79/16) and none
of them holding a feat the client auto-trains. Returns non-zero on a character that does hold one, which is the
only direction that can only be a defect; an unspent budget is printed, since a player may simply not have spent
it. ⚠️ A test cannot do this - `faolan.sql` excludes every `Character*` table, so the fixture holds no
`CharacterFeats` and the assertion would pass on an empty left side) ·
`pet-cost` (#823: which field of a summoned pet's spawn carries the point it costs? The corpus discriminates well
- three cost classes, 89 cost-1 / 13 cost-2 / 53 cost-4 spawns - and ⛔ **nothing carries it**: `generic_stats` is
empty on every pet, and of 64 numeric fields on the character message, 60 present on all 155 samples with 29
varying, none equals the cost. That refutes the ticket's reading that the client costs "the pet it receives".
Prints the field counts as a control, since "no field matches" and "no fields collected" print the same) ·
`magicbuff-descriptor` (#658: where do the MagicBuff apply payload's `descriptor[0]` and `bool2` come from? Both
are near-per-spell, so this cross-tabs them against the spell's RDB record. ⛔ **Both of the ticket's proposed
routes are eliminated**: no FLAGS bit scores above +7 against a +50 yardstick, and no PROPERTY FIELD carries
either — the one 80% candidate, field 54, matches **0 of 46** informative records, so its agreement is entirely
base rate (`descriptor[0]` is 1 on 83% of spells, so a mostly-1 field agrees for free). Also tests the level pair
by within-spell variance, which is confounded by the corpus being a handful of characters and says so) ·
`duplicate-records` (#842: which s2c `StatIIR` records does retail send twice, back to back and byte-identical?
⭐ Answers the question that decides whether that ticket is special — and it is not: a whole family of stats is
**always** sent as a pair (`WaypointType` 336/336, `SoulFragments` 56/56, `GuildID` 38/38,
`MovementMultiplier` 35/35, and the hostile flip itself 3/3), where a doubled count equal to the single count
means one "first" and one "repeat" per event. ⚠️ Adjacency is required rather than mere recurrence, or a
frequently-sent stat would dominate the count) ·
`interest-radius` (#856: how far from the player does retail DESPAWN an entity? Measures the DESPAWN side
because it is immune to the contaminant that defeated the spawn-side attempt - nothing bulk-despawns at login,
so there is no burst to exclude. Four named filters: a despawn arriving in a burst is a ZONE CHANGE (21,736 of
23,765), a stale player position is a teleport gap, a handle that DIED is a corpse timer, and a despawn where the
player was not RECEDING is a lifecycle event - a dismissed pet, a summon, a mount. ⚠️ Receding rather than
distance on purpose: filtering by distance would assume the answer in order to decide what counts as evidence
for it. ⛔ **Verdict is negative and stays that way**: the 69 survivors span 25..850 u, a factor of 35, where a
hard radius predicts a floor with a tight cluster above it. So the corpus cannot state the radius, and 150 u is
neither confirmed nor refuted - it merely sits near the median, which is not evidence) ·
`npc-aggression` (#822: which hostile-COLOURED templates the armed-or-not-humanoid rule leaves passive. Retail
colours civilians red without them ever fighting, so the name colour and the aggro decision are separate facts.
Currently **10 of 352**, and every name in the list should read as a civilian - a mob appearing there means a
template whose weapon was never harvested. A report and not a test per #729: the RULE is gated in
`NpcAggressionTests`, this prints who it selects) ·
`quest-container` (#843: what takes an item out of the quest container `0x71` - the goal that SPENDS it, or the
quest COMPLETING? The two readings make different predictions about the rest of the corpus and this measures
them. Verdict: **7 of the 17 completions that happened while the container held something KEPT their items**, so
"a completed quest clears its container" is refuted rather than merely unchosen. The decisive pair is one chain -
13563 *"Get the Scavenger's Key"*, an ACQUIRE goal, completes holding the key and keeps it, while 13480 *"Free
Casilda"*, the step that USES it, completes and loses it. ⚠️ Prints an ARRIVAL control beside every parse count,
which is what caught the first run reading `.Record` where an RPC body wants `.Body` - 187 packets arrived, 0
parsed, and the report printed a confident zero) ·
`quest-markers` (#848/#857: what retail's quest-marker byte means, scored against every relation the shipped
data holds — and, at the end, what the SHIPPING rule would send, by calling `QuestMarkerResolver.LightsSilver`
directly rather than restating it, since a rule measured in one implementation and shipped in another has no
measurement. Scoped to the packets that rule actually decides: the giver fold owns anything with a quest still
on offer, and scoring those measures a branch the server never reaches) ·
`world-object-items` (#824: which world objects point at an item template `Items` does not hold, split by whether
the CLIENT ships that template — because only one half can ever be fixed by importing harder. *Shipped, keyless*
needs a capture referencing the item, since an item's TEA key is server-assigned and only ever arrives on the wire
(#203, closed won't-fix); *not shipped* can never be produced at all. Currently **227 of 227 distinct missing
templates are shipped-but-keyless**, across 1,616 objects — so the whole gap is capture-blocked and none of it is
an import defect. Reported, never asserted: both counts move as captures are added) ·
`inventory-token-check` (#719/#723: does every item a character is HOLDING reach the wire as something the client
will render? Flags a held template with no `Items` row — invisible in the snapshot with nothing to explain it — and
one whose minted wire token is malformed. ⚠️ A report for the #729/#849 reason: `ItemInstances` hangs off
`Characters`, so the seed excludes it and the two gates that used to assert this in `InventoryItemHashTests` ran
over an empty table. The TEMPLATE half stayed a test, where it covers a strictly larger population) ·
`broadcast-check` (#272: which server records does retail send to EVERY player who can see the subject, and which
only to the one client concerned? The oracle is `login_2-clients_group-trade-friend`, the ONLY capture with two game
connections — with one client a broadcast and a private reply look identical, with two a record reaching both streams
IS a broadcast. Prints each IIR as BROADCAST / per-client / MIXED. ⚠️ Do NOT automate off the type: a
`BehaviorControlCenterIIR` carries both an entity animation and an ack of the client's own request, so the record's
ROLE decides — the MIXED rows are the proof, and `N3ToClientQuit` at 2/44 is a despawn being per-viewer by interest,
which is correct. The standing measurement behind the broadcast rule in
[combat.md](../subsystems/combat.md)) ·
`disposition-rule` (#841: is the LAST-RESORT hostility rule better than the default it replaced, and does it leave
the townsfolk alone? `NpcDispositionTiers` decides an NPC's overhead disposition from three observations and one
inference; this scores the inference, and its ablations, against the 430 templates retail was captured answering a
`RequestAggroStatus` for — ground truth no tier of the chain contributed to. The friendly default gets 0 of 102
hostiles (76.3% overall); `Community > 0`, the rule the 2026-07-11 regression removed, calls **159 polled-FRIENDLY
templates hostile** and lands BELOW the default at 39.8%; the shipped `CarsGroup == 3 AND a real non-civilian
community` gets 84 of 102 at 95.3% with **zero** friendly-side errors. ⭐ The collision group ALONE scores higher
(97.4%) and is deliberately not what ships: the `fills` column shows the two answer the SAME 118 templates on the
population that actually reaches the tier, so the community half costs nothing today while halving the neutral-side
error — the slice they disagree on is where the rabbits, hens and deer sit. ⚠️ The assertion is not a percentage but
the regression signature: exit code 1 on any directly-polled FRIENDLY template the rule would make hostile) ·
`spawn-animation` (#607: what IS `npc_stats.spawn_animation`? Two tests. The first partitions every captured NPC
spawn into the login BURST and MID-SESSION — the class of predictor a field census cannot see — and ⛔ FALSIFIED the
ticket's hypothesis: 7.3% of burst spawns carry the flag against 5.8% mid-session, so it does not mean "this entity
is arriving". The second is the ⭐ **stickiness test**, which settled it: read per `(capture, handle, templateId)`,
of the 4,687 entities sighted more than once **351 are MIXED** (refuting a per-creature capability), **346 of those
are ON for the first sighting and off thereafter, and 0 ever turn ON** — so it is a **decaying state**, an entity
inside a window that has closed by the next sighting. Two more since #794: the window is bracketed **above** at
2.6 s and **below** at 25.6 s, which cannot both be one window (the lower bound is one co-streaming group in one
capture, so it is weak); and the MonsterData census — 174 of 3,185 records declare a `spawnanim`, and of the
templates the corpus flags **275 wear a creature that declares none** against 14 that do. ⛔ That census used to be
read as refuting "play this creature's entrance animation"; it does not, because the client falls back to
`defaultidleanim` for exactly those creatures (#800, `tools/ghidra/DumpSpawnAnimationConsumer.java`). Needs
`--Content:RdbDir` for that half),
`follow-pairs` (#741: every NPC-follows-NPC binding the corpus carries — the mode-07 formation MoveTasks — with
both ends resolved to creature NAMES and the offsets each follower held. Grouped by creature PAIR, because the
formation is only visible as a GROUP: the four `Black Dragon Guard Patroller`s behind one captain each hold a
different corner of a 3x3 grid with their own slot index, which is what proves the offset is a vector-plus-slot
and not the single scalar it was read as. It also prints what the merge WOULD store and the residue naming a
creature the DB does not hold, so the harvest's completeness is visible rather than assumed),
`spawn-duplicates` (is a cluster of near-identical STATIONARY spawns one NPC harvested twice, or a genuinely tight
retail placement? #718/#765. The verdict comes from `StationaryPlacementResolver` — the same code the
`npc-placements` import stamps `PlacementId` from — so the report cannot show a verdict the importer would not act
on. ⚠️ The test is a HANDLE sighted at two of the cluster's positions, NOT a distance and NOT "the same capture saw
both": a creature that moves is re-streamed within one session, so reading it per-capture inverts the answer.
`--report-arg <name substring>` narrows to one creature),
`ldb-search` (which LDB strings the client ships whose TEXT contains a substring, with the id that names them —
`--report-arg [<category>:]<substring>`, e.g. `50200:Ironwood`; `--report-arg id:<n>` goes the other way and prints
what EVERY category holds at one id, which is how an opaque wire id gets a name when its category is unknown — it is
what identified LDB 55701/55700 as the achievement catalog behind `DispatchAddTag`, #748. The direction every other LDB tool cannot go: when
the question is *"does the client ship an item called Ironwood, and at what id"* the name is all there is to go on.
⚠️ Not `rdb-search`, which despite the name only greps MESH manifest source paths. **ENGLISH ONLY**: a category
ships once per bundled LANGUAGE, so an unfiltered walk returns each hit seven times and headlines whichever
language has the most pages — `716200` reported as *Jatte de Set* with *Bowl of Set* underneath it. The filter is
the client's own per-page language record (`LdbLanguageIndex`), never a sniff of the text),
`bcc-census` (every BehaviorControlCenterIIR bucketed by command, with the tail lengths each carries — the census
[behaviorcontrolcenter-grammar.md](../re/behaviorcontrolcenter-grammar.md) quotes; `--report-arg <phase hex>` also
dumps every record of one phase in full),
`quest-xp` (#820: what retail PAYS for a quest. Every completion in the corpus with the experience readings either
side of it, with the kill-completed ones held apart — their delta is the kill's. Its real finding is a CONTROL: Exp
never appears in a stat block at all (0 of 20,213 records over 33 stat ids, from 10,016 parsed blocks), so its only
live carrier is the level-up ding and no delta isolates a quest. What it can state is a lower bound, from a
completion that dings: the overshoot past the threshold just crossed),
`quest-markers` (#848/#559: every `DispatchSetQuestMarker` retail sent, resolved to the NPC it was about — via the
most recent spawn of that handle BEFORE the packet, since handles are recycled — and cross-tabbed against the two
relations the shipped quest data holds for that NPC: what it GRANTS (the dialogue graph, all `QuestMarkerResolver`
reads) and what a quest goal SENDS you to. Each packet is scored against the player's journal AS OF that packet,
replayed from `QuestIIR` + `QuestFullUpdateIIR`, so "untouched / held / completed" is a fact about that moment.
It is what measured silver as an 85%-goal-target population rather than a grant one, and what counts the marker
states retail never sends),
`respawn-interval` (#855: how long after a kill the creature comes back, and what picks the number. A respawn is a
NEW entity id, so the signal is the same TEMPLATE back at the dead one's anchor on a handle the client has never
seen. Whether the interval is a TIMER or just the moment the player walked back is decided from the timestamps
alone — a cluster of agreeing intervals whose DEATHS are far apart, or which spans two captures, cannot be one
delivery event. Then it cross-tabs every attribute the spawn body states against the measured intervals, and says
which survive as selectors. Also prints why every unmatched kill matched nothing, which is what shows the corpus
is exhausted rather than the filters being too tight),
`death-timing` (every death BccIIR grouped by its ANIMATION, with each actor resolved to a creature name against
its own capture's spawns. It is what settled that `field12` is the CREATURE's animation length and not the
animation's: on 17036, 22 creatures, 22 carrying one value each and none carrying two — #781. Reports the
unresolved actors separately, since an unresolved population is the one thing that could fake either answer),
`weapon-set-drawn` (does a captured spawn's worn list say which of the two weapon SETS is drawn? No — 138 of the
10,990 armed spawns carry a primary and a secondary slot at once, so the list is the whole equipment container.
`--report-arg <name>` breaks it down per character, which is what shows DemoL80 carrying both and NecroL80 carrying
only a primary — #588),
`ambient-animation` (the two commands that make a standing NPC look alive — `BccAction` and `Emote`, 63,743 of the
corpus' 76,262 BCC records — measured per ACTOR and per creature TYPE rather than per capture, which is the only
way the question "what does ONE citizen receive, and how often" gets an answer. It is what separated the two:
`BccAction` is a p50-1-per-actor assert, `Emote` the recurring ambient animation whose next occurrence the previous
record's own `activation_time` times. `--report-arg <capture substring>` narrows to one capture; see the ambient
section of [behaviorcontrolcenter-grammar.md](../re/behaviorcontrolcenter-grammar.md)),
`player-actions` (every `PerformSpecialAction` 0x205A selector in the corpus, NAMED out of the `Abilities`
catalog — the selector is the ability's grant `Slot` — with the server's answering BCC records and IIRs.
`--report-arg <selector hex>` narrows to one and prints those answers in full, which is the spec a server-side
stance has to reproduce; see [player-actions.md](../subsystems/player-actions.md)),
`combo-swings` (every melee-combo swing in the corpus, resolved against the `Combos` catalog — the census
[combos.md](../subsystems/combos.md) quotes: which combo, which step, the `bcc_action_id`→direction map, the
player/NPC split, and — for #623 — each NPC combo swing attributed to its actor's own spawn, so the combo can be
grouped against the weapon that picked it. `--report-arg <file>` also writes the matched packets whole, for
`--report fixture-set`),
`create-classes` (which CLASS each create-flow capture selected, and how many spell/combo grants arrived under
it — the guard on every create-derived import, all of which attribute a whole capture to its first `ChangeClass`.
That is only correct while a capture selects ONE class: retail sends a clicked class's book so the editor can
display it, so a player browsing the list would put several books in one capture. 0 of the 21 today, and #581 is
where that mattered),
`method-census` (every RPC METHOD the corpus carries, named, with direction, endpoint classes and frame counts —
and, with `--report-arg <substring>`, the OTHER half: which matching methods have NO frame at all, which is the
evidence that decides whether a ticket is "decode it" or "capture it"),
`weapon-set` (can a capture say which WEAPON SET a character had drawn? The drawn set rides a stat-0x17
re-registration on the newly-active `Equipped` weapon slot at a swap — slots alternating 6 <-> 20 — but it is NOT
readable at login, because every equipped slot carries that same registration. Gitea #588 / #400),
`stat-series` (`--report-arg <id>[,<id>...]`: the ordered value sequence of one or more stats per capture and
handle, out of the captured `StatIIR` stream — a census says a stat is pushed, a SERIES says whether it counts
up, counts down, resets or is merely restated. Repeats collapse to `20 x3`. Gitea #656),
`morph-stats` (does retail ever push a SHAPESHIFT stat on the wire? Censuses every captured `StatIIR` against the
POLYMORPH set — `Scale` is attested 103 times on a Character, the other seven never appear — and prints the whole
77-id census beside it so an absence reads as a measurement rather than a broken scan. Gitea #709),
`spell-effect-gap` (Db-only, no corpus and no RDB: how much of the imported `SpellStatEffects` table reaches a live
engine, and for the rest, which of the two gates stopped it — the KIND one, or the STAT one. It asks
`BuffStatEffects.Classify` and `SpellPoolEffects.Applies` rather than re-deriving them, so it cannot report a policy
the server does not have. ⚠️ Its load-bearing column is the last one on the KIND table: how many of a kind's rows
land on a stat the engine ALREADY models, which is what says whether admitting the kind is worth anything and is
nearly unrelated to its row count. Gitea #701 is why it exists — that ticket was filed off a hand-census that had
gone four times stale and named the wrong gate. `--report-arg <kind or stat id>` lists the rows behind one line with
the spell that carries them; see [player-stats.md](../subsystems/player-stats.md)),
`creature-races` (RDB 1000508, the client's creature RACE table — the names behind `CharacterStats.race`, which
every NPC carries as a bare number; plus how far that vocabulary reaches into the unresolved open-world spawn
groups, which is the #518 question),
`npc-chatter` (the ambient-bark catalog's reach, #331 — the category-50015 page census, every stored `NpcChatter`
row resolved to its TEXT through the client's own token hash, and a per-group SWEEP of `sayinchat(N)` listing every
line the client ships for that group with the ones the DB lacks marked, split by whether they carry a `{Slot}`;
see [chat.md](../subsystems/chat.md)),
`fall-speed` (#515: pair every captured FALL with the impact SPEED the client reported for it — the landing
`CharDCMove`'s `Velocity.Y`. ⭐ It confirms the ticket's premise by measurement: over 39 falls the rank
correlation against damage is **0.886 / 1.000** per entity for the client's speed against **0.460 / 0.700** for
the drop we reconstruct from position samples. ⚠️ Two traps it documents rather than falls into — `Delta` is in
HUNDREDTHS of a health point, and a fall's airborne run cannot be bounded by the velocity SIGN because the client
sends exactly one downward-velocity record per fall, so the drop has to come from a position-max window. It
deliberately does NOT refit `FallDamage`, which needs the health pool per event),
`ldb-stat-names` (the client's stat NAME tables — LDB category 615, plus a three-way reconciliation against the
compiled descriptor registry recovered into [`aoc-client-stat-names.tsv`](../re/aoc-client-stat-names.tsv) and
against `CharacterStatId`, #625. ⭐ It prints the **306 ids where the client's own two sources disagree** and says
which wins: the registry, because the LDB display table still carries `Organization rank`, `AMS` and `Bandolier
Slots` — Anarchy Online terms — on ids the registry calls `ClanLevel`, `SpellCategory` and `ItemSource`. It also
splits our own 158 disagreements into the 38 that are a rewording and the **120 that name a different thing**),
`class-hierarchy` (the client's own archetype → class membership, RDB `1000506` — a `[parentClassId][childClassId]`
adjacency list whose loader names it *"classmembership info"*. Prints both entries (0 = the full authored hierarchy
including eleven cut classes, 1 = the shipped set) and **checks `ConanArchetypes` against entry 1**. That constant
used to cite the game's published documentation on the finding that the client shipped no membership table; it
does, all twelve placements agree, and this report is what keeps the hard-coded copy honest, #469),
`factions` (the Khitai faction & reputation catalog, #310 — RDB `1010701`/1 `data/FactionRelations.xml` joined to
`LDB[254]`. Prints the 16 faction ids with their rivals and icons, the six encounter tiers (`relationId =
factionBase + tier`, 120/180/720/1800/1440/2400 points) and the nine player rank ladders. The LDB blocks are
ordered runs rather than keyed by id, so the join is BY NAME and the resulting id order is printed rather than
assumed — it comes out strictly descending. ⚠️ The point THRESHOLDS between ranks are not in the client and the
report says so instead of interpolating them; see [factions.md](../subsystems/factions.md)),
`name-group-templates` (can the CLIENT name the creature behind an unresolved `Name`-category spawn group, #684?
`LDB[51000]` is the NPC-template name table, keyed BY TEMPLATE ID, so reading it backwards answers exactly what the
spawn-area resolver's tier 1 asks. Of the 31 unresolved creatures, **11 resolve to one template id** and 7 more are
named but not uniquely — several templates share a string, and the 4-char tag beside the name does not break the tie
(`Shark (H8AV)` and `Coast Shark (H8AV)` share one). ⚠️ Read-only by design: a template id plus a display name is an
IDENTITY, not an entity — level, health, model and faction are still capture-bound — so writing a stats-less
`NpcTemplate` would ship a creature that spawns WRONG rather than one that is absent),
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
string — `--Content:RdbDir` is OPTIONAL and only decrypts the strings it finds. `--report-arg runs` lists every distinct
string, `dump` writes a hex dump of every block. ⚠️ Counts are taken per PRINTABLE RUN and anchored on a closing
delimiter, because a regex run over the whole 228-byte buffer crosses the gaps between leaked strings and
manufactures roughly half its findings; see [retail-server-heap-leak.md](../re/retail-server-heap-leak.md)),
`rdb-quest-catalog` (the client's OWN quest catalog, RDB type 1000104 — 2181 quest lines / 5042 quests / 7327
goals with the designers' names in plaintext. No arg prints a summary, a populated-field census and three flat
TSV sections; `--report-arg <line or quest id>` prints that line as a tree. It also cross-checks `MainQuest.hash`
against the four `ACGHash` line tokens recovered from captures, which is what establishes that our `QuestLineId`
IS the client's `MainQuest.template_id`. ⚠️ It reads the **DB** as well as the RDB, for the step-order check:
the catalog's row order is scored against the captured `JournalSequence`, with ascending-id and reversed-order
controls — the measurement that rules the catalog OUT as a source of step order. It also carries the two reward
checks: `LineRewardCheck` (is `Quest_c` @+0xf0/@+0xf8 the LINE's reward? no — the populations are disjoint) and
⭐ `TwoPairExclusion` (is it the same pair as the journal's @+0x28/@+0x20 in a second slot? per LINE, 30 carry
one, 20 the other and **1 both** where chance predicts 6.5 — which refutes "two rewards at opposite ends of a
line", #802); see [quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)),
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
stated by whatever APPLIES it, and that applier is only findable by scanning the corpus for the id),
`spell-tooltip` (`--report-arg <spellId>[,...][@<casterLevel>]`: the spell's in-client TOOLTIP rendered out of our
own decode — the effect CHAIN followed, each element turned into the client's own LDB sentence, each magnitude
through the client's divisor and unit suffix, and an accounting line saying how much of the record that consumed.
`--report-arg census` scores the same renderer over all 21,502 records instead of printing one. RDB only, no
corpus. See [`../rdb/spell-tooltip.md`](../rdb/spell-tooltip.md), #775), … — the report's
target comes from `--report-arg`), read captures or the DB, and never write — independent of `--dry-run`.

## Decisions

1. **RDB modules are opt-in.** Not in the default set / `--all`; only run when named via `--only` (and they
   require `--rdb`). Matches the deliberate one-off RDB workflow.
2. **Seeds stay inline.** The hand-decoded quest/NPC constants live in their owning modules as code constants;
   externalising to JSON is a possible later refinement, out of scope.
