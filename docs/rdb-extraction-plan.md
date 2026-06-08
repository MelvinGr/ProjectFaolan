# Plan: extract game data from the client RDB files

## Why

Across this project the recurring wall is **"the client renders that from its local RDB, the server never sends
it"**. The RDB (Funcom Dreamworld resource database, shipped with the client) is the authoritative source for
exactly the data we keep failing to obtain from packet captures:

| Open gap | RDB resource that would solve it |
|----------|----------------------------------|
| Buildable items only from player inventory snapshots (`docs/faolan-importer-plan.md`; vendor/AH don't help) | item/template definitions (the client-side `p_items` equivalent: names, stats, archetype, the `Data1..4` identity) |
| NPC dialogue coverage-bound; Casilda's create-world text not capturable (`protocol_conversation_routing`) | conversation/dialogue + `remoteformat`/`localized` string tables (text by id) |
| Combat uses a placeholder NPC max HP (`docs/quests-and-combat.md`) | NPC/creature definitions (stats incl. health) |
| Spells are id-only, names client-side (`CaptureSpellImporter`) | spell/ability definitions (names, params) |
| Create-world intro fails on `invalid camera path "Kalanthes"` (`project_create_editor_flow`) | the Tortage cinematic / camera-path resources |
| World bursts are literal replayed packets, not reconstructed from Maps/MapObjects | playfield / map / spawn-template data |

If even a couple of these come out cleanly, it changes the data strategy from "scrape captures" to "import the
real client data" — far more complete and not coverage-bound.

## What we have

- `<AoC>/RDB/`: **27 × 1 GB `NN.rdbdata`** blobs (~27 GB) + `RDBHashIndex.bin` (41 MB) + `RDBDiffIndex.bin` (1 MB).
- `RDBHashIndex.bin` starts with magic **`RDHI`**, version **6** (`06 00 00 00`), then a header
  (counts: `0x000D5474` ≈ 873k, plus a few more u32s) and fixed-size entries. Each entry looks like a 16-byte
  GUID/hash + a few u32s (type, file index, offset, length) pointing into the `NN.rdbdata` files. Confirming this
  layout is step 1.
- The client itself reads this at runtime (we already see `ResourceManager ... does not exist` for the assets
  it's missing), so the format is exactly what the live client consumes.

## Candidate tool

**`philippelatulippe/rdbreader`** (C/C++): extracts files from Funcom RDB archives; "run it in the directory
containing the RDB directory", configured via `config.h` (output dir + which types to extract). Ships
`fctxconverter` (Funcom texture → DDS) and `lipreader` (LIP audio), and a `structs.h` documenting the format.
Built for VS2012 / Debian Squeeze (≈2013), so it likely predates **RDHI v6** — version/format drift is the main
risk. "or similar": also evaluate any newer AoC/AO community extractors and the AO `RDBExplorer`-style tools
(the RDB format is shared across Funcom's Dreamworld titles).

We care about the **structured data resources** (items/NPCs/spells/conversations/strings), NOT textures/audio/
models — so the texture/audio converters are irrelevant; what matters is whether the tool (a) parses the v6
index and (b) lets us pull resources by **type id**.

## Phases

### Phase 0 — format validation (no extraction yet)
- [ ] Read `rdbreader`'s `structs.h` / `common.h`: does it understand `RDHI` and which version(s)? What is its
      index entry layout and resource-type enumeration?
- [ ] Independently verify the `RDBHashIndex.bin` v6 layout by hand (a small Python parser: magic, header,
      entry stride, `(type, fileIndex, offset, length, id/hash)`). Cross-check a couple of entries against the
      `.rdbdata` files (seek to offset, read length, sanity-check the bytes).
- [ ] Decide: does rdbreader work as-is on v6, need a patch, or do we write our own minimal index parser?
      (A from-scratch index reader is small once the entry layout is known — the 27 GB blobs are just
      offset+length slices.)

### Phase 1 — resource-type inventory
- [ ] Tally entries by **type id** (count + total size per type). This reveals what's in there and how big.
- [ ] Identify the type ids for the data we want by sampling a few blobs per type and looking for: readable
      strings (names/dialogue), the item `Data1..4`/archetype pattern we already know, NPC stat blocks, etc.
- [ ] Map type id → meaning (build a legend, like `docs/capture-events.md` does for packets). Lean on AO/AoC
      community docs where they exist.

### Phase 2 — targeted extraction + parse (one data type at a time, by value)
Prioritise by payoff/effort:
1. **Localized strings / `remoteformat` text** — likely the easiest win and unblocks all dialogue/UI text.
2. **Item/template definitions** — the big one for the importer (names + archetype + `Data1..4` for *every*
   item, not just held ones). Validate by matching a known captured item (e.g. the health potion: f4/f5 template
   180450, archetype `0x4847505A`) to its RDB record.
3. **NPC/creature definitions** — for combat HP and spawn templates.
4. **Spell/ability definitions** — names/params to enrich the id-only `Spell` rows.
5. (later) cinematic/camera-path + playfield/map data.
- For each: extract the resource blobs of that type, reverse the blob's internal record format, decode a handful
  by hand against known ground truth (captures + the screenshots), then write a parser.

### Phase 3 — integrate into Faolan.Importer → DB
- [ ] Add an `--rdb <path>` source to `Faolan.Importer` (alongside `--captures` / `--archive`), with a per-type
      extractor that parses the RDB resources and **upserts** into the existing tables (`Items`, `Spell`,
      `ConversationNode`/text, NPC stats, …). DB stays the single source of truth ([[faolan_importer]],
      [[db_access_model]]).
- [ ] Keep it a **one-time/optional** import like `--archive`: the 27 GB RDB is not committed; only the extracted
      rows land in `faolan.db`. Document the exact client build/version the data came from.
- [ ] Reconcile with captured data: RDB gives the canonical definition; captures still give live wire bytes /
      session specifics. Prefer RDB for identity/text, captures for on-wire framing.

## Risks / unknowns
- **Format version drift:** rdbreader may not read RDHI v6 → may need patching or a own parser (likely small).
- **Compression/encryption:** individual resources may be zlib/LZMA-compressed or obfuscated; need to confirm
  (the GameServer stream is zlib, so zlib-per-resource is plausible).
- **Resource format complexity:** extracting the blob is easy; *parsing* each resource type's internal binary
  layout is the real work (per-type RE, like the packet work).
- **Type-id identification:** which type id is "item" vs "creature" vs "string" needs sampling/community docs.
- **Disk/time:** full extraction is tens of GB; prefer index-driven *selective* extraction by type, not a full dump.
- **Scope creep:** this is a parallel data pipeline — timebox Phase 0/1 before committing to Phase 2+.

## Phase 0 findings (executed 2026-06-08)

Significant de-risking — the tool and format are identified:

- **`rdbreader` is itself a Project Faolan tool** ("Server Emulator for Age of Conan, © 2009–2012 The Project
  Faolan Team", GPLv3) — same lineage as our `Archive/` C++ server, written for AoC's RDB. Source cloned for
  reference. It supports **RDB index version 5 (Age of Conan)** and **7 (The Secret World)**.
- **Our client is RDB index version 6** (`RDBHashIndex.bin` magic `RDHI`, `version=6`) — a newer AoC build,
  **between** v5 and v7, so rdbreader rejects it as-is ("does not support this version"). v6 must be patched in
  or parsed ourselves; it's bracketed by the two supported versions.
- **The `TYPES` enum (structs.h) is the resource-type map we need**: `MONSTERDATA=1010201` (NPC/creature stats →
  combat HP), `TEXTSCRIPT=1010035` (text/dialogue), `SCRIPTGROUP=0` (scripts/cinematics), `LUAARCHIVE=1010223`,
  `NPCSPAWNDATAEXPORT=1000079` / `CONTENTSPAWNDATAEXPORT=1000077` / `TREASUREDATAEXPORT=1000083` (spawns/loot),
  `PLAYFIELDDESCRIPTIONDATA=1070003` (maps), `RESURRECTIONPOINTSEXPORT=1000090`, `CHARACTERCREATOR=1010700`,
  plus media (PNG/JPEG/FCTX/OGG/LIP). **There is NO explicit ITEM type in the enum** (it's a partial list) —
  finding the item-template type id is an early task (sample types not in the enum; cross-ref the known captured
  health-potion template 180450 / archetype `0x4847505A`).
- **v5/v7 index format** (from rdbreader.cpp): header = `magic(4) + version(u32) + hash1..hash4(4×u32) +
  recordCount(u32)`; then a section of `(type, idx)` u32 pairs; then a detail section per record:
  `unk0, offset, size, unk3..unk6` (v7) plus `unk7..unk10` (v5). **File resolution: the `.rdbdata` file number
  is `unk0 & 0xff`; `offset`/`size` locate the blob within `NN.rdbdata`.** (27 data files ⇒ low byte 0..26.)
- **v6 layout (reversed so far):** `recordCount ≈ 268,792`. The `(type,idx)` section is at the **END** of the
  index (bytes ~40.51M..42.66M, 8 bytes/record, exactly 268,792 records). The front ~40.5M holds the header +
  hash table + detail records + (almost certainly) variable-length resource **name strings** (that variability
  is why fixed-stride scans for the detail/offset columns failed). The header field at offset 8 is 873,588
  (a hash-bucket count, not the record count). Exact v6 record/hash/name byte layout is the remaining unknown.

### Concrete next steps
1. **Crack the v6 record layout:** with recordCount≈268,792 and the type+idx section located at the end, work
   backwards to find the detail section (validate by `unk0&0xff ∈ 0..26`, `offset < 1 GB`, `size` sane) and the
   hash/name sections. Validate end-to-end by resolving one record → seek `offset` in `NN.rdbdata` → confirm a
   plausible resource header (FCTX/PNG/zlib/etc.).
2. **Decide tool vs own parser:** either patch rdbreader to accept v6 (add the v6 header delta + section order),
   or write a small v6 index reader in C# (`Faolan.Importer`), reusing the v5/v7 field meanings.
3. Then proceed to Phase 1 (type inventory) and Phase 2 (per-type parse), as below.

## Decision gate
After **Phase 0 + Phase 1** (cheap: index parse + type inventory) we'll know whether the structured game data is
present and extractable. Green-light Phase 2 only if at least the **string table** or **item definitions** look
parseable. If the data is encrypted/opaque or the format is intractable, fall back to the capture-driven approach
and close this out.

## Notes
- Legal/ethical: this reads the **user's own legally-installed client** for a private preservation/interop
  emulator (same spirit as the existing `Archive/` C++ server data + the packet captures). Not redistributing
  client assets; only derived game-data rows land in the local DB.
- Verification ground truth we already have: captured item identities, the lvl-23 character-sheet screenshots
  (`docs/player-stats.md`), and the conversation nodes — use these to validate any RDB parser.
