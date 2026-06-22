# Faolan.Importer v2 — Reimplementation Plan

Status: **IMPLEMENTED** (2026-06-23) · Supersedes the original `docs/archive/faolan-importer-plan.md`.

The importer is now module-based: `Program.Main` delegates to `Faolan.Importer.V2.ImportCli`. All capture/tsv
modules ship in the default run, rdb-* + mounts are opt-in, `--dry-run`/`--mode`/`--only`/`--skip`/`--list`/
`--report` are live, the archive importer is deleted, and a full v2 run is **byte-identical** to the old v1
import (verified via `.dump` diff). Step 2a remains partially open: `WriteMode` is honoured at the orchestration
level (npcs/world-bursts/conversations) but the leaf CaptureX importers still do their inherent upsert for
insert/upsert — threading `ctx.Mode` into each is the one tracked follow-up (the modules advertise + warn).

## Why v2

The importer grew organically subsystem-by-subsystem. Today everything lives in one 1100-line
`Program.cs` `Main` with a single linear run, a grab-bag of one-off `--flags` checked inline, hand-decoded
seed constants inlined between the import calls, and a `--dry-run` that bails out *before* the DB is even
opened (so it reports almost nothing). We now understand every subsystem it feeds, so v2 is a structural
rewrite — same extractors/codecs, new orchestration.

### Goals (what the user asked for)

1. **Separate option for every import.** Each import is independently selectable (`--only`, `--skip`, or a
   per-module flag). No more "all-or-the-handful-of-special-modes".
2. **A real `--dry-run` that just outputs.** Runs the full extraction + would-be writes, prints the exact
   per-table delta, writes **nothing**.
3. **Drop all Faolan-archive (old C++ `aocdatabase.sql`) imports.** No `--archive`, no `ArchiveSqlImporter`.
4. **Selectable write mode.** A `--mode` switch chooses how data lands: `insert` (only add new rows),
   `upsert` (default — add + update in place), or `overwrite` (replace the module's owned data wholesale).

### Non-goals

- No change to extractors, codecs, or wire grammar — those are proven by golden tests and stay as-is.
- No change to `faolan.db` schema. v2 is orchestration only.
- The beach (4010) committed-seed exemption and the Tortage-pruned world set are preserved exactly.

## What gets dropped

| Dropped | Reason |
|---|---|
| `--archive` flag + `ArchiveSqlImporter.cs` | Old C++ server SQL; user-confirmed no longer needed. |
| `p_abilities` ability defs from archive | Already covered: `CaptureAbilityImporter` builds `Ability` rows (incl. the identity quad `Data1..4`) from captured `0x2052` grants; the 27 ability rows are committed in `faolan.db` (source of truth). |
| The 6 archive-only ability-slot constants (`AbilitySlots` dict) | Move into `CaptureAbilityImporter` as a tiny inline seed (or a `data/ability-slots.json`), so the slot column survives a re-import without the archive. **This is the one piece of archive data to rehome — verify before deleting `ArchiveSqlImporter`.** |

## New architecture

### Import module abstraction

Every import becomes a self-describing module instead of an inline block:

```csharp
enum ImportSource { Captures, Rdb, Tsv, Db }   // what inputs the module needs

interface IImportModule
{
    string Name { get; }                       // CLI token, kebab-case, e.g. "npcs", "world-objects"
    string Description { get; }                 // one line for --list / dry-run header
    ImportSource Source { get; }                // gates the module if its input is absent
    IReadOnlyList<string> DependsOn { get; }    // module names that must run first
    Task<ImportResult> RunAsync(ImportContext ctx);
}

sealed record ImportResult(string Summary);     // the one-line "X seen, Y new, Z updated" each module prints today

sealed class ImportContext
{
    DatabaseContext Db;
    IReadOnlyList<NamedCapture> Captures;        // lazily read once, shared across modules
    string CapturesDir, RdbDir;
    WriteMode Mode;                              // Insert | Upsert | Overwrite (see "Write modes")
    bool DryRun;                                 // informational; the rollback (below) does the real work
    TextWriter Out;
}
```

A module reads `ctx.Mode` to decide how to write each extracted row (see next section). It may declare a
`SupportedModes` set and the runner skips/falls-back with a warning if an unsupported mode is requested.

A static `ModuleRegistry` lists all modules. The runner resolves the requested set (plus dependencies),
topologically sorts by `DependsOn`, and runs them in order, printing each `Summary`.

### Module registry (one row = one `--only`/`--skip` token)

Capture-sourced (default group, run in dependency order):

| Module | Replaces | Depends on |
|---|---|---|
| `listings` | `CaptureListingImporter` (was also `--listings-only`) | — |
| `items` | `CaptureItemImporter` | — |
| `maps` | `CaptureMapImporter` + name-sanitize backfill | — |
| `spells` | `CaptureSpellImporter.ImportAsync` | — |
| `abilities` | `CaptureAbilityImporter` (+ rehomed slot seed) | — |
| `starting-spells` | `CaptureSpellImporter.ImportClassStartingSpells` | — |
| `characters` | `CaptureCharacterImporter` (was `--skip-characters`) | `items` |
| `conversations` | `ConversationExtractor` + `ImportConversations` | — |
| `character-state` | `CaptureCharacterStateImporter` (wallets, hotbar) | `characters` |
| `learned-spells` | `CaptureSpellImporter.ImportLearnedSpells` | `characters`, `spells` |
| `shops` | `CaptureShopImporter` | — |
| `npcs` | `BuildBurstTruthSpawns` → spawns + types + patrol + npc-spells + `LinkSpellsToTypes` | — |
| `world-objects` | `WorldObjectExtractor` + `ImportWorldObjects` | — |
| `world-bursts` | `MapWorldBurstExtractor` + `ImportMapWorldBursts` | — |
| `quests` | `QuestDefinitionExtractor` + the seed steps (below) | — |
| `mounts` | `CaptureMountImporter` (was `--reseed-mounts`, isolated) | — |

RDB-sourced (require `--rdb`, **not** in the default group — opt-in via `--only`):

| Module | Replaces |
|---|---|
| `rdb-inventory` | `ImportRdb` type-count report (read-only) |
| `rdb-morphs` | `RdbMorphTargetImporter` |
| `rdb-spells` | `RdbSpellImporter` |
| `rdb-maps` | `RdbMapImporter` (was `--rdb-maps`) |
| `rdb-lua` | `RdbLuaImporter` (was `--rdb-lua`) |
| `rdb-respawns` | `RdbRespawnImporter` + CDN fetch (was `--rdb-respawns`, `--cdn`/`--store`) |
| `rdb-items` | `RdbItemImporter` decrypt (was `--rdb-items`, `--dump-templates`) |

TSV-sourced (run if the file exists, opt-in otherwise):

| Module | Replaces |
|---|---|
| `ellatha-levels` | `EllathaSpellLevelImporter.ImportAsync` |
| `ellatha-combos` | `EllathaSpellLevelImporter.ImportCombosAsync` |

### Seed data gets an explicit home

The hand-decoded constants currently inlined in `Program.cs` (`SeedBeachQuestGoals`, `SeedCasildaChain`,
`SeedCasildaQuestSnapshot`, `UniqueNpcHomes`) are snapshot-only data the extractors can't recover. In v2:

- Quest seeds become explicit sub-steps **inside** the `quests` module (run after `QuestDefinition` upsert),
  so `--only quests` still produces the full, correct quest state.
- `UniqueNpcHomes` moves into the `npcs` module.
- Keep them as code constants (they're tiny and heavily commented), but consider a `data/seeds/*.json`
  sidecar later if they grow. Out of scope for the first cut.

### Write modes (`--mode`)

Today every importer hardcodes its own write strategy, and three different ones are already in use. v2
makes that an explicit, selectable dimension — orthogonal to *which* modules run:

| Mode | Semantics | Generalises today's |
|---|---|---|
| `insert` | Add only rows whose key is **not** already present; never modify or delete an existing row. Safe to re-run on a populated DB. | `--skip-characters`, the "existing names are skipped, never overwritten" item/character behaviour. |
| `upsert` *(default)* | Update existing rows in place by key **and** insert new ones; never delete. | The default behaviour of items/maps/spells/shops/world-objects/quests. |
| `overwrite` | Replace the module's **owned** data wholesale for the keys it touches: delete-then-rebuild per key (or wipe the module's table set and rebuild), then insert. | The conversation clean-slate, `--reseed-npcs`, the world-burst stale-purge. |

Rules and edge cases:

- **Per-module support.** Not every module supports every mode. Conversations are *always* a clean rebuild
  (handle-keyed stale rows must be purged), so it advertises `overwrite` only. Characters sensibly support
  `insert` (the common case) and `upsert`, but `overwrite` is dangerous (cascades to 12 child tables incl.
  live player progress) — gate it behind an extra `--force` and a printed warning, or disallow it.
- **Scope of `overwrite` = the module's own tables only.** It must never touch another module's data. The
  NPC reseed already models this correctly (it wipes only `NpcSpawns`/morphs/equipment/patrol, leaving the
  Tortage prune and `NpcSpells` intact). Document each module's owned-table set so `overwrite` stays
  surgical and the committed seeds (beach burst 4010, pruned world) remain exempt.
- **Per-module override.** Global `--mode` sets the default; `--mode npcs=overwrite,items=insert` overrides
  individual modules in one run.
- **Interaction with `--dry-run`.** The chosen mode is fully exercised (deletes included) inside the
  transaction, then rolled back — so `--dry-run --mode overwrite --only npcs` shows the true "removed N,
  added M" delta without touching the DB.
- **Default stays `upsert`** so a no-flag run reproduces today's behaviour exactly (no surprises for the
  committed-DB workflow).

### Diagnostics split out

The read-only inspection modes are **not** imports and shouldn't share the import runner. Group them under
a `--report <name>` family (or leave as the existing flags, just routed through the new dispatcher):
`conversation-states`, `burst-coverage`, `anim <Name>`, `worldobj <name>`. They open the DB read-only and
never write, independent of `--dry-run`.

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

**Clean break — no back-compat shims.** The old one-off flags (`--skip-characters`, `--listings-only`,
`--reseed-npcs`, `--reseed-mounts`, `--rdb-items`, `--archive`, ...) are removed outright, not aliased.
v2 ships the new vocabulary only; update `README.md` + the CLAUDE.md command snippets to match.

## The `--dry-run` mechanism

Run the **real** module code inside a DB transaction and roll it back, so there's zero per-module dry
plumbing and the delta is exact:

```csharp
var before = await Snapshot(db);
await using var tx = await db.Database.BeginTransactionAsync();
foreach (var module in plan) Console.WriteLine(await module.RunAsync(ctx).Summary);
var after = await Snapshot(db);
PrintDelta(before, after);          // the existing before/after table
if (dryRun) await tx.RollbackAsync();
else        await tx.CommitAsync();
```

- SQLite honours the rollback across every module, including the `ExecuteSqlRaw` DELETEs used by the NPC
  reseed, so `--dry-run --only npcs` accurately shows "removed N, added M" then reverts.
- `Snapshot()` expands to cover **all** touched tables (today it misses Shops, Quests, Conversations'
  parents, Mounts, RDB-fed Spells/Morphs, etc.) so the delta is complete.
- Dry-run also prints, per module, a short "would write" line — already the module `Summary`, so free.

Caveat to verify during implementation: the CDN-fetching `rdb-respawns` module has a side effect outside
the DB (writing blobs to `--store`). Under `--dry-run` it must skip the disk write (guard on `ctx.DryRun`).
This is the one place a module genuinely needs to read the dry-run flag.

## Implementation steps

1. **Scaffold** `IImportModule`, `ImportContext`, `ImportResult`, `ModuleRegistry`, and the runner
   (selection parse → dependency resolve → topo sort → transactional run → delta). No behaviour yet.
2. **Wrap existing importers** as modules one at a time, each delegating to its current
   `CaptureXImporter.ImportAsync` / extractor. Pure mechanical move; the golden tests and a full
   `--dry-run` delta vs. a baseline `git stash` import prove parity per module. At this stage each module
   keeps its current write behaviour as its **default** mode (so parity holds).
2a. **Add the `WriteMode` dimension.** Thread `ctx.Mode` through each module: factor each importer's write
   into the three strategies (most already do `upsert`; characters → `insert`; conversations/npcs/world-
   bursts → `overwrite`). Declare each module's `SupportedModes` + owned-table set.
3. **Rehome the ability-slot seed** into `abilities`, then delete `ArchiveSqlImporter.cs` and every
   `--archive` reference (Program, README, CLAUDE.md command list).
4. **Fold the quest/npc seeds** into their owning modules.
5. **Wire the new `--dry-run`** (transaction rollback) and expand `Snapshot()` to all tables.
6. **Route the diagnostics** through `--report`.
7. **Add `--only`/`--skip`/`--list`** (new vocabulary only — no aliases for the old flags).
8. **Delete the old linear `Main` body** + every removed flag; update `README.md` and the CLAUDE.md
   importer command snippets to the v2 CLI.

## Verification

- **Parity gate:** on a clean `faolan.db`, a full v2 run must produce a byte-identical DB to a full v1 run
  (minus archive-only rows, which we've confirmed are already committed). Compare with
  `sqlite3 ... .dump | diff`, or row-count parity per table via the delta report.
- **Per-module dry-run:** `--dry-run --only <m>` for each module shows a non-empty, sensible delta and
  leaves the DB file mtime unchanged.
- **Golden tests** in `Faolan.Tests` stay green (they cover the builders the importer feeds, not the
  importer itself — unchanged).
- Re-run the standard `--skip characters` flow against a populated DB and confirm no churn in unrelated
  tables (the original reason `--listings-only` existed — now just `--only listings`).

## Decisions

1. **RDB modules are opt-in.** Not in the default set / `--all`; only run when named via `--only` (and they
   require `--rdb`). Matches today's deliberate-one-off RDB workflow.
2. **No back-compat shims.** Clean break — old flags removed, docs updated to the new CLI.
3. **Seeds stay inline for now.** The hand-decoded quest/NPC constants are externalised to JSON *later*,
   not in the first cut; they live in their owning modules as code constants.
```