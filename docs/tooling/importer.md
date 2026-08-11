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
`--report <name>` (`conversation-states`, `anim`, `worldobj`, `spell-fields`, `rdb-assets`,
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
`spawn-animation` (#607: is `npc_stats.spawn_animation` a property of the spawn EVENT? Partitions every captured NPC
spawn into the login BURST and MID-SESSION, which is the class of predictor a field census cannot see. ⛔ It
FALSIFIED the ticket's own hypothesis - 7.3% of burst spawns carry the flag against 5.8% mid-session, so it does not
mean "this entity is arriving"),
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
controls — the measurement that rules the catalog OUT as a source of step order;
see [quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)),
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
