# Plan: `Faolan.Importer` — pcap → database ingestion tool

## Goal

A standalone CLI tool that reads retail `.pcapng` captures and imports the game data they contain **directly
into the database** — item templates, NPCs, conversations, maps, spells, and (later) appearance/stats. One
canonical, testable C# pipeline that replaces today's scattered, ad-hoc extraction:

- the `ArchetypeHarvestRegenerator` **test** that writes `captured-items.tsv`,
- the Python scripts `Captures/extract_conversations.py`, `dissect_spawn.py`, `compare_log_to_dump.py`,
- the manual "run the server to seed" dance.

"Captures are leading" is the project's core principle; this tool makes the captures a first-class data source.

## Why a tool (not more harvester tests / Python)

- The harvest/extract logic already exists but is split across a unit test, three Python scripts, and runtime
  replay code. It's not reusable, not unified, and the Python duplicates the C# protocol parsing (drift risk).
- We now have **many** real played-character captures (items, NPCs, conversations, multiple maps). Turning them
  into DB rows by hand / per-feature doesn't scale.
- A tool gives a repeatable `capture → DB` step that anyone can re-run when new captures arrive.

## What we reuse (already built)

- **`Faolan.PacketAnalyzer.PacketAnalysisService.AnalyzeCapture(path)`** → `PacketAnalysisResult` with an
  `IReadOnlyList<AnalyzedPacket>` — already does SharpPcap reading, **TCP reassembly**, zlib inflation, and Conan
  framing. The importer references this project and gets decoded packets for free (no pcap re-parsing).
- **`AnalyzedPacket`** (raw bytes + opcode + sub-opcode + direction + endpoints) — the unit each extractor reads.
- **`InventorySnapshotInspector`** + **`ArchetypeHarvester`** — parse `0x737A6DF9`; extract item identity
  (template id + `Data1..4` quad + archetype). Already produces the exact data the `ItemExtractor` needs.
- **Spawn dissection** (`Spawn/SpawnPacketBuilder.cs`, `dissect_spawn.py` logic) — `FA015807` → handle, position,
  rotation, name, race/appearance fields.
- **Conversation extraction** (`extract_conversations.py` logic, `ConversationReplay.cs`) — `Interact`
  `(TypeId, Mode, Field3)` → response-packet nodes.
- **DB layer** — `IDatabaseRepository` (`Execute`/`Query` units of work over a pooled `DbContextFactory`) and the
  EF entities (`Item`, `Npc`/`NpcBody`/`NpcEquipment`, `Map`, `Spell`, …). See `docs` / README "Architecture".

## Architecture

New project **`Faolan.Importer`** (console exe), referencing `Faolan.PacketAnalyzer`, `Faolan.Core`, and `Faolan`
(for the existing parsers/extractor logic that lives under `Faolan/GameServer/...`).

```
pcapng ──▶ PacketAnalysisService.AnalyzeCapture ──▶ AnalyzedPacket[]
                                                        │
                            ┌───────────────────────────┼───────────────────────────┐
                          ItemExtractor            NpcExtractor              ConversationExtractor   (…)
                            │                           │                           │
                            └──────────── ImportSink (idempotent upsert, unit of work) ───────────┘
                                                        │
                                                      faolan.db
```

- **`ICaptureExtractor`**: `Task ExtractAsync(IReadOnlyList<AnalyzedPacket> packets, IImportSink sink, ImportReport report)`.
  Each extractor owns one data domain; pure parse → typed records → `sink`.
- **`IImportSink`**: idempotent upsert per entity (`UpsertItem`, `UpsertNpc`, `UpsertConversationNode`, …),
  backed by `IDatabaseRepository`/`DbContextFactory` short-lived units of work. Upsert by stable key (see below).
- **`ImportReport`**: per-extractor counts (inserted/updated/skipped) + conflicts, printed at the end. Same spirit
  as the harvester's conflict log.
- **CLI**: `Faolan.Importer <capture.pcapng | dir> [--db <path>] [--items] [--npcs] [--conversations] [--maps]
  [--dry-run]`. No flags = all extractors. Accepts a directory to batch-import every capture. `--dry-run` reports
  what would change without writing. Defaults the DB path to the repo `faolan.db`.

## Extractors (priority order)

1. **Items** (`0x737A6DF9`) — DONE logic, just relocate. Move `ArchetypeHarvester` extraction into
   `ItemExtractor`; upsert `Item` by template id (`Item.Id`): set `Data1..4` + `Archetype`; keep an existing
   `p_items` name/level if present, else leave name empty. This replaces the `captured-items.tsv` regenerator.
   **Stable key: template id** (`tail.f4`). Solid.
2. **Conversations** (`Interact 642CD3D6` → `4A3D899A`/`F164A4A5`/`045B0B0F`) — port `extract_conversations.py`.
   A new `NpcConversations` table (or reuse the embedded `.bin` format) keyed by `(TypeId, Mode, Field3)` →
   ordered response packets (with the captured NPC handle + instance recorded for runtime substitution, exactly
   as `ConversationReplay` expects). **Stable key: `(TypeId, Mode, Field3)`.** Solid; biggest content win.
3. **Maps** (`MapChange 5D85BFC7`) — upsert `Map` by map id with the start position/rotation. **Stable key: map
   id.** Easy.
4. **NPCs** (`FA015807` spawns) — name, position, appearance/model, equipment. ⚠ **Identity problem**: the spawn's
   entity handle is session-scoped, so it is NOT a stable NPC id. Options: derive identity from the NPC's
   model/template field in the spawn body, or match against the archive `npcs` table by name+map, or store
   capture-NPCs in a separate `CapturedNpc` staging table keyed by (name, map, model). Needs a short dissection
   spike before this extractor is reliable. Until then it can populate a staging table for review.
5. **Spells / skills / abilities** — a played character's captures carry its spellbook/skills: the create-editor
   and in-world bursts include `ApplySpell` (`0x6B87E316`) and ability/feat packets, and the stats screenshots
   (`Captures/screenshots/`) list the character's feats/spells with mana costs as ground truth. Extract the
   spell/ability **definitions** (id, name, level, cast time, costs, effect) -> upsert `Spell` (the archive
   `spelldata` seeded 21; real captures add the rest), and the character's **known set** -> a `CharacterSpells`
   table (later, alongside stats). **Stable key: spell/ability id.** Needs a short dissection spike to pin the
   spell/ability packet layout (mirror the item work). Mid priority - do after conversations/maps.
6. **Appearance & stats** (later) — the spawn `field4` morphs and the character stats (inventory tails / a stats
   packet). Both are still being reverse-engineered (see `spawn-dissection-plan.md`); wire these extractors once
   the encodings are cracked. The stats screenshots in `Captures/screenshots/` are the ground truth.

## DB write strategy

- Each extractor's writes go through the repository's unit-of-work model (short-lived context per batch). No
  long-lived context; matches the game-server persistence model (see README "Architecture").
- **Idempotent upsert** keyed by the stable id above, so re-running over the same/overlapping captures converges
  (no duplicates) — same guarantee the current importer/harvester gives.
- **Conflict policy**: if two captures disagree on a field for the same key, prefer the named/p_items source and
  log the conflict (the harvester already proves conflicts are ~0 for item identity).

## DB is the single source of truth — retire the embedded artifacts (decided 2026-06-07)

`faolan.db` is committed in git, so the database *is* already the versioned store. The embedded data resources
(`archive/*.sql`, `captured-items.tsv`, `conversations-*.bin`, `strie-login-worldburst.bin`,
`spawn-player-template.bin`) and the startup auto-seed exist only to self-heal an empty DB - they duplicate what
the committed DB already holds. New model:

- **The server carries NO embedded data** and does NOT auto-seed; `DatabaseMigrator` only migrates the schema.
  Everything is read from the committed `faolan.db` at runtime.
- **`Faolan.Importer` is the one way to (re)build the DB** from the raw sources kept in the repo: the
  `Captures/*.pcapng` and the archive `.sql` files (read from disk, exactly like the captures - source files for a
  tool, not blobs compiled into the server).
- Reverting `faolan.db` restores the committed, data-full DB; re-run the importer when captures/sources change.

Migration order: items/spells/NPCs (archive SQL + pcap items) first - DONE here; conversations -> a DB table
(Phase 2) retires `conversations-*.bin`; the replay scaffolding (`strie-login-worldburst.bin`,
`spawn-player-template.bin`) is transitional (the dissection plan replaces it with DB-driven world entry) and is
the last to move.

## Relationship to the current seed path

- The **base archive data** (`p_items`/`spelldata`/`npcs` from `Archive/`, embedded SQL) stays as the
  startup seed (`ArchiveDataImporter`) so a fresh DB still bootstraps without any capture.
- `Faolan.Importer` **enriches** that DB from captures: capture-only items, conversation trees, real maps/NPCs.
- Decision to make: do capture-derived rows live (a) committed in `faolan.db` (run the tool, commit the db), or
  (b) re-baked into embedded seed artifacts the server loads on startup? **Recommendation:** primary mode writes
  straight to the DB (what was asked); keep an optional `--emit-seed` mode that regenerates the embedded
  artifacts (`captured-items.tsv`, `conversations-*.bin`) so a from-scratch DB can still self-seed. This lets the
  tool *retire* the regenerator test and the Python scripts without losing the self-seeding property.

## Phases

- **Phase 0 — DONE (2026-06-07).** `Faolan.Importer` scaffolded (console exe, references `Faolan` +
  `Faolan.PacketAnalyzer`, added to the solution). CLI: `Faolan.Importer [<capturesDir>] [--db <path>] [--dry-run]`,
  defaults to the repo `Captures/` + `faolan.db`. Reads packets via `PacketAnalysisService.AnalyzeCapture` for
  `.pcapng` (skipping those with a `.json` sibling), the `.json` dumps' `RawBytesBase64`, and raw `.bin`.
- **Phase 1 — DONE (2026-06-07).** Item import: `CaptureItemImporter` (in `Faolan`, takes raw packet bytes →
  `ArchetypeHarvester` → upsert `Items` with quad+archetype, insert capture-only) wired into the tool. Verified:
  reads 52,658 packets from `Captures/`, upserts 177 item identities; DB = 384 items / 177 buildable. **Retired**
  the `captured-items.tsv` regenerator test, the embedded `captured-items.tsv`, the archive `.sql` embeds, and
  `ArchiveDataImporter` (the base archive data was imported once and now lives in the committed `faolan.db`); the
  server's `DatabaseMigrator` is now **schema-only** (no seeding). Tests reseed temp DBs via `CaptureItemImporter`.
- **Phase 2 — DONE (2026-06-07).** `ConversationNode` + `ConversationPacket` tables (migration `AddConversations`,
  unique index on (TypeId,Mode,Field3), packets cascade). `ConversationExtractor` ports the Python script
  (find TalkToNpc 642CD3D6/actionType 0x16 → collect following 4A3D899A/F164A4A5/045B0B0F/0x2015 until the next
  talk; `Merge` dedups across captures preferring a 4-byte-varint instance then most packets). The tool full-
  replaces the conversation tables each run (verified: **12 nodes / 96 packets**, matching the old `.bin`).
  `ConversationReplay` now reads from the DB via `IDatabaseRepository.GetConversationNode`. **Retired**
  `conversations-strie.bin` (+ its embed) and `Captures/extract_conversations.py`. Tests:
  `ConversationExtractorTests` (extract windowing + merge); server starts clean.
- **Phase 3 — DONE (2026-06-07).** `CaptureMapImporter` reads `MapChange` (0x2000 / 0x5D85BFC7) packets - mapId
  @ inner[30], spawn position @ inner[13] (3 BE floats) - and INSERTS maps not already in the DB (existing
  curated rows left untouched). Wired into the tool; verified it discovered 3 new maps (2010, 2040, 4000) from
  the captures (9 -> 12). Test: `CaptureMapImporterTests` (real King Conan's Castle MapChange -> map 2040). NOTE:
  the position is the character's spawn at map change (representative, not canonical), so it only seeds new rows;
  the curated maps' positions still carry an old decimal-shift bug - a separate cleanup.
- **Phase 4 — DONE (2026-06-08).** Spike: the FA015807 spawn carries the NPC **name** + position + appearance but
  NOT the archive's numeric id/model, and entity **handles are session-scoped** - so the stable key is (map,
  position). Chose the **raw-spawn catalog**: `NpcSpawn` table (MapId, Name, CapturedHandle, position, raw FA015807
  Data), migration `AddNpcSpawns`, unique index (MapId, position). `NpcSpawnExtractor` tracks the current map
  (MapChange) and, per spawn, reads handle@body12 (skips the player - handle >= 0x02000000), position@24/29/34 (LE),
  and a best-effort name (`0A <len> <ascii>` scan past the position block). Imported **1528 distinct NPC spawns
  across 9 maps**, all named (Conarch Village Guard, Nikulas the Smith, ...). Upsert by (map, position), no wipe.
  Tests: `NpcSpawnExtractorTests`. CONSUMER: future - a world-entry-from-DB step replays these per map (re-addressing
  the handle) the way the beach world-burst does today (see docs/spawn-dissection-plan.md).
- **Phase 5 — DONE (2026-06-08).** Spike found the spellbook is NOT in captures (class/level-derived client-side);
  captures only carry cast-spell ids (ApplySpell). So spells = two sources: (1) **archive** ability/spell defs -
  `ArchiveSqlImporter` (`--archive <dir>`) imports `p_abilities` -> `Ability` (id + Data0..4 identity quad) and
  `const_abilitys` -> `ClassAbility` (class/level -> ability+spell id lists); new tables, migration `AddAbilities`,
  upsert (no deletes). (2) **captures** - `CaptureSpellImporter` harvests cast spell ids from ApplySpell
  (0x6B87E316; spell id at inner[13]) and catalogs them id-only in `Spells` (~119 from the dumps). Names/params
  stay client-side/`spelldata`. Tests: `ArchiveSqlImporterTests`, `CaptureSpellImporterTests`. NOTE: archive defs
  are tiny (2 abilities / 3 class rows - all the archive has); the rest of the spell catalog is the harvested ids.
- **World bursts — DONE (2026-06-08).** `MapWorldBurstExtractor` stores each map's full login world build (the
  `0x2000` downloads from MapChange to the first `0x2020`, + the captured player instance) in `MapWorldBurst`/
  `MapWorldBurstPacket` (migration `AddMapWorldBursts`). Beach (4010) is seeded from the proven embedded blob
  (`SeedBeachBurstFromBlob`, 71 packets) so it stays byte-identical; other maps from captures. CONSUMER (done):
  `ReplayMapWorldBurst` replays the burst per map on login (re-addressed to the live char), generalizing and
  retiring the embedded beach-only path. So login now renders ANY map with a captured burst. Tests:
  `MapWorldBurstExtractorTests`. KNOWN: maps whose captured player instance is out of the 4-byte-varint range
  (e.g. 1000/11040) don't re-address cleanly yet (extractor mis-detected the instance there).
- **Phase 6** — appearance/stats extractors, once the encodings are cracked (stats screenshots are ground truth).
- **Cleanup** — delete the Python scripts and the regenerator test once their C# extractors land (keep
  `compare_log_to_dump.py` only if still useful for diffing).

## Risks / open questions

- **NPC stable identity** from session-scoped handles (Phase 4) — needs a dissection spike; biggest unknown.
- **Conversation storage**: new `NpcConversations` table vs. keep the embedded `.bin`. A table is cleaner and
  query-able; the `.bin` is what `ConversationReplay` already reads. Pick when starting Phase 2.
- **Capture-derived data provenance**: keep a `source` column (capture file name) on imported rows for
  traceability/debugging? Cheap and useful.
- **Appearance/stats** remain blocked on the encoding work, independent of this tool.

## Acceptance (per extractor)

1. Re-running the tool over the same captures is idempotent (no dupes; stable counts).
2. The imported data reproduces what the feature needs in-client (items build byte-exact; conversations replay).
3. A unit/integration test per extractor over a known capture asserts the rows it produces (mirrors the existing
   `SnapshotItemBuilderTests` / `ArchiveDataImporterTests` style).
