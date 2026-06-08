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

### v6 layout (refined)
v6 **reorders** the sections vs v5 (and adds a hash + name-string section):
```
[header 36 B]  RDHI, version=6, then 7×u32 (873588=hash buckets, 195, 3, 3727, 0, 198, 0)
[section1]     per-record: 16-byte hash + detail fields (~28 B stride looks most likely:
              hash(16) + ~3×u32 holding file(low byte)/offset/size). 36 .. ~7.5 MB.
[name strings] variable-length resource names/paths (~33 MB) — why fixed-stride offset scans fail.
[type+idx]     at the END (bytes ~40.51 M .. 42.66 M): contiguous (type, idx) u32 pairs, 268,792 records.
              CONFIRMED with real values (e.g. type 1000020 with sequential idx 1093924,1093925,...).
[trailer]     ~80 KB.
```
Section1 and the type+idx section are in the **same record order**, so record *i*'s detail (section1) pairs
with record *i*'s (type, idx) — alignable by index without parsing the variable name section.

### Concrete next steps
1. **Crack the v6 detail encoding by ANCHORING on a known resource** (blind validation is too noisy):
   take a known `(type, idx)` from a packet — e.g. the create-world script references `1010003:3147889`,
   `1010003:137283`, ... (the `playorskip` payload) — find that exact `(type,idx)` pair in the type+idx
   section to get its record index *i*, read section1 record *i*, and confirm which u32s give a `(file, offset,
   size)` that resolves to a real resource in `NN.rdbdata`. That pins the detail layout and section1 stride.
2. **Decide tool vs own parser:** either patch the cloned rdbreader's v5 path for the v6 deltas (header size +
   section reorder + detail stride), or write a small v6 index reader in C# (`Faolan.Importer`) reusing the
   v5/v7 field meanings (`file = byte & 0xff`, `offset`, `size`).
3. Then proceed to Phase 1 (type inventory) and Phase 2 (per-type parse), as below. First targets once the
   index reads: `TEXTSCRIPT` (dialogue/strings), then whatever type holds item templates (cross-ref captured
   health-potion template 180450), then `MONSTERDATA` (NPC HP).

### BREAKTHROUGH (2026-06-08): the index is `le.idx` (v5), pipeline validated end-to-end

The whole `RDBHashIndex.bin` v6 effort was a **dead end / wrong file** — that's just the patch *hash* index
(MD5s, no offsets). The real offset index is **`le.idx`** (was missed by the first `find`; it has no `.bin`/
`rdb` in the name). Confirmed via the TSW community docs (`joakibj/tswrdb` `docs/DOCUMENTATION.md` — TSW and AoC
share the format). And crucially **our `le.idx` is version 5**, which rdbreader already fully supports. No v6 RE
needed.

**`le.idx` layout (v5, validated against our files):**
- Header 28 B: magic `IBDR`(4) + version(4)=**5** + MD5(16) + `NumEntries`(4) = 873,588.
- Index: `NumEntries × 8` = `(RDB Type u32, RDB Id u32)` pairs. (195 distinct types.)
- Detail: `NumEntries × 44` (v5 = 11×u32; v7 is 28 B). Fields: `unk0, offset, size, unk3..unk10`;
  **`.rdbdata` file number = `unk0 & 0xff`**, `offset`/`size` locate the entry. (det starts at `28 + N*8`.)
- Then a bundles section.

**`.rdbdata` entry (validated):** at `offset-16` there's a 16 B pre-header `(type, id, len, len+2)`; at `offset`
the data begins with wrapper `IOz1`(4) + uncompressedSize(4) + a **zlib** stream (`78 da`). Decompress
`raw[8:]`; the result starts with the 12 B RDB-Id-data header then the type-specific payload.

**Proven end-to-end:** TextStrings entry (type 1030002, id 211656, file 8) → read 10402 B → `IOz1`/uncomp=28704
→ zlib-inflate to exactly 28704 B → `TDC2` string collection → real strings extracted: *Flags, Health, Volume
Mass, Attack Speed, Clan, Team*.

**What's available (entry counts in our `le.idx`):** TextStrings `1030002`=**1,103**, MONSTERDATA `1010201`=
**3,185** (NPC stats incl. HP), TextScript `1010035`=819, Playfield `1070003`=520, Treasure `1000083`=1,018,
Filenames `1000010`=1 (the id→filename table). Item-template type still TBD (sample types; cross-ref health
potion 180450). TextStrings = the dialogue/UI text (the `TDC2` format is documented); `1030002` category +
the `/data/text/*.tdbl` language lists map strings to languages.

### Implementation
**Done:** `Faolan.Importer --rdb <RDB dir>` with `RdbReader` (le.idx v5/v7 parse + `IOz1`/zlib entry decode,
validated against the real client - 873,588 entries) and a **resource-type inventory** (counts + friendly
names; the foundation for locating the data resources).

**Design decision — do NOT bulk-store text in the DB.** The server references strings by `id`+`category` and
the client renders them from its own RDB, so the runtime DB only needs the **structured data the server can't
get from the client** (item-template identity, NPC stats, spawn/treasure tables) plus the **ids** to reference.
The `TDC2` text format is fully decoded (validated: dialogue in categories like 50013/50014) and can resolve a
specific id on demand for dev, but we won't dump all ~hundreds-of-thousands of strings into `faolan.db`.

**Items from RDB — INVESTIGATED, BLOCKED (encrypted). 2026-06-08.**
Item-template type = **1000020** (98,048 entries; captured health-potion 180450 = record index 31805). Decisive
cross-check against ground truth: of **191** DB buildable items, **190** are present in RDB(1000020), and **all
190 are `RDBH`-wrapped** (0 plain), high-entropy (~7.0), no standard decompression (zlib/IOz1/raw) yields
anything; **0 of the 190 contain the known item `Data1` quad value, and 0 contain the `Archetype` value.** The
plain ~28% of 1000020 resources are test items ("TESTITEM Spell uploader"), not shippable identity. Conclusion:
the buildable identity (archetype + `Data1..4` quad) is **absent / intentionally `RDBH`-encrypted** in the RDB
and **cannot be extracted** without breaking the `RDBH` cipher (a separate major effort). **Buildable items must
still come from captures** (ArchetypeHarvester on `0x737A6DF9` snapshots) — the RDB does not shortcut this.

**Next (structured data → DB):**
1. ~~Item-template identity~~ — **blocked** (see above; `RDBH`-encrypted, identity absent).
2. Decode **MONSTERDATA (1010201, 3,185 entries)** payload → NPC max HP → combat (`CharacterMaxStats`-style or
   an NPC-stats table). NOTE: likely the same `RDBH` wrapping as items — check the wrapper before investing.
3. (later) conversation/quest structure (which ids to reference), Playfield/Treasure.
   Each needs per-type payload RE (the inventory + RdbReader make this straightforward to iterate). Cross-validate
   against ground truth (captured item identities, the lvl-23 sheet, conversation nodes).

### Phase 0 conclusion (2026-06-08)  [superseded by the breakthrough above]
v6 is **fully reverse-engineerable but it's a real project, not a quick crack.** Mapped so far: the resource
TYPE enum, the type+idx section (pinned: 294,125 records at 40,308,696..42,661,696, confirmed real type/idx),
and that the front ~40 MB is multi-layered — header + a hash table (16-byte hashes, sorted by hash so NOT in
type+idx record order) + name strings + a large **reference/dependency table** (~28–40 MB; a commonly-shared
resource like `1010003:3147889` appears ~190× there as `(type,idx)` cross-refs). The **detail table
(file/offset/size) is NOT a fixed-stride block** — every stride/field-arrangement tried resolves <11% of
records against the `.rdbdata` files, so offsets are encoded differently (variable records, packed, or a
separate structure keyed off the hash table). Cracking it needs sustained RE (hash-function identification or
full section delimitation), not more brute-force.

**Recommended pragmatic path: side-step v6 with a v5 client.** rdbreader *fully* supports RDB index **v5
(Age of Conan)**, and the game DATA (items / NPC stats / dialogue / spells) is essentially the same across
client builds. So obtaining an **older AoC client build whose RDB is v5** lets the existing, working tool
extract everything cleanly — no v6 RE required. Order of preference:
1. Run rdbreader on a v5 AoC client RDB (fastest; reuses a working tool). ← recommended
2. Find a community RDB extractor that already handles current-era (v6) AoC clients.
3. Commit to a focused v6 RE session (anchor: identify the hash function and/or fully delimit the front
   sections; the type+idx section is already solved as a (record→type,idx) map).

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
