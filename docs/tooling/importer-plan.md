# Faolan.Importer — Module-Based Import

Status: **CURRENT** · Supersedes the original linear importer plan and the
old C++ `aocdatabase.sql` archive importer (deleted).

`Faolan.Importer` rebuilds `faolan.db` from the capture sources (the DB is the single source of truth; the
server carries no embedded data blobs). It is **module-based**: every importable subsystem is a selectable
`IImportModule`. `Program.Main` delegates straight to `ImportCli.RunAsync`; `--list` shows every module and
`--dry-run` writes nothing (it runs the real module code in a rolled-back transaction and prints the exact
per-table delta).

One tracked follow-up remains (the `TODO(importer step 2a)` markers): `WriteMode` is honoured at the
orchestration level (npcs/world-bursts/conversations) but the leaf `CaptureX` importers still do their
inherent upsert for `insert`/`upsert` — threading `ctx.Mode` into each is the open item (modules advertise
their `SupportedModes` + warn).

## Project layout

`Program.cs` is the entry point only. The rest is organised by role (all one `namespace Faolan.Importer`;
the folders are organisational):

| Folder | Holds |
|---|---|
| `Cli/` | `ImportCli` (parse + load + migrate), `ImportRunner` (transactional run + delta), `ModuleRegistry`, `ImportModule`/`ImportContext` abstractions, `ReportCli`, `TableSnapshot`. |
| `Modules/` | The `IImportModule` definitions (one class per `--only`/`--skip` token). |
| `Captures/` | Capture reading (`TsharkCaptureReader`, `NamedCapture`), the shared `ImportOps` orchestration, and the per-subsystem `CaptureX` importers. |
| `Extractors/` | Packet-grammar extractors (conversations, NPC spawns/spells, patrol paths, quests, world objects, world bursts, archetypes). |
| `Rdb/` | Client-RDB readers/importers (opt-in, require `--rdb`). |
| `Tsv/` | Ellatha scrape backfills (spell levels/combos). |
| `Reports/` | Read-only diagnostics (burst coverage). |

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

## Module registry (one row = one `--only`/`--skip` token)

Capture-sourced (default group, run in dependency order):

| Module | Depends on |
|---|---|
| `listings` | — |
| `items` | — |
| `maps` | — |
| `spells` | — |
| `abilities` (+ the rehomed ability-slot seed) | — |
| `starting-spells` | — |
| `characters` | `items` |
| `conversations` | — |
| `character-state` (wallets, hotbar) | `characters` |
| `learned-spells` | `characters`, `spells` |
| `shops` | — |
| `npcs` (spawns + types + patrol + npc-spells + spell links) | — |
| `world-objects` | — |
| `world-bursts` | — |
| `quests` (definitions + the hand-decoded seed steps) | — |
| `mounts` (opt-in via `--only mounts`) | — |

RDB-sourced (require `--rdb`, **not** in the default group — opt-in via `--only`):
`rdb-inventory` (read-only type-count report), `rdb-morphs`, `rdb-spells`, `rdb-maps`, `rdb-lua`,
`rdb-respawns` (+ CDN fetch), `rdb-items` (template decrypt).

TSV-sourced (run if the file exists): `ellatha-levels`, `ellatha-combos`.

### Seed data has an explicit home

The hand-decoded constants the extractors can't recover live **inside their owning module** as commented code
constants: the quest seeds (`SeedBeachQuestGoals`, `SeedCasildaChain`, `SeedCasildaQuestSnapshot`) run as
sub-steps in the `quests` module; `UniqueNpcHomes` lives in `npcs`; the 6 ability-slot constants live in
`abilities` (rehomed from the deleted archive importer). Externalising them to `data/seeds/*.json` is a
possible later refinement, not done.

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
  support `insert`/`upsert`; `overwrite` is dangerous (cascades to 12 child tables incl. live player
  progress) — gated behind `--force`.
- **Scope of `overwrite` = the module's own tables only.** It must never touch another module's data. The NPC
  reseed models this (it wipes only `NpcSpawns`/morphs/equipment/patrol, leaving the Tortage prune and
  `NpcSpells` intact). The committed seeds (beach burst 4010, pruned world set) stay exempt.
- **Per-module override.** Global `--mode` sets the default; `--mode npcs=overwrite,items=insert` overrides
  individual modules in one run.
- **Default stays `upsert`** so a no-flag run reproduces the committed-DB workflow with no surprises.

## CLI specification

```
dotnet run --project Faolan.Importer -- [paths] [selection] [modifiers]

Paths
  --db <file>          default: <repo>/faolan.db
  --captures <dir>     default: <repo>/Captures
  --rdb <dir>          client RDB dir (enables rdb-* modules)

Selection (mutually exclusive; default = all capture + tsv modules)
  --all                every module whose input is present (capture + tsv; rdb only if --rdb given)
  --only a,b,c         run just these modules (+ their dependencies), comma-separated
  --skip a,b,c         run the default set minus these
  --list               print every module name + description, then exit

Modifiers
  --dry-run            run everything, print the per-table delta, write NOTHING
  --mode <m>           insert | upsert (default) | overwrite — global write strategy
                       per-module override: --mode npcs=overwrite,items=insert
  --force              permit overwrite on guarded modules (e.g. characters)
  --cdn <url>          rdb-respawns CDN base (module-specific)
  --store <dir>        rdb-respawns blob archive (module-specific)
  --dump-templates <d> rdb-items: dump decrypted templates (module-specific)

Reports (read-only, bypass the import runner)
  --report conversation-states | burst-coverage | anim <Name> | worldobj <name>
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
  reseed, so `--dry-run --only npcs` accurately shows "removed N, added M" then reverts.
- `TableSnapshot` counts **all** EF-mapped tables (so new tables are picked up automatically) — the delta is
  complete.
- The CDN-fetching `rdb-respawns` module has a side effect outside the DB (writing blobs to `--store`); under
  `--dry-run` it guards on `ctx.DryRun` and skips the disk write. This is the one place a module reads the
  dry-run flag.

## Diagnostics (`--report`)

Read-only inspection modes are **not** imports and don't share the import runner. They are grouped under
`--report <name>` (`conversation-states`, `burst-coverage`, `anim <Name>`, `worldobj <name>`), open the DB
read-only, and never write — independent of `--dry-run`.

## Decisions

1. **RDB modules are opt-in.** Not in the default set / `--all`; only run when named via `--only` (and they
   require `--rdb`). Matches the deliberate one-off RDB workflow.
2. **Seeds stay inline.** The hand-decoded quest/NPC constants live in their owning modules as code constants;
   externalising to JSON is a possible later refinement, out of scope.
