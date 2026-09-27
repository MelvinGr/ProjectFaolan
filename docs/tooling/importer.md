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
| `Cli/` | `ImportCli` (parse + load + migrate), `ImporterCommand` (the command line), `ImportRunner` (transactional run + delta), `ModuleRegistry`, `ImportModule`/`ImportContext` abstractions, `ReportCli` (one `ReportCli.<Name>.cs` partial per report), `TableSnapshot`. |
| `Modules/` | The `IImportModule` definitions (one class per `--only`/`--skip` token). |
| `Captures/` | Capture reading (`TsharkCaptureReader`, `NamedCapture`), the shared import operations by domain (`NpcImportOps`, `QuestImportOps`, `WorldObjectImportOps`, `ConversationImportOps`, `LootImportOps`), `QuestGoalWiring` (the pass that decides each quest goal's trigger/count/target), and the per-subsystem `CaptureX` importers. |
| `Extractors/` | Packet-grammar extractors (conversations, NPC spawns/spells, patrol paths, quests, world objects, per-map loot pools, item-hash harvesting). |
| `Rdb/` | Client-RDB readers/importers (opt-in, require `--Content:RdbDir`). |

## Module abstraction

Every import is a self-describing module:

```csharp
enum ImportSource { Captures, Rdb, Tsv, Db }   // what inputs the module needs

interface IImportModule
{
    string Name { get; }                       // CLI token, kebab-case, e.g. "npcs", "world-objects"
    string Description { get; }                 // one line for --list / dry-run header
    ImportSource Source { get; }                // gates the module if its input is absent
    bool InDefaultSet => Source is ImportSource.Captures or ImportSource.Tsv;   // false = opt-in
    IReadOnlyList<string> DependsOn { get; }    // module names that must run first
    IReadOnlyList<string> RequiredAfter => [];  // modules that must run after this one when it runs
    IReadOnlySet<WriteMode> SupportedModes { get; }
    Task<ImportResult> RunAsync(ImportContext ctx);
}

sealed record ImportResult(string Summary);     // the one-line "X seen, Y new, Z updated" each module prints
```

`ImportContext` carries the shared per-run state: the tracked `DatabaseContext`, the captures (read once),
the captures/RDB dirs, the raw packet bytes, the chosen `WriteMode`, and the `DryRun` flag.

`ModuleRegistry.All` lists every module in default-run order. The runner resolves the requested set (plus
`DependsOn` and `RequiredAfter`), topologically sorts by `DependsOn` (registration order is the stable
tie-break), and runs them in order, printing each `Summary`.

`buff-durations` is registered **last of all**, after the RDB group, and that placement is load-bearing:
`rdb-spells` writes `Spells.DurationMs` unconditionally, so a capture-sourced duration for the rows its property
run leaves empty has to be applied after it. `RdbSpellsModule.RequiredAfter` names it for the same reason, so
`--only rdb-spells` pulls it in rather than leaving those rows back at 0/1 ms.

## Module registry (one row = one `--only`/`--skip` token)

In registration (default-run) order. `--list` prints the same set with each module's description.

Capture- and DB-sourced, in the default set:

| Module | What it imports | Depends on |
|---|---|---|
| `listings` | auction-house listings (the tradepost search results) | — |
| `stock-phrases` | cutscene stock-phrase subtitles | — |
| `items` | item identities from inventory/ApplySpell packets | — |
| `chat-itemref` | item identities + TEA keys from `itemref://` chat links | `items` |
| `quest-reward-items` | item identities + TEA keys from the quest reward lists in captured journal records | `items` |
| `maps` | maps from MapChange packets | — |
| `spells` | spell ids from ApplySpell packets | — |
| `dot-effects` | DoT per-tick deltas + measured tick intervals | `spells` |
| `spell-effects` | server-acted spell effects: pet summons + the fatality proc (DB-sourced, needs no captures) | — |
| `abilities` | ability grants | — |
| `class-spells` | per-class spell access map (create grants) | — |
| `characters` | captured characters (incl. other players) and their bodies/gear | `items` |
| `conversations` | the NPC dialogue graph (whole-corpus rebuild, overwrite-only) | `npcs`, `quests` |
| `character-state` | wallets, hotbar | `characters` |
| `learned-spells` | per-character learned spell sets | `characters`, `spells` |
| `character-abilities` | per-character ability sets + combo books | `characters`, `abilities`, `character-state` |
| `character-quests` | per-character quest journals | `characters`, `quests`, `maps` |
| `character-bank` | per-character bank contents | `characters`, `items` |
| `character-sockets` | gems socketed into characters' items | `characters`, `items` |
| `shops` | vendor shop windows | `items`, `npcs` |
| `npcs` | NPC spawns/types/spells/patrol (overwrite is scoped to the maps the run saw) | `items`, `spell-effects` |
| `npc-placements` | pools the stationary spawn rows the corpus proves are one NPC seen twice (`PlacementId`) | — |
| `npc-disposition` | NPC hostility disposition (from `RequestAggroStatus`) | — |
| `npc-type-appearance` | lifts each creature's morph table + worn equipment from a placement onto its TYPE (DB-sourced) | — |
| `npc-script-events` | the `SendScryEvent` scripts an NPC's dialogue fires | — |
| `npc-combo-class` | the NPC combat archetype (which `combo_npc_<class>_*` family a template swings) | — |
| `npc-death-behaviors` | per-creature death animation | — |
| `npc-ambient-animations` | per-creature idle animations from the captured `Emote` records | — |
| `npc-follow-pairs` | which creature TYPE walks with which, from the captured mode-07 formation MoveTasks | — |
| `npc-chatter` | ambient NPC bark lines | — |
| `world-objects` | static world objects | — |
| `world-teleports` | zone-line door destinations, three tiers in precedence: captured use→teleport sequences, then the client's own teleport table RDB 1000087, then the rest by name against the playfield table ([teleport-changemap.md](../subsystems/teleport-changemap.md)) | — |
| `map-loot` | per-map loot pool (overwrite-only) | `items` |
| `quests` | quest definitions + areas + objective goals | `maps` |
| `quest-wiring` | the hand-decoded quest wiring (trigger fields, chains) onto the harvested definitions | `quests`, `maps`, `world-objects` |
| `quest-rewards` | each quest's own reward set (granted + choose-one) + the line rewards' quantities | `quests`, `items`, `quest-reward-items` |
| `travel-wiring` | local travel: the dialogue branch that sells each trip, its fare and arrival | `maps`, `conversations` |
| `consumable-uses` | consumable on-use spells → `Items.OnUseSpell` | `items`, `spells` |
| `starter-spells` | marks create-granted spells as each class's starter kit + the levels later grants land at | `class-spells` |
| `buff-durations` | the duration retail states, for spell rows whose RDB record leaves property field 8 empty (registered last, see above) | `spells` |

Opt-in (run only when named via `--only`): `class-spells-known` (class↔spell membership derived from the captured
spellbooks — DB-sourced; `characters` is deliberately not a dependency, which would drag its destructive rebuild
into an `--only` run), `mounts`, `pets`, `claims`, `recipes`. `mounts` harvests the mount CREATURE models and
selects them against the mount CATALOG, which `rdb-spells` imports — so run that first on an empty database, or
the module fails with that instruction rather than silently finding nothing ([mounts.md](../subsystems/mounts.md)).

RDB-sourced (require `--Content:RdbDir`, **not** in the default group — opt-in via `--only`):
`rdb-inventory` (read-only type-count report), `rdb-morphs`, `rdb-appearance`,
`rdb-spells` (the spell catalog + the 155-row mount catalog, [mounts.md](../subsystems/mounts.md)),
`rdb-ldb-names` (backfills empty spell names from the English LDB page), `rdb-achievements` (the achievement
catalog behind `DispatchAddTag` — LDB 55701 titles + 55700 descriptions, keyed by TAG id; see
[achievements.md](../subsystems/achievements.md)), `rdb-factions`, `rdb-ldb-abilities`, `rdb-abilities`,
`rdb-combos`, `rdb-feats`, `rdb-maps`, `rdb-name-filter` (the client's forbidden-word list for character names,
RDB 1065100 — stored HASHED, see [character-names.md](../subsystems/character-names.md)), `rdb-respawns`
(+ CDN fetch), `rdb-quest-catalog` (the client's quest catalog, RDB 1000104 — quest lines/quests/goals + their
plaintext names; see [quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)), `rdb-items` (template decrypt),
`rdb-npc-spawns` (the open-world ambient spawn areas, RDB 1010506), `rdb-quest-text`, `rdb-dialogue-teleports`,
`rdb-regions` (the bounded-area trigger volumes a subsystem consumes — discovery pads, quest goal regions, area
banners and the lava hazards; depends on `rdb-respawns`), `class-attributes` (the per-class/level attribute +
health BASE, see below), `rdb-combo-scaling`.

There are no web-scrape modules: the client carries both facts they would stand in for — a spell's class
MEMBERSHIP is its class-encoded `GuiStackingLine` (`rdb-spells`, all twelve classes) and a combo's REUSE TIME is
the combo XML's own `cooldown` (`rdb-combos`).

### Seed data has an explicit home

The hand-decoded constants the extractors can't recover live **inside their owning code** as commented
constants: the quest seeds (`SeedBeachQuestGoals`, `SeedCasildaChain`, `SeedCasildaQuestSnapshot`) run in
`QuestGoalWiring`; `UniqueNpcHomes` lives in `NpcImportOps` (`npcs`); the pet-summon roster and Frozen Hatred's
fatality proc live in `spell-effects`.

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

  `--report conversation-fold-stability` measures how much that matters: it folds the corpus, then folds it again
  with each capture held out in turn, and counts the node ids that MOVE when it is added back. Every
  dialogue-carrying capture is append-stable (zero ids move).

  The reason is a fact about the DATA, not about the fold. A node's id is
  `ComputeId(template, contentKey, discriminator)` and the discriminator is the class's **index** within its
  `(template, contentKey)` group, so an append can only move an existing id by renumbering a group that holds
  more than one class. The corpus has **zero** nodes above discriminator 0. An incremental importer could be
  built on that, but it would want this report in its acceptance path (or a discriminator derived from the class
  rather than from its position, which removes the failure mode outright).
- **Characters** advertise `insert` (the default: add what is missing, never touch an existing row) and
  `overwrite`, which is the **refresh**: a character whose row carries `Character.ImportedFromCapture` is deleted
  and rebuilt from the current corpus, so a re-dumped capture can update an imported bag / worn set / stat sheet
  that would otherwise stay frozen at whichever dump first built it.

  **The marker is the whole safety rule, and the two obvious alternatives both fail.** "The name appears in a
  capture" exempts nothing (the importer only ever iterates names that do), and the ACCOUNT cannot discriminate
  either, because imported characters deliberately sit on the normal play account. Only the importer sets the
  marker, so a character created in-game cannot carry one and is exempt by construction; a level threshold is not
  a guard. The refresh still discards a player's own rearrangement of an *imported* character's items, so it is
  guarded by `--force`.
- **Scope of `overwrite` = the module's own tables only.** It must never touch another module's data. The NPC
  reseed models this (it wipes only `NpcSpawns`/morphs/equipment/patrol, leaving `NpcSpells` and the other
  modules' tables intact). The committed seeds (e.g. the beach burst 4010) stay exempt.
- **Per-module override.** Global `--mode` sets the default; `--mode npcs=overwrite,items=insert` overrides
  individual modules in one run.
- **Default stays `upsert`** so a no-flag run reproduces the committed-DB workflow with no surprises.

## CLI specification

`--help` prints the authoritative list (`Faolan.Importer/Cli/ImporterCommand.cs`); this is the same set in prose.

```
dotnet run --project Faolan.Importer -- [paths] [selection] [modifiers]

Paths (config keys - faolan.json / faolan.user.json / environment / command line)
  --Database:ConnectionString <s>  override the connection; default: faolan.json Database:ConnectionString
  --captures <dir>                 the capture corpus (the private data repo's Captures/)
  --Content:RdbDir <dir>           client RDB dir (enables rdb-* modules)

Selection (mutually exclusive; default = every InDefaultSet module)
  (no flag)            the default set. RDB and the opt-in modules (class-spells-known/mounts/pets/claims/recipes)
                       are NEVER auto-included — name them with --only.
  --only a,b,c         run just these modules (+ their dependencies), comma-separated
  --skip a,b,c         run the default set minus these
  --list               print every module name + description, then exit

Modifiers
  --dry-run            run everything, print the per-table delta, write NOTHING
  --mode <m>           insert | upsert (default) | overwrite — global write strategy
                       per-module override: --mode npcs=overwrite,items=insert
  --force              permit overwrite on guarded modules — `characters` is the one that consumes it,
                       because its overwrite rebuilds every capture-imported character
  --rdb-proxy <url>    Faolan.Web.Content mirror ADDRESS for on-demand (le.idx file==0xFF) RDB resources —
                       just the host, e.g. http://127.0.0.1:7081; RdbReader owns the /gameupdate/upm/rdb route.
                       Defaults to the shared `Cdn` config (PublicAddress, else the local mirror on its
                       ListenPort - never ListenAddress, which is an interface), so a mirror on another box is one
                       line in the gitignored faolan.user.json rather than a flag on every command. Every rdb-*
                       module AND every --report fetches CDN-only records through it, and the proxy
                       store-and-forwards so pulls are archived. A reader built WITHOUT it degrades to the local
                       records only, which for several types is a minority of the game (577 of the 817 NPC spawn
                       layouts, 580 of 653 in-game map tiles, 276 of 520 tilemaps). Gate on `rdb.CanRead(entry)`,
                       never on `entry.IsLocal`.
  --dump-templates <d> rdb-items: dump decrypted templates (module-specific)

Reports (read-only, bypass the import runner)
  --report <name>      a read-only diagnostic; `--report list` prints every name, a typo answers did-you-mean
  --report-arg <v>     the target a report needs (NPC name / id / ...), e.g. --report anim --report-arg Casilda
  --out <dir|file>     output location for reports that write files
  --skeleton / --terrain-out / --dump-mesh   report-specific (rdb-rig / terrain-export / rdb-assets)
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

### `class-attributes` — the class/level attribute base

Fills `ClassAttributeBases` from RDB type 1000020, where the base lives as 80 PLAINTEXT templates per class — one per
level. The level comes from the template, not from its id: each names itself `"<Class> Class Feat <NNN>"` where `NNN`
is the first level of a ten-level block, and rank-by-attribute-sum inside the block gives the rest.

**`templateId = classBase + (level − 1)` is wrong** — the eight blocks sit inside a class's id range in an
arbitrary per-class order, so that rule would import, for example, the Priest of Mitra's level 61–70 values as
levels 11–20. The full decode is in the remarks on `ClassAttributeBaseModule` and in
[`docs/subsystems/player-stats.md`](../subsystems/player-stats.md).

All 12 classes resolve at all 80 levels, and the module verifies its result against every hardcoded **client
observation** (`ClientObservations`: a class, a level, and the template that character was seen loading) rather than
using them as an input. A missing `(class, level)` is treated as unknown, not zero, and world entry logs a warning
naming the character. `--report class-attribute-runs` prints the per-class block layout and the observation checks.

## Diagnostics (`--report`)

**The index is the table in `Faolan.Importer/Cli/ReportCli.cs`** (`ReportCli.Reports`, one `ReportSpec` per report:
name, description, whether it needs the capture corpus, body). `--report list` prints it, an unknown name answers
with the nearest ones, and a report that needs the corpus fails before running when `--captures` does not resolve.
`ReportCatalogTests` keeps the names unique. The descriptions below are the long form for the reports whose
findings other docs quote.

Reports are **not** imports and don't share the import runner: they read captures, the RDB or the DB, take their
target from `--report-arg`, and never write — independent of `--dry-run`.

**Report or test?** A check whose two sides are shipped DATA and the capture corpus belongs in a report, not a test:
the test suite reads a frozen corpus slice (`fixtures.bin`) while `faolan.db` is built from whatever dumps exist,
so the two populations diverge the moment captures are added. The same holds for anything over `Character*` rows:
`faolan.sql` excludes them as per-developer play state, so a test over them passes vacuously. Such a report PRINTS
the populations that move with the corpus and asserts (non-zero exit) only the direction that can only be a defect.

### Codec and corpus coverage

- **`codec-sweep`** — EVERY codec against EVERY captured record of the IIR type it declares, across all the dumps:
  `Parse`, re-`Build`, byte-compare. Two findings that differ: **REFUSED** = the codec models a shape retail does
  not send; **MISMATCH** = it parsed the body without accounting for all of it, so a field is being dropped. The
  whole-corpus form of the `--report timeline` footer; `CodecSurfaceConventionTests.BuildParseRoundTripsOverTheCorpus`
  asks the same question of the frozen slice. `--report-arg <IirTypeName>` narrows to one type and dumps every
  offending body in full.
- **`method-census`** — every RPC method the corpus carries, named, with direction, endpoint classes, frames and
  captures — and the other half: with `--report-arg <substring>`, the matching methods with **zero** frames, which
  decides whether a ticket is "decode it" or "capture it". Diffed against every `*Methods` member our code names,
  the remainder is **captured and unhandled**, sorted by frames — the priority order, because frame count is how
  hard retail leans on a message.
- **`method-bodies --report-arg <NameSubstring>`** — the bodies behind a named method, with endpoints and a
  per-offset stability census. A frame whose header will not parse is **counted and still shown** (endpoints as
  `0x0`), never dropped: a silent skip would read as "the corpus does not carry this message".
- **`iir-bodies --report-arg <IirTypeNameSubstring>`** — every captured body of one IIR type, grouped by a SHAPE
  signature (length plus the words where a discriminator sits), so a VARIANT stands out, and a shape with few
  members prints all of them. `method-bodies` cannot reach these: an IIR rides inside `PassBlob`, so its type is the
  sub-opcode at the front of the body.
- **`frame-health`** — how many captured frames our own strict `Rpc.Parse` refuses. It should read **100%**; a
  shortfall means a reader defect (a truncated method varint in the capture reader is the known shape).
  `RpcHeaderTests` cannot see that class of defect (its slice is `PassBlob`-only, method 0), so this is the
  whole-corpus counterpart.
- **`entity-classes`** — which object-class word each dynel-family IIR carries, read off the corpus: the `EntityClass`
  registry's evidence (it names `TombStone`, 51323).
- **`fixture-stats`** — what a representative selection of the corpus slice would keep and throw away: per IIR the
  bodies, the distinct body LENGTHS, the distinct bodies once the volatile 32-byte head is masked, and the bytes
  one-per-length would retain. One-per-length does not work as a rule: `MagicBuffIIR` has 4,666 bodies across **2**
  lengths, so it would keep two and every gate that reads all 2,929 captured applies would go vacuous — while those
  4,666 bodies are 291 KB of a 75 MB corpus. The size is elsewhere: `FullPlayerCharacterIIR` is **50 MB of the 75** at
  2.4 bodies per class, and `SimpleCharFullUpdateIIR` another 20 MB at 14.9. A selection is worth making per IIR.
- **`fixture-repack`** — rewrites `fixtures.bin` in the current container format, content byte-for-byte unchanged,
  so a container change never requires a re-harvest (the corpus half needs the capture repo and the named half
  cannot be re-derived at all). The container is BROTLI at quality 11 / window 24: the archive is 45k
  near-duplicate packet bodies laid out by (IIR, length) over 81 MB of payload, and a 16 MB window sees a whole
  class at once where deflate's 32 KB window saw only neighbours — 2.8 MB against 15.5 MB, and it decodes faster
  (0.04 s against 0.14 s). Reads no captures. It prints the archive's CONTENT digests before and after and refuses a
  repack that changed them; the digests cover every body with its index/frame/opcode and every named fixture, and
  deliberately not the id or the layout order, which a repack may change. It also converts a manifest that stores
  capture NAMES to capture ids (the first 8 hex digits of the SHA-256 of the dump's file name).
- **`fixture-source`** — maps a failure's `<id>#<index>` provenance back to the dump it came from: hashes every
  `*.tshark.json` name under `--captures` and prints the name(s) each requested id resolves to;
  `--report-arg <id>[,<id>…]`, or every archived id when none is given. Exit 1 when an id resolves to no dump (a
  dump renamed or removed since the harvest).
- **`create-classes`** — which CLASS each create-flow capture selected, and how many spell/combo grants arrived under
  it: the guard on every create-derived import, all of which attribute a whole capture to its first `ChangeClass`.
  That is only correct while a capture selects ONE class — retail sends a clicked class's book so the editor can
  display it, so a player browsing the list would put several books in one capture.

### RDB and LDB lookups

- **`rdb-dump`** (`<type>` or `<type>:<id>`), **`rdb-assets`**, **`spell-fields`**, **`item-stat-scaling`**,
  **`class-attribute-runs`**, **`conversation-states`**, **`anim`**, **`worldobj`** — record-level inspection.
- **`rdb-word-scan`** — every RDB record containing a given 32-bit word, by type: the scan our property readers
  cannot do, since they all bound a field id to <= 2000. LOCAL records only unless `:cdn` is passed, because a CDN
  entry is an HTTP fetch and a whole-RDB sweep would pull ~250k of them.
- **`ldb-search`** — which LDB strings the client ships whose TEXT contains a substring, with the id that names
  them — `--report-arg [<category>:]<substring>`, e.g. `50200:Ironwood`; `--report-arg id:<n>` goes the other way and
  prints what EVERY category holds at one id, which is how an opaque wire id gets a name when its category is
  unknown (it identified LDB 55701/55700 as the achievement catalog behind `DispatchAddTag`). Not `rdb-search`, which
  only greps MESH manifest source paths. **English only**: a category ships once per bundled language, so an
  unfiltered walk returns each hit seven times and headlines whichever language has the most pages; the filter is
  the client's own per-page language record (`LdbLanguageIndex`), never a sniff of the text.
- **`ldb-stat-names`** — the client's stat NAME tables (LDB category 615) reconciled three ways against the compiled
  descriptor registry recovered into [`aoc-client-stat-names.tsv`](../re/aoc-client-stat-names.tsv) and against
  `CharacterStatId`. It prints the **306 ids where the client's own two sources disagree** and says which wins: the
  registry, because the LDB display table still carries `Organization rank`, `AMS` and `Bandolier Slots` — Anarchy
  Online terms — on ids the registry calls `ClanLevel`, `SpellCategory` and `ItemSource`. It also splits our own 158
  disagreements into the 38 that are a rewording and the **120 that name a different thing**.
- **`creature-races`** — RDB 1000508, the client's creature RACE table (the names behind `CharacterStats.race`), plus
  how far that vocabulary reaches into the unresolved open-world spawn groups.
- **`class-hierarchy`** — the client's own archetype → class membership, RDB `1000506` — a
  `[parentClassId][childClassId]` adjacency list whose loader names it *"classmembership info"*. Prints both entries
  (0 = the full authored hierarchy including eleven cut classes, 1 = the shipped set) and **checks `ConanArchetypes`
  against entry 1**; all twelve placements agree, and the report keeps the hard-coded copy honest.
- **`factions`** — the Khitai faction & reputation catalog: RDB `1010701`/1 `data/FactionRelations.xml` joined to
  `LDB[254]`. Prints the 16 faction ids with their rivals and icons, the six encounter tiers (`relationId =
  factionBase + tier`, 120/180/720/1800/1440/2400 points) and the nine player rank ladders. The LDB blocks are
  ordered runs rather than keyed by id, so the join is BY NAME and the resulting id order is printed rather than
  assumed — it comes out strictly descending. The point THRESHOLDS between ranks are not in the client and the
  report says so instead of interpolating them; see [factions.md](../subsystems/factions.md).
- **`name-group-templates`** — can the CLIENT name the creature behind an unresolved `Name`-category spawn group?
  `LDB[51000]` is the NPC-template name table, keyed BY TEMPLATE ID, so reading it backwards answers what the
  spawn-area resolver's tier 1 asks. Of the 31 unresolved creatures, **11 resolve to one template id** and 7 more
  are named but not uniquely — several templates share a string, and the 4-char tag beside the name does not break
  the tie (`Shark (H8AV)` and `Coast Shark (H8AV)` share one). Read-only by design: a template id plus a display name
  is an IDENTITY, not an entity — level, health, model and faction are still capture-bound — so a stats-less
  `NpcTemplate` would ship a creature that spawns WRONG rather than one that is absent.
- **`bounded-areas`** — the client's trigger-volume survey: no arg prints the census, `--report-arg <bucket>` lists
  one bucket and cross-checks it against what we already hold; see
  [bounded-areas-1000053.md](../rdb/bounded-areas-1000053.md).
- **`particle-effects`** — RDB 1010207 named, so an effect id can be found by what it IS. The RDB name table indexes
  the type, so 7,779 of the 8,163 have a name; the 189 records that ship the artist's authored XML also carry a
  folder path and a duration. `--report-arg <substring>` searches name, authored path and texture;
  `--report-arg xml:<id>` prints one authored source whole.
- **`rdb-quest-catalog`** — the client's own quest catalog, RDB 1000104: 2181 quest lines / 5042 quests / 7327 goals
  with the designers' names in plaintext. No arg prints a summary, a populated-field census and three flat TSV
  sections; `--report-arg <line or quest id>` prints that line as a tree. It cross-checks `MainQuest.hash` against the
  four `ACGHash` line tokens recovered from captures, which establishes that our `QuestLineId` IS the client's
  `MainQuest.template_id`. It reads the **DB** as well, for the step-order check: the catalog's row order scored
  against the captured `JournalSequence`, with ascending-id and reversed-order controls — which rules the catalog OUT
  as a source of step order. Two reward checks: `LineRewardCheck` (is `Quest_c` @+0xf0/@+0xf8 the LINE's reward? no —
  the populations are disjoint) and `TwoPairExclusion` (is it the same pair as the journal's @+0x28/@+0x20 in a
  second slot? per LINE, 30 carry one, 20 the other and **1 both** where chance predicts 6.5 — which refutes "two
  rewards at opposite ends of a line"); see [quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md).
- **`scaffold-heap`** — the RETAIL SERVER's own memory, harvested out of the NPC spawn scaffold: retail writes 3 of
  the 53 scaffold words on an NPC spawn and sends the other 50 carrying whatever was there, so 17,400 captured spawns
  ship 3.3 MB of server-side data. Yields the script engine's ScriptGroup catalogue and C++ source-file inventory,
  retail's `Universe[1]/Territory[1]/Dimension[N]/GameServer[M]` config tree and its Oracle SQL, plus the odd LDB
  string (`--Content:RdbDir` is optional and only decrypts the strings it finds). `--report-arg runs` lists every
  distinct string, `dump` writes a hex dump of every block. Counts are taken per PRINTABLE RUN anchored on a closing
  delimiter, because a regex run over the whole 228-byte buffer crosses the gaps between leaked strings and
  manufactures roughly half its findings; see [retail-server-heap-leak.md](../re/retail-server-heap-leak.md).
- **`teleport-match`** — how well the client's teleport table (RDB 1000087) lines up with our zone-line
  `WorldObject` rows: the measurement behind `world-teleports`' tier 2 — the nearest-door distance distribution,
  how many doors would gain a destination, and a cross-check against the destinations we already hold. Re-run it
  after importing more maps; every figure in the teleport docs comes from here.

### Spells, buffs and feats

- **`spell-flags`** — sweeps the spell record's FLAGS word bit by bit against what the DB knows, so "which bit says X"
  is measured. Bit 2 is printed as the CONTROL — a known predicate that scores 100.00%, the yardstick every other row
  is read against.
- **`spell-element-lists`** — scores the element-LIST grammar over the whole spell catalog: a spell record's elements
  come in count-framed groups, `[2][listId][(n+1) * 0x3F1]`. It cannot be a test because the suite runs from a clone
  with no RDB. `--report-arg kinds` adds the per-id kind breakdown.
- **`spell-refs --report-arg <spellId>[,...]`** — every spell record whose GameAction element region NAMES the given
  spell, printed by key (the inverse of `spell-template`). A buff whose own record is nearly empty has its mechanics
  stated by whatever APPLIES it, and that applier is only findable by scanning for the id.
- **`spell-tooltip --report-arg <spellId>[,...][@<casterLevel>]`** — the spell's in-client TOOLTIP rendered out of our
  own decode: the effect CHAIN followed, each element turned into the client's own LDB sentence, each magnitude
  through the client's divisor and unit suffix, and an accounting line saying how much of the record that consumed.
  `--report-arg census` scores the renderer over all 21,502 records. RDB only; see
  [`../rdb/spell-tooltip.md`](../rdb/spell-tooltip.md).
- **`spell-level-ladder`** (`--report-arg <family substring>` for per-rank detail) — which spell FAMILIES ship a
  per-rank level ladder and which ship one level for several ranks, so that a single ding would teach several ranks
  of one spell at once. `Spells.Level` comes from RDB field 125, which states the ladder for 103 of the 122 learnable
  rank families and states none for a few (every rank of `Chill` reads 7; `Lifestrike`'s five read 2/3/4/4/4).
  `RdbSpellImporter.RankLadderFromEffectBands` repairs the families where the record answers a second time (the
  `InterpolatedHit` ramp's level band) completely and monotonically; this counts the residue. The one assertion: a
  family the rule WOULD separate whose shipped rows are still collapsed (i.e. `faolan.db` predates the rule). It
  diagnoses each residual collapse against the TEACHING FEATS, which split the 19 into two shapes — an *untaught top
  rank* (no feat grants it while every rank below has one, and it repeats the level below; all nine sit in one late
  id block ≥ 4487431, and the captured level-80s know the rank below but never it) and a *rank 1 from a class feat*
  (rank 1's only grant is a `<Class> Class Feat`, which states no band, so field 125 copied band-granted rank 2's level
  onto it). It prints the whole-sweep diff of the featclass schedule against field 125, per class: 432 rows, 363
  agree, 69 differ — and names the 14 that differ *upward*, which refute the rule that field 125 bounds the grant.
  The five melee classes appear nowhere in it: no feat teaches a combo. See the field-125 section of
  [spell-record-fields.md](../rdb/spell-record-fields.md).
- **`spell-grant-check`** — does the level path offer a captured character the spellbook retail gave them? Runs the
  SHIPPING queries (`GetStartingSpells`, `GetStartingBarSpells`) for every character in the DB against its own captured
  known-spell list, and splits the errors: EXTRA (offered, not known) is printed; MISSING is split into *never on
  offer* (absent from `ClassSpells`, or `Level 0` — a catalog gap), *held below their stated level* (retail granted it
  earlier than the client's record says), *no teaching feat* (a feat-harvest gap) and *bought in the feat TREE* (a
  different mechanism) — leaving DROPPED, the only direction that can only be a defect. The standing measurement
  behind the per-class feat gate in [combos.md](../subsystems/combos.md). It also asserts three captured-record
  exclusions: no captured spellbook holds a combo's GameAction or a book spell's effect, and no captured feat list
  holds a progression track — a combo turning up in a retail spellbook would refute the exclusion rule itself. An
  empty oracle returns non-zero rather than reporting success.
- **`rank-tables`** — which member of a rank TABLE a chain applies. A rank table is a spell in no stacking line
  fanning out one `CastNano` edge per rank of one line, and exactly one member may land. Every edge carries a
  CRITERION run gating it on `MustHavePassiveAbilityRunning <the rank below it>`. Over the 65 tables: **43 upgrade
  ladders** (the gate names another member), **4 trained-rank lookups** (the gate names a spell outside the table —
  the passive rank the character holds), 4 mixed and 14 with no gate at all, where arrival order is all there is. 42
  tables' FIRST edge gates on ITSELF — the refresh-at-the-cap rung, and the asymmetry that identifies the shape.
  `--report-arg <spell id>` prints one table edge by edge with each gate resolved to a name.
- **`feat-autotrain-check`** — does the client's auto-train flag agree with what retail puts in a feat list, and does
  one point per rank reproduce the budget those lists spend? Both sides are the client's: the featclass table
  (`FeatClassEntry`, RDB 1000043) against the feat lists harvested off the captured player spawns. Each captured
  character spends its level budget exactly (79/79/16) and none holds a feat the client auto-trains; non-zero exit on
  a character that does. The EFFECTS half: of 8,255 auto-trained effect rows, 5,828 are the class PROGRESSION track
  that `ClassAttributeBases` already applies and 2,427 are the derived source, and it asserts the derived half
  touches **no pool and no attribute stat**, so it cannot move a captured stat sheet. It prints the LADDER PICK beside
  the sum it refuses (Assassin 29,112 picked against 75,695 summed — the ladders state totals, not increments) and the
  mana-regen corroboration table, where class base + the auto-trained `Mage` / `Priest` feat reproduces all four of
  retail's observed stat-280 values.
- **`feat-keys`** — hunts a record's per-template TEA key by trial-decrypting it against captured packet bodies.
  Modes: the feat sample (default, or `<n>`), `ids:a,b,c` for named records, `unkeyed[:n]` for a coverage sample of
  the encrypted 1000020 records we hold no key for (spread evenly across the catalog, because id ranges cluster by
  content pack), and `exhaustive` / `exhaustive:apply`. It drives `FeatKeyScanner`.
  - **All 19,777 feat records are plaintext**, so feats need no key.
  - **A key quad has two carriers.** A `ProtoAcgItem` writes the four words as four `fixed32` fields, so a tag byte
    sits between each one — `0d <k1 LE> 15 <k2 LE> 1d <k3 LE> 25 <k4 LE>`, 20 bytes for 16 bytes of key — and no
    16-byte window ever contains it, in any byte order. The raw fixed-width inventory/snapshot form writes
    `<k1><k2><k3><k4>` big-endian. A scanner that knows only the window form misses most keys and makes a scanner
    limitation look like a corpus gap; with both layouts a 20-item recall check scores **20/20** (18 `proto`, 2 `be`).
  - **A null from `unkeyed` is weak**: a key only rides the wire when the server SENDS you the thing, so a sampled
    record nobody in the corpus was sent can never be opened. Recall is not the limit; the sample is.
  - **`exhaustive` inverts the scan**: the two carriers can be located STRUCTURALLY — the protobuf one by its
    `0d/15/1d/25` tag run, the raw one by the quad being written twice back to back — so the corpus yields ~11.5k
    candidate quads instead of 127M windows, each tried against **every** encrypted record. The tag run is generic
    (any four-float message matches, so quaternions are in the candidate set), which costs one trial decrypt each.
    Over 138 dumps: **4,918 records opened — 4,873 of the 5,072 we already hold (a 96% implicit control) and 45
    NEW**, all 45 surviving a full decrypt + inflate + `[type][id]` check. The 4% of held keys it does not recover
    are the carriers with no byte anchor — the `itemref://` text links and the quest-reward lists inside `Quest_c`.
  - **It names the carrier**, which says which harvester is missing:

    | carrier | new keys | what it is |
    |---|---:|---|
    | `SimpleCharFullUpdateIIR` (`0xFA015807`) | 20 | another entity's SPAWN — their equipped gear |
    | `DispatchTradepostSearchResults` (`GameCharAgent`→`TradepostInterface`, method 5) | 20 | auction-house search results — items strangers are selling |
    | `BehaviorControlCenterIIR` (`0x642CD3D6`) | 4 | the action ack's item-identity slot |
    | `LootBagFullUpdateIIR` (`0xF096251E`) | 1 | a loot bag |

    The tradepost one is why the report names a message by opcode **and receiver class**: `0x2005` alone is the lossy
    wire rendering, shared with the 12-byte wallet update (`DispatchUpdateCash`). Three of the four carriers are one
    shape — SOCKETS: a gem lives in `slot_map` / `gem_slots` inside the item it is set into and never appears as a
    top-level entry again, so the spawn, bank, loot-bag, mail, add-item and tradepost harvest sources all walk sockets
    through one shared `RecordSockets`. 41 of the 45 records come in structurally that way.
  - **`exhaustive:apply` writes the validated keys into `Items`** — the exception, not the shortcut. Most carriers
    name the item (an inventory entry, a shop ware, a spawn's worn gear, an auction row and a socketed gem all carry
    `(template, key)` together) and belong in `ItemHashHarvester`, where they keep paying on every future capture. The
    equip/unequip `BehaviorControlCenter` ack does not: its tail carries the moved item's key quad twice with **no
    template id anywhere** (`BehaviorAcks.EquipMoveAck` writes exactly that), so trial decryption is the only way to
    identify the record. Keys written this way have survived the full decrypt + inflate + `[type][id]` check and go
    through `CaptureItemImporter.UpsertAsync`, so the never-downgrade rules apply as usual.
  - **Every run plants two known-keyed records as a CONTROL and refuses to report a negative unless they come
    back.** The report's only possible answer is a negative, and a tool whose only answer is "nothing" can be
    completely broken while looking like a true negative (candidate keys rebuilt little-endian in a big-endian
    protocol, or an oracle expecting the template's type word where `RdbReader.ReadDecrypted` decrypts and *then*
    unwraps, so the plaintext under the cipher is an `IOz1` container). `FeatKeyScannerTests` gates the control
    synthetically so a plain clone keeps it.
- **`feat-procs`** — `FeatProcs`' shape, and the controlled test that `Amount` is a percent chance.
- **`buff-durations`** — every captured `MagicBuffIIR` apply grouped by spell, with the durations retail stated and
  the `Spells.DurationMs` we build from. It separates the two populations a total hides: a SHORTFALL that decreases
  across a capture is retail re-sending a long buff with its remaining time, while an EXCESS cannot be a remainder
  and is therefore a wrong row (which `buff-duration-check` asserts on). `--report-arg <name substring or id>`
  drills one spell and prints each apply with its capture and offset.
- **`dot-trains`** — the evidence behind every measured DoT tick interval: each `(spell, target, caster)` tick train,
  its length, and the gaps it contributes — the exact input `DotIntervalEstimator` is handed. Train LENGTH is what
  the pooled gap list hides: a length-2 train contributes one gap that is a re-application phase offset as often as
  it is the period. `--report-arg <name substring or id>` drills one spell; `--out <file>` writes the same input at
  full precision as one `(spell, gap)` row per line, so a candidate acceptance rule can be re-run over every spell as
  a diff.
- **`magicbuff-descriptor`** — where the MagicBuff apply payload's `descriptor[0]` and `bool2` come from. Both are
  near-per-spell, so it cross-tabs them against the spell's RDB record: no FLAGS bit scores above +7 against a +50
  yardstick, and no PROPERTY FIELD carries either — the one 80% candidate, field 54, matches **0 of 46** informative
  records, so its agreement is entirely base rate (`descriptor[0]` is 1 on 83% of spells). It also tests the level
  pair by within-spell variance, which is confounded by the corpus being a handful of characters and says so.
- **`buff-descriptor`** — the cross-tab behind the `MagicBuffIIR` hostile-flag gap in
  [gaps.md](../protocol/gaps.md).
- **`spell-effect-gap`** — DB-only: how much of the imported `SpellStatEffects` table reaches a live engine, and for
  the rest, which gate stopped it — the KIND one or the STAT one. It asks `BuffStatEffects.Classify` and
  `SpellPoolEffects.Applies` rather than re-deriving them, so it cannot report a policy the server does not have. Its
  load-bearing column is the last one on the KIND table: how many of a kind's rows land on a stat the engine ALREADY
  models, which says whether admitting the kind is worth anything and is nearly unrelated to its row count.
  `--report-arg <kind or stat id>` lists the rows behind one line; see [player-stats.md](../subsystems/player-stats.md).
- **`morph-stats`** — does retail ever push a SHAPESHIFT stat on the wire? Censuses every captured `StatIIR` against the
  POLYMORPH set — `Scale` is attested 103 times on a Character, the other seven never appear — and prints the whole
  77-id census beside it so an absence reads as a measurement rather than a broken scan.
- **`shield-rose`** — every `CombatShieldIIR` timed against its own defender's spawn: of 71 first-roses, none arrives
  within 2 s of that handle's spawn, so retail sends no catch-up rose on spawn.
- **`pet-cost`** — where a summoned pet's point cost comes from. No field of the character message EQUALS the cost
  (`generic_stats` is empty on every pet; of 64 numeric fields, 60 present on all 155 samples with 29 varying, none
  equals it). Exactly one field DETERMINES it — `npc_stats.TemplateId` — only by identifying the creature. The
  `PetIIR` owner-assign's `(f1,f2,f3)` KIND triple is where **`f2` IS the cost**, equal on 61 of 62 resolved assigns:
  `(0,1,1)` on cost-1 pets, `(0,2,2)` on cost-2, `(0,4,2)` on cost-4. It prints the field counts as a control, since
  "no field matches" and "no fields collected" would otherwise print the same.

### Items and quests

- **`acg-pair`** — every ProtoAcgItem in the dumps, tabulated by how template2 relates to template1, with BOTH halves of
  each differing pair decrypted and diffed field by field: they differ only in LEVEL, so the pair is a low/high
  endpoint range.
- **`gem-slots`** — the gem model out of the item template (`NumGemSlots` (114), `GemSize`, the four
  `GemSlot<N>Color` fields and the gem's own `GemColor`/`GemTier`) cross-checked against every socket the dumps show.
  Three items carry more gems than they declare, which is retail's own LEGACY overflow state, not a decode failure —
  the client has a `GemOverflow` token and link for exactly it.
- **`spawn-item-level`** — what LEVEL a character spawn puts on each worn item, measured against `Items.Level`:
  125,721 of 125,722 entries agree, so the spawn carries the item's own level exactly as the snapshot entry does.
- **`item-binding`** — the client's own bind-on-equip/pickup rule evaluated over every decryptable item template, the
  `Flags` bit census, and the wire's arrival `Flags` from the dumps against the template's own — the measurement
  behind the [item-template-stats](../rdb/item-template-stats.md) binding section.
- **`acg-token-check`** — `Items.Hash` is the HARVESTED token (0 when no capture showed that template entering an
  inventory) and `AcgHash.Mint` supplies the rest, so a minted token must not land on a harvested one or on another
  minted one — the one-token-per-template property retail keeps. The assertion is a BIRTHDAY PROBLEM in the item
  population, not a property of the code: with 4,615 harvested and 529 minted tokens in a 36^4 space the expectation
  is 1.54 collisions per salt, so a given salt is clean about 22% of the time (measured, 90 of 400 consecutive salts),
  and every capture harvested makes a collision likelier. It prints the count, the expectation, and the next clean
  `AcgHash.Salt`; exit 1 only when a collision exists.
- **`inventory-token-check`** — does every item a character is HOLDING reach the wire as something the client will
  render? Flags a held template with no `Items` row (invisible in the snapshot) and one whose minted wire token is
  malformed. The TEMPLATE half is a test (`InventoryItemHashTests`), where it covers a strictly larger population.
- **`world-object-items`** — which world objects point at an item template `Items` does not hold, split by whether
  the CLIENT ships that template, because only one half can ever be fixed by importing harder. *Shipped, keyless*
  needs a capture referencing the item, since an item's TEA key is server-assigned and only arrives on the wire; *not
  shipped* can never be produced. Reported, never asserted: both counts move as captures are added.
- **`quest-rewards`** — the four reward lists inside every captured `Quest_c` journal record. Each 16-word element is
  one reward ITEM: `[instance][questId]` then the ACG `template1`/`template2` pair, each as
  `[1000620][templateId][key x4]`, then `[level][quantity]`. The proof is DECRYPTION with a CONTROL: all 101 distinct
  templates open with the key the wire states beside them and all 101 fail with a bogus key. It reports the CONTENT
  gap too: templates not in `Items`, each arriving with the key that would admit it. Needs `--Content:RdbDir` for the
  decryption half. (The `quest-rewards` and `quest-reward-items` MODULES harvest from the same records: the quest's
  own granted/choose-one set into `QuestRewardOptions`, the line rewards' `Quantity`/`WirePos` into
  `QuestLineRewards`, and item identities + keys — a reward list describes what a quest WILL pay, so it reaches items
  nobody in the corpus received. `rdb-quest-catalog`'s clean rebuild carries the harvested rows across. The item
  module reads them through the codec (`QuestcRecord.Obj1..Obj4` → `QuestRewardItem`), because `ItemHashHarvester`
  scans for the shapes an INVENTORY entry takes and does not see these.)
- **`quest-reward-lists`** — what the four reward lists MEAN, checked against what retail handed out: `obj1`/`obj2` are
  the quest's OWN granted/choose-one set, `obj3`/`obj4` the LINE's catalog reward previewed on every non-last step —
  1,553 of 1,553 equal the catalog, 0 differ. Prints the residue by quest.
- **`quest-xp`** — what retail PAYS for a quest: every completion in the corpus with the experience readings either
  side of it, kill-completed ones held apart. Its real finding is a CONTROL: Exp never appears in a stat block at all
  (0 of 20,213 records over 33 stat ids, from 10,016 parsed blocks), so its only live carrier is the level-up ding and
  no delta isolates a quest. It states a lower bound from a completion that dings.
- **`quest-container`** — what takes an item out of the quest container `0x71`: the goal that SPENDS it, or the quest
  COMPLETING? **7 of the 17 completions that happened while the container held something KEPT their items**, so "a
  completed quest clears its container" is refuted. The decisive pair is one chain — 13563 *"Get the Scavenger's
  Key"*, an ACQUIRE goal, completes holding the key and keeps it, while 13480 *"Free Casilda"*, the step that USES it,
  completes and loses it. It prints an ARRIVAL control beside every parse count: an RPC body read at the `.Record`
  offset instead of `.Body` parses zero and would otherwise print a confident zero.
- **`quest-markers`** — every `DispatchSetQuestMarker` retail sent, resolved to the NPC it was about (via the most
  recent spawn of that handle BEFORE the packet, since handles are recycled) and cross-tabbed against the two
  relations the shipped quest data holds for that NPC: what it GRANTS (the dialogue graph `QuestMarkerResolver`
  reads) and what a quest goal SENDS you to. Each packet is scored against the player's journal AS OF that packet,
  replayed from `QuestIIR` + `QuestFullUpdateIIR`. It measures silver as an 85%-goal-target population rather than a
  grant one, and counts the marker states retail never sends. At the end it reports what the SHIPPING rule would send,
  by calling `QuestMarkerResolver.LightsSilver` directly, scoped to the packets that rule decides (the giver fold owns
  anything with a quest still on offer).
- **`snapshot-eligibility`** — which quests can ride the LOGIN SNAPSHOT, and which arm stops the rest. A held quest
  that fails `SnapshotTails.IsSnapshotEligible` is absent from the journal (there is no live restore path), so this is
  a gameplay bound. The gate that binds is the quest's five header text refs, upstream of the `@609` objective region
  and the record scalars: the scarcest is the SUMMARY (slot 1, LDB category 50302) at 114 of 5,049. Its second table —
  how many quests fail exactly one arm — is **zero** on every row: the capture-only inputs (the summary's per-string
  LDB key, the `Quest_c` record's `Level` word, every goal's wire KIND tag) are all known for the SAME 114 quests,
  because one captured journal carries all three at once, so no arm can be relaxed to widen coverage. DB-only.
- **`daily-kills`** — what stats 1000069 / 1000070 count. **1000070 is a witnessed-NPC-death counter with a hard clamp
  at 20**: where deaths stay under 20 its increments equal them exactly (7 of 15 captures), and four captures with
  29 / 34 / 37 / 39 deaths all stop dead on 20. **1000069 is the daily FATALITY count** — 49 of 49 of its bumps pair
  with the Fatality self-buffs on the player, both ways — and it does NOT clamp at its stated cap of 5. The client
  builds a global tweak accessor for `DailyPvPKillsCap` inside its PvP-XP economy initializers and none for
  `DailyPvEKillsCap`; nothing reads that global but its ctor and its atexit.
- **`stat-series --report-arg <id>[,<id>...]`** — the ordered value sequence of one or more stats per capture and
  handle, out of the captured `StatIIR` stream: a census says a stat is pushed, a SERIES says whether it counts up,
  counts down, resets or is merely restated. Repeats collapse to `20 x3`.

### NPCs, combat and the world

- **`npc-pose-check`** — does every shipped `NpcStatCores` row carry the pose and idle animation the type is USUALLY
  caught in? A `bcc_state` / `anim_idle_id` is the MODE over a type's sightings, so a type whose one captured
  representative was mid-emote ships that emote as its permanent pose. Exit 1 when something is flagged, so it can
  gate a re-import.
- **`field-stability`** — for every field of the captured NPC `CoreCharacter` message: is its value a property of the
  TYPE, of the PLACEMENT, or of neither? Two `GROUP BY` tables over retail's login-burst spawns — by
  `npc_stats.TemplateId`, then by physical map+position. A field no more stable at placement scope than at type scope
  is rolled per SPAWN EVENT and no column can hold it: `VoiceSet` (9.7% / 11.7%), `AnimIdleTimer` (50.2% / 45.2%) and
  `MovementMode` (59.0% / 60.6%), against the vitals block (73.7% / 90.8%) whose `NpcSpawn` columns are the right
  home. A third table gives the VALUE DISTRIBUTION of the fields the first two condemn. The key is the TemplateId, not
  the name — a name spans several templates. Read the third table by FREQUENCY, never by distinct-value count: 92 of
  `AnimIdleTimer`'s 94 distinct values lie strictly between 0 and 1, which reads as a continuous animation phase, but
  they carry only 24% of sightings — the field rests at exactly 0 or 1 and is merely caught mid-blend.
- **`field-predictors`** (`--report-arg <field>`, default `MovementMode`) — for a field rolled per spawn event: is it
  COMPUTED from something else the same spawn carries? Groups every sighting by each candidate and scores the share
  that agree on the target, against a constant's baseline. Candidates are the character message's fields PLUS
  synthetic ones off the spawn's MOVE TASK, the only part of a spawn that says what the entity is DOING. It shows the
  post-IIR header's third word IS `movement_mode` (5,738 of 5,739 NPC spawns and 209 of 209 player ones). Read purity
  together with CARDINALITY: a candidate as fine-grained as the sample scores 100% while explaining nothing — the
  table marks those `(fine)`; a real answer is COARSE and near-100%. Both this report and `field-stability` read
  PRESENCE through `ShouldSerializeX()`, not off the property: protobuf-net generates
  `get => __pbn__X.GetValueOrDefault()`, so an unset optional field reads back as 0. That is how
  `npc_stats.spawn_animation` shows as never sent as 0 — `(unset)` on 5,265 spawns and `1` on 474, a presence flag.
- **`spawn-animation`** — what `npc_stats.spawn_animation` IS. Partitioned into the login BURST and MID-SESSION it is
  not an arrival marker (7.3% of burst spawns against 5.8% mid-session). Read per `(capture, handle, templateId)`, of
  the 4,687 entities sighted more than once **351 are MIXED** (refuting a per-creature capability), **346 of those are
  ON for the first sighting and off thereafter, and 0 ever turn ON** — a **decaying state**, an entity inside a window
  that has closed by the next sighting. The window is bracketed above at 2.6 s and below at 25.6 s, which cannot both
  be one window (the lower bound is one co-streaming group in one capture, so it is weak). In the MonsterData census
  174 of 3,185 records declare a `spawnanim`, and of the templates the corpus flags 275 wear a creature that declares
  none against 14 that do — which does not refute "play this creature's entrance animation", because the client falls
  back to `defaultidleanim` for those (`tools/ghidra/DumpSpawnAnimationConsumer.java`). Needs `--Content:RdbDir` for
  that half.
- **`spawn-duplicates`** — is a cluster of near-identical STATIONARY spawns one NPC harvested twice, or a tight retail
  placement? The verdict comes from `StationaryPlacementResolver`, the same code `npc-placements` stamps
  `PlacementId` from. The test is a HANDLE sighted at two of the cluster's positions — not a distance, and not "the
  same capture saw both": a creature that moves is re-streamed within one session, so reading it per-capture inverts
  the answer. `--report-arg <name substring>` narrows to one creature.
- **`follow-pairs`** — every NPC-follows-NPC binding the corpus carries (the mode-07 formation MoveTasks), both ends
  resolved to creature NAMES with the offsets each follower held. Grouped by creature PAIR, because the formation is
  only visible as a group: the four `Black Dragon Guard Patroller`s behind one captain each hold a different corner of
  a 3x3 grid with their own slot index — the offset is a vector-plus-slot. It prints what the merge WOULD store and the
  residue naming a creature the DB does not hold.
- **`npc-aggression`** — which hostile-COLOURED templates the armed-or-not-humanoid rule leaves passive. Retail colours
  civilians red without them ever fighting, so the name colour and the aggro decision are separate facts; every name
  in the list should read as a civilian, and a mob there is a template whose weapon was never harvested. The rule is
  gated in `NpcAggressionTests`; this prints who it selects.
- **`disposition-rule`** — scores `NpcDispositionTiers`' last-resort hostility inference, and its ablations, against the
  430 templates retail was captured answering a `RequestAggroStatus` for (ground truth no tier contributed to). The
  friendly default gets 0 of 102 hostiles (76.3% overall); `Community > 0` calls **159 polled-FRIENDLY templates
  hostile** and lands below the default at 39.8%; the shipped `CarsGroup == 3 AND a real non-civilian community` gets
  84 of 102 at 95.3% with **zero** friendly-side errors. The collision group ALONE scores higher (97.4%) and is
  deliberately not what ships: the `fills` column shows the two answer the SAME 118 templates on the population that
  reaches the tier, so the community half costs nothing today while halving the neutral-side error (the slice they
  disagree on is where the rabbits, hens and deer sit). Exit 1 on any directly-polled FRIENDLY template the rule
  would make hostile.
- **`broadcast-check`** — which server records retail sends to EVERY player who can see the subject, and which only to
  the client concerned. The oracle is `login_2-clients_group-trade-friend`, the only capture with two game
  connections. Prints each IIR as BROADCAST / per-client / MIXED. Do not automate off the type: a
  `BehaviorControlCenterIIR` carries both an entity animation and an ack of the client's own request, so the record's
  ROLE decides — the MIXED rows are the proof, and `N3ToClientQuit` at 2/44 is a despawn being per-viewer by interest.
  The measurement behind the broadcast rule in [combat.md](../subsystems/combat.md).
- **`bcc-relay`** — for each `BehaviorId`, does retail send a client a record for somebody ELSE's actor handle? It needs
  no multi-client capture, only the recipient's own handle: the client sends its own actions, so the modal actor over
  the c2s records IS the player, and an s2c record naming anyone else is a relay. **19 of the 23 ids the corpus
  carries** are relayed, hence the relay-by-default rule. It prints the CONTROL for the zero-relay ids — how many of
  the captures where the player did it had any foreign BCC actor at all.
- **`bcc-census`** — every BehaviorControlCenterIIR bucketed by command, with the tail lengths each carries (the
  census [behaviorcontrolcenter-grammar.md](../re/behaviorcontrolcenter-grammar.md) quotes); `--report-arg <phase hex>`
  dumps every record of one phase in full.
- **`bcc-attack-tail`** — the attack tail's middle block word by word, under the Command slot the client's reader puts
  each in, with the direction ability resolved.
- **`ambient-animation`** — the two commands that make a standing NPC look alive, `BccAction` and `Emote` (63,743 of
  the corpus' 76,262 BCC records), measured per ACTOR and per creature TYPE: `BccAction` is a p50-1-per-actor assert,
  `Emote` the recurring ambient animation whose next occurrence the previous record's own `activation_time` times.
  `--report-arg <capture substring>` narrows to one capture.
- **`player-actions`** — every `PerformSpecialAction` 0x205A selector in the corpus, named out of the `Abilities`
  catalog (the selector is the ability's grant `Slot`), with the server's answering BCC records and IIRs.
  `--report-arg <selector hex>` prints those answers in full; see [player-actions.md](../subsystems/player-actions.md).
- **`combo-swings`** — every melee-combo swing in the corpus, resolved against the `Combos` catalog: which combo, which
  step, the `bcc_action_id`→direction map, the player/NPC split, and each NPC swing attributed to its actor's own
  spawn. `--report-arg <file>` also writes the matched packets whole, for `--report fixture-set`.
- **`npc-debuff`** — when a MOB lands a spell on the PLAYER, retail **applies once and closes it**: 87 distinct
  (player, mob, spell) pairings, a median of one apply each and never more than four, with the end twin arriving on
  86 of the 87.
- **`melee-crowd`** — several mobs on one player take slots on a RING of about 1–1.5u around the target: dividing each
  crowd's closest walking pair by the ring factor `2*R*sin(pi/N)` gives an implied radius FLAT across crowd sizes
  (1.05, 1.03, 1.06, 0.96, 1.08 for four to eight mobs), where opportunistic packing would fall away with N. It does
  not go through attack records (a mob's `Combat:*` BCC carries an empty target list), and it tracks NPC positions
  from **`MoveTask`** — `CharDCMove` is the PLAYER's movement only (30,769 records, none naming an NPC handle).
- **`interest-radius`** — how far from the player retail DESPAWNS an entity. It measures the despawn side because
  nothing bulk-despawns at login. Named filters: a despawn near a `PlayfieldFIIR` is a ZONE CHANGE, a stale player
  position is a teleport gap, a handle that DIED is a corpse timer, a re-stream is not a crossing, a pool-managed
  handle is the ambient population, and a despawn where the player was not RECEDING is a lifecycle event. Receding
  rather than distance, because filtering by distance would assume the answer. **Answer: ~60 u; 150 u is refuted** —
  76% of the ~750 survivors arrive inside 150. The distribution is a radius and not a smear: zero samples below 20 u,
  a mode at 45–55 u, a decaying tail — and the player's SPEED separates its two halves (median 68 → 391 u from
  2–4 u/s to 12–20 u/s while the p10 does not move), as "boundary plus a variable sweep overshoot" predicts. A filter
  that infers a zone change from a burst COUNT ("more than 3 despawns at once") also matches walking away from a
  camp and keeps only 19 of 23,971 despawns; naming it from the wire keeps ~750.
- **`dynel-cycle`** — every world-object spawn/despawn BURST with the player's position at the time. A single interest
  RADIUS is ruled out one-sidedly — `R` must satisfy every `farthest-spawned ≤ R ≤ every nearest-despawned`, and the
  corpus despawns at 3.5 u while spawning at 1,834 u — then candidate CELL sizes are scored against the same statistic
  over every position the player walked. ~32 u fits.
- **`respawn-interval`** — how long after a kill the creature comes back, and what picks the number. A respawn is a
  NEW entity id, so the signal is the same TEMPLATE back at the dead one's anchor on a handle the client has never
  seen. A cluster of agreeing intervals whose DEATHS are far apart, or which spans two captures, is a timer, not a
  delivery event. It cross-tabs every attribute the spawn body states against the intervals and prints why every
  unmatched kill matched nothing (see the respawn entry in [gaps.md](../protocol/gaps.md)).
- **`death-timing`** — every death BccIIR grouped by its ANIMATION, each actor resolved to a creature name against its
  own capture's spawns: `field12` is the CREATURE's animation length, not the animation's (on 17036, 22 creatures each
  carry one value and none carries two). Unresolved actors are reported separately.
- **`chain-kill`** — every `ChainKillIIR` with the gap to the previous one and to the kill counters `106` / `1000069` /
  `1000070`: counts 2/3 and never 1, every non-zero closed by a 0 a median 8.97 s later, and the counters never move
  with it.
- **`fall-speed`** — every captured FALL paired with the impact SPEED the client reported (the landing `CharDCMove`'s
  `Velocity.Y`): over 39 falls the rank correlation against damage is **0.886 / 1.000** per entity for the client's
  speed against **0.460 / 0.700** for the drop reconstructed from position samples. `Delta` is in HUNDREDTHS of a
  health point, and a fall's airborne run cannot be bounded by the velocity SIGN (the client sends exactly one
  downward-velocity record per fall), so the drop comes from a position-max window. It does not refit `FallDamage`,
  which needs the health pool per event.
- **`duplicate-records`** — which s2c `StatIIR` records retail sends twice, back to back and byte-identical: a whole
  family is **always** a pair (`WaypointType` 336/336, `SoulFragments` 56/56, `GuildID` 38/38, `MovementMultiplier`
  35/35, the hostile flip 3/3). Adjacency is required rather than mere recurrence, or a frequently-sent stat would
  dominate the count.
- **`weapon-set`** / **`weapon-set-drawn`** — can a capture say which WEAPON SET a character had drawn? The drawn set
  rides a stat-0x17 re-registration on the newly-active `Equipped` weapon slot at a swap (slots alternating 6 ↔ 20),
  but not at login, where every equipped slot carries it. Nor does a spawn's worn list: 138 of the 10,990 armed
  spawns carry a primary and a secondary slot at once, so the list is the whole equipment container.
  `--report-arg <name>` breaks it down per character.
- **`travel-trips`** — every local-travel trip the captures yield, with the capture that yielded it (the wiring module
  prints only a count, which cannot tell a fixed attribution from a lost trip).
- **`sprint`** — the sprint/regen family: bodies, the receiver-instance census that decides reply-vs-broadcast, and the
  c2s→s2c pairing.
- **`npc-chatter`** — the ambient-bark catalog's reach: the category-50015 page census, every stored `NpcChatter` row
  resolved to its TEXT through the client's own token hash, and a per-group sweep of `sayinchat(N)` listing every line
  the client ships for that group with the ones the DB lacks marked, split by whether they carry a `{Slot}`; see
  [chat.md](../subsystems/chat.md).

## Decisions

1. **RDB modules are opt-in.** Not in the default set; only run when named via `--only` (and they require
   `--Content:RdbDir`).
2. **Seeds stay inline.** The hand-decoded quest/NPC constants live in their owning code as constants.
