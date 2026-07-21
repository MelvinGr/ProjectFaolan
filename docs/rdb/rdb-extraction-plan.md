# Plan: extract game data from the client RDB files

> **STATUS: COMPLETE — kept as the derivation reference, not open work.** The live extraction pipeline is the
> module-based importer ([`../tooling/importer-plan.md`](../tooling/importer-plan.md)); the recovered asset
> formats are documented in this folder (`rdb-type-catalog.md`, `gear-rendering.md`, `skeleton-grammar.md`, …).

## Why

Across this project the recurring wall is **"the client renders that from its local RDB, the server never sends
it"**. The RDB (Funcom Dreamworld resource database, shipped with the client) is the authoritative source for
exactly the data we keep failing to obtain from packet captures:

| Open gap | RDB resource that would solve it |
|----------|----------------------------------|
| Buildable items only from player inventory snapshots (vendor/AH do not help) | item/template definitions (the client-side `p_items` equivalent: names, stats, archetype, the `Data1..4` identity) |
| NPC dialogue coverage-bound; Casilda's create-world text not capturable (`protocol_conversation_routing`) | conversation/dialogue + `remoteformat`/`localized` string tables (text by id) |
| Combat uses a placeholder NPC max HP (`docs/re/quests-and-combat.md`) | NPC/creature definitions (stats incl. health) |
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
- [ ] Map type id → meaning (build a legend, like `docs/tooling/capture-events.md` does for packets). Lean on AO/AoC
      community docs where they exist.

> ## ⭐ CONVERSATIONS — SCOPED & PINNED (2026-06-11): RDB dialogue is a quest VM, not a table import. DEFERRED.
> Inspected the dialogue resources in the live client RDB (`Faolan.Importer --rdb`):
> - **TextStrings (1030002, TDC2)** = the dialogue TEXT, by string-id — resolvable for any NPC/state.
> - **LuaArchive (1010223)** = the dialogue LOGIC/branching, as **Lua** with quest-state TAG conditions
>   (`if player:HasTag(3104) then return false else return true end ... player:AddTag(2742)`).
> - **TextScript (1010035)** = a generic XML node-graph (textures etc.), mixed-purpose; **ScriptGroup (0)** = 0 entries.
> - There is **NO single "conversation tree export" type** — branching IS the quest system (player tags).
>
> So a faithful RDB conversation engine needs a **Lua interpreter + a player-tag/quest-state system + TextStrings(TDC2)
> resolution + Knubot script extraction + a server Knubot VM** — a major multi-session subsystem entangled with quests.
>
> **VM ENGINE REMOVED (2026-07-21) — see `lua_vm` memory.** A Lua VM was built (2026-06-11, MoonSharp) to run the
> RDB scripts against a character, but the whole premise was dead: the recovered LuaArchive scripts are client-side
> and unlinked to our dialogue (see below), and the interpreter's only live use was three trivial quest-state gates on
> Casilda. Those are now structured columns — `ConversationNode.ConditionQuestId` + `ConditionQuestState` (matched in
> C#: `HasCompletedQuest(X)` == `(X, Completed)`, `HasQuest(X) and not HasCompletedQuest(X)` == `(X, Active)`) — so no
> Lua/MoonSharp remains. The `CharacterTag` store (the `player:HasTag/AddTag` quest-flag mechanism) is KEPT for future
> tag-gated content. (The 1992 RDB LuaArchive scripts had earlier been bulk-imported to a `LuaScripts` table, but that
> table + its `--rdb-lua` importer + the `.lua run` command were removed first as near-worthless: ~59% are one-line
> `player:AddTag(n)`, the rest mostly Funcom-shop upsells / PvP-saga / event cruft, none linked to our dialogue.)
> REMAINING — and **WALLED (checked 2026-06-11):** wiring the VM into conversations/quests needs the TREE definition
> (which node runs which script, which option → which next node, which quest has which goals). That tree is **NOT in
> the client RDB** — inventoried all 195 types: the RDB has the SCRIPTS (LuaArchive) + TEXT (TextStrings) but no
> conversation/quest/knubot/dialog definition type (the XML types are textures/effects/playfield/animation/combo/
> fatality only). The tree was **server-authoritative content, never shipped to clients**, so it's not recoverable
> from the RDB. Consequence: the VM can RUN any script (and apply effects) but we can't know which scripts retail's
> conversations/quests used. So: capture-replay (`protocol_conversation_routing`) stays the way to reproduce captured
> NPC dialogue; the VM is usable for HAND-AUTHORED / server-defined scripted content. Reconstructing trees from
> captures (correlate each KnubotAnswer with the following AddTag/QuestIIR effect) is the only data-driven path, and
> it only covers captured conversations.

### Phase 2 — targeted extraction + parse (one data type at a time, by value)
Prioritise by payoff/effort:
1. **Localized strings / `remoteformat` text** — likely the easiest win and unblocks all dialogue/UI text.
2. **Item/template definitions** — the big one for the importer (names + archetype + `Data1..4` for *every*
   item, not just held ones). Validate by matching a known captured item (e.g. the item Daggamalt: f4/f5 template
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
- **(Resolved — see "The index is `le.idx` (v5)" below.)** `RDBHashIndex.bin` (RDHI v6) is only the patch hash
  index; the real offset index is `le.idx` **version 5**, which rdbreader supports — there was never a v6 index to
  reverse. (This bullet's original "client is v6, unsupported, must patch" worry was the dead end.)
- **The `TYPES` enum (structs.h) is the resource-type map we need**: `MONSTERDATA=1010201` (creature MODEL data -
  animsys/skeleton/mesh refs + name, NOT combat stats; checked 2026-06-11 - NPC **HP is NOT in the client RDB**, it
  was server-authoritative: use the captured spawn `CharacterStats.health` / the Archive-imported `Npc.Health`),
  `TEXTSCRIPT=1010035` (text/dialogue), `SCRIPTGROUP=0` (scripts/cinematics), `LUAARCHIVE=1010223`,
  `NPCSPAWNDATAEXPORT=1000079` / `CONTENTSPAWNDATAEXPORT=1000077` / `TREASUREDATAEXPORT=1000083` (spawns/loot),
  `PLAYFIELDDESCRIPTIONDATA=1070003` (maps), `RESURRECTIONPOINTSEXPORT=1000090`, `CHARACTERCREATOR=1010700`,
  plus media (PNG/JPEG/FCTX/OGG/LIP). **There is NO explicit ITEM type in the enum** (it's a partial list) —

> **Resurrection points (1000090) — present but v6-gated (checked 2026-06-11).** `le.idx` (v5) has **1024**
> `RESURRECTIONPOINTSEXPORT` entries (sizes ~148–350 B = room for map id + coords + name), but their detail records
> carry **`file=0xFF` (sentinel, not in the numbered `NN.rdbdata`)** + a **16-byte content hash**
> (e.g. `d90f6ce15e8c6b48c0e761980e6072ee`). That hash resolves in **`RDBHashIndex.bin` (v6, RDHI)** — its 32-byte
> detail record is `[size:u32][pad][16-byte hash][trailing 8B]`, size matching le.idx. So the data IS local and
> locatable, but reading the bytes needs the **v6 content-addressed resolution (hash → blob offset)** = the open v6
> detail-encoding crack below. The RdbReader v5 path (which works for items, type 1000020, `file<0xFF`) can't reach
> these. Bottom line: respawn-point coordinates exist in the RDB; extracting them is blocked on the v6 work.

> **CRACKED (2026-06-11) — `file==0xFF` resources are CDN content, fetched by hash.** There is NO local v6 index to
> crack: the resurrection content hash is in ZERO of the 28 `NN.rdbdata` files. `file==0xFF` simply means the resource
> isn't packed locally — it's on-demand CDN content. **The le.idx 16-byte content hash IS the CDN path md5**:
> `<gameupdate host>/upm/rdb/res/<h[0:2]>/<h[2:3]>/<h[3:]>` returns the SAME `IOz1`+zlib blob as the local store
> (verified HTTP 200, byte-sizes match le.idx). So any `file==0xFF` resource is readable: take its le.idx hash → fetch
> → UnwrapIOz1. `RdbReader.Entry` now exposes `Hash` + `IsLocal`. **Resurrection points DONE**: `Faolan.Importer
> --only rdb-respawns --rdb <dir>` (`RdbRespawnImporter`) fetches type 1000090 for our maps, parses the XML
> (`<resurrectionpoint ...><position x y z>`; record id == playfield/map id; `discoverable="false"` => AlwaysKnown),
> and upserts `RespawnPoints` keyed on the RDB `Guid` — **557 points across 259 maps** (beach 4010 = 3 real points).
> The SAME path now unlocks the other v6-gated types (the ~279 v6 maps, TREASUREDATA) — fetch by hash. Like every
> rdb-* module it goes through the mirror proxy (`--rdb-proxy`, default `http://127.0.0.1:7081/gameupdate/upm/rdb`),
> which store-and-forwards, so pulls are archived for preservation — it does NOT hand-roll its own CDN fetch. NOTE
> most of our maps' resurrection records are actually LOCAL (240 local / 784 CDN); the CDN path is only for the
> on-demand ones.
>
> The XML's `name` is the world-EDITOR object label, NOT a display name, and is not unique (95 points are
> "ResurrectionAIPoint0"); no display-name/text-id attribute exists on the element at all, so a player-facing name is
> not recoverable from this file (#372). Also carried: `RestorationLevel` (a percent) and `Orientation` (yaw radians),
> both now imported.
> (MONSTERDATA = model data, not HP — NPC HP comes from the captured spawn / Archive `Npc.Health`, not the RDB.)

> **Loot tables — NOT extractable (checked 2026-06-11).** `TREASUREDATA` (1000083, 1018 recs, per-playfield XML) is
> treasure-container PLACEMENT only (position/level/name/mesh/`ScryScriptID`), NO item contents. Neither the RDB nor
> the Archive C++ DB (`Archive/projectfaolan.db`: 15 tables - `npc_items` = NPC worn equipment, `items` = item DB,
> NO loot/drops table) has probabilistic loot tables — AoC loot was server-script-driven (ScryScripts), which we
> don't have. So loot-bag contents can't be sourced from data; loot stays blocked (no contents + the in-client-gated
> live grant). The Archive `npcs` has real `health`/`getExp` but our `Npcs` table imported only 4 rows, so it's not a
> useful HP/XP source either (captured-spawn HP is the real source already in use). **The data-extraction thread's
> high-value results are done: map names, resurrection points, the CDN fetch-by-hash+archive pipeline, real spawn HP.
> Remaining gameplay values (loot, most XP) are server-authoritative / absent — pivot to gameplay, not more extraction.**

> **Map names + ids (1070003) — EXTRACTED (2026-06-11).** `PLAYFIELDDESCRIPTIONDATA` has 520 entries; **241 are
> v5-reachable** (file < 0xFF), 279 v6-gated. Each readable record is XML `<playfielddescriptiondata>` with
> `<playfield Name=".." ID=".."/>` (+ `ShowInGUI`); the **RDB record id == playfield id == our `Map.Id`** (verified
> 4010 Tortage Island, 2040 Castle of King Conan, 3000 Khemi, 1000 Conarch Village, ...). `Faolan.Importer
> --rdb <dir> --rdb-maps` (RdbMapImporter) imports them into `Maps` (id+name; creates reference rows for unknown
> maps, renames existing) — isolated, touches only `Maps`. Imported: 241 read, 226 created, 8 renamed (242 maps total).

  finding the item-template type id is an early task (sample types not in the enum; cross-ref the known captured
  Daggamalt template 180450 / archetype `0x4847505A`).
- **v5/v7 index format** (from rdbreader.cpp): header = `magic(4) + version(u32) + hash1..hash4(4×u32) +
  recordCount(u32)`; then a section of `(type, idx)` u32 pairs; then a detail section per record:
  `unk0, offset, size, unk3..unk6` (v7) plus `unk7..unk10` (v5). **File resolution: the `.rdbdata` file number
  is `unk0 & 0xff`; `offset`/`size` locate the blob within `NN.rdbdata`.** (27 data files ⇒ low byte 0..26.)
### The index is `le.idx` (v5); there is no "v6 index" to crack

(Supersedes an earlier dead-end effort to reverse `RDBHashIndex.bin` as a v6 offset index.) `RDBHashIndex.bin`
(RDHI v6) is just the patch **hash** index (per-resource md5s, NO offsets). The real offset index is **`le.idx`,
version 5**, which rdbreader fully supports — no v6 RE needed. Confirmed via the TSW community docs
(`joakibj/tswrdb`; TSW and AoC share the format). A resource's data is either packed in the local `NN.rdbdata`
(`le.idx` detail gives `file`/`offset`/`size`) or, when `file==0xFF`, **not local at all** — it's CDN content
fetched by the 16-byte content hash (see the "CRACKED" note above; the hash is the CDN path md5).

**`le.idx` layout (v5, validated against our files):**
- Header 28 B: magic `IBDR`(4) + version(4)=**5** + MD5(16) + `NumEntries`(4) = 873,588.
- Index: `NumEntries × 8` = `(RDB Type u32, RDB Id u32)` pairs. (195 distinct types.)
- Detail: `NumEntries × 44` (v5 = 11×u32; v7 is 28 B). Fields: `unk0, offset, size, unk3..unk10`;
  **`.rdbdata` file number = `unk0 & 0xff`**, `offset`/`size` locate the entry. (det starts at `28 + N*8`.)
- Then a bundles section.

**`.rdbdata` entry (validated):** at `offset-16` there's a 16 B pre-header `(type, id, len, len+2)`; at `offset`
the data begins with wrapper `IOz1`(4) + uncompressedSize(4) + a **zlib** stream (`78 da`). Decompress
`raw[8:]`; the result starts with the 12 B RDB-Id-data header then the type-specific payload. Some entries have
no wrapper (the 12 B header is the first thing); a few are `RDBH`-wrapped (see encryption below).

### RDB encryption (`RDBH`) — what we know (2026-06-08)
Investigation findings from this client's RDB. **Only item-templates are encrypted; nothing else is.**

- **Scope:** of every resource type, **only `1000020` (item-templates) carries `RDBH` resources** — and ~all of
  them are wrapped (sampled 98,048 entries; a small minority are plain binary item structs, the rest `RDBH`).
  No other type (models, text, playfields, treasure, combos, …) is ever `RDBH`. TSW, same engine (rdbreader /
  tswrdb), encrypts **nothing** — `RDBH` is an AoC-only protection applied surgically to the item database.
- **Container layout (20-byte header + ciphertext):**
  `"RDBH"`(4) + `version u32 = 1` + `flags u32 = 1` + `plainLen u32` (unpadded plaintext length) +
  `reserved u32 = 0`, then the ciphertext = `entrySize - 20` bytes.
- **64-bit block cipher:** the ciphertext length is **always a multiple of 8**; `plainLen` is the true length and
  `ciphertextLen - plainLen` is uniformly 0..7 (block padding). Multiple of 8 but **not** 16 → an 8-byte-block
  cipher (Blowfish/DES/TEA/XTEA/CAST/IDEA family), **not AES**.
- **Not ECB:** across 5,537 items / 129,371 ciphertext blocks, **zero** 8-byte blocks repeat anywhere (intra- or
  inter-resource). With 98k items sharing common plaintext (the `1000000` marker + shared archetype fields) ECB
  would collide massively → it's **CBC** (per-resource IV, presumably derived from `type`/`id` since the header
  has no IV field) or a per-resource-keyed stream/CTR mode. Byte histogram is uniform (256 values, ~0.39% each)
  → real encryption, not a short XOR/obfuscation.
- **Known-plaintext is available but doesn't help recover the key:** plaintext block 0 = `type(1000020 LE)` +
  `id(LE)` (fully known per resource), and the *plain* `1000020` resources reveal the whole binary item-template
  layout (12 B RDB-Id header `type,id,marker=1000000`, then binary fields). Known PT/CT pairs do not break a
  proper 64-bit cipher.
- ~~**The key is in the client.**~~ **SUPERSEDED — see the binary RE below: the cipher is TEA, but the key is
  NOT in the client; it is server-issued per template.**

### RDBH cracked via static RE of AgeOfConan.exe (2026-06-08)
Reverse-engineered the client (capstone/pefile; no ghidra). **The cipher is TEA and the routine is identified;
the remaining wall is the KEY, which is a server secret, not unknown crypto.**

- **Cipher = standard TEA** (the 8-byte-block guess was right). Decrypt routine at exe va **`0xa8f550`**:
  32 rounds, sum starts `0xc6ef3720`, step `-0x9e3779b9` (the single occurrence of `0xc6ef3720` in `.text` is
  inside it). Key = 4 LE u32 read at `[ctx+0]..[ctx+0xc]`; standard TEA decrypt order
  (`v1 -= ((v0<<4)+k2)^(v0+sum)^((v0>>5)+k3); v0 -= ((v1<<4)+k0)^(v1+sum)^((v1>>5)+k1)`). Same TEA *family* as
  login (`tea_unexpand`), but a **different, separate use** — see below.
- **Call chain:** `RDBServerSource::Decrypt` (vtable `0x203655c`, **slot 5** = va `0x1064d40`) → `0x106fcb0`
  (reads the `RDBH` magic `cmp eax,0x48424452`, dispatches on version/flags) → `0x106fef0` → TEA `0xa8f550`.
  Module strings: `RDBSource.cpp`, `RDBSource`, `RDBServerSource.log`. The MD5 (`0x67452301…` init consts) seen
  nearby is the **cache-integrity** check (`Deleted a corrupted entry from the cache (wrong MD5)`), *not* the
  TEA key.
- **The key is server-issued, per template.** The decrypt path requires `!cID.GetDecryptionKey().IsNull()` and
  logs `Resource %u:%u does not have a decryption key`. The key is a **`DecryptionKey_c`** delivered over the
  network: protobuf field **`ProtoDecryptionKey`**, and
  `GameCharAgentProxy_t::RequestRecipeInfo(uint32 levelId, uint32 templateId1, uint32 templateId2,
  DecryptionKey_c key1, DecryptionKey_c key2)` — i.e. **keys are paired with specific template ids** ⇒ per-template,
  no single master key in the client. Gate at va `0x8cb40d`: only types **`1000020`** (item-templates) and
  **`1000620`** (recipe-templates) are encrypted; `Template item %u:%u (type %u) was used without a decryption
  key` is the missing-key warning.
- **Why it works this way:** in normal play the client never decrypts these RDB blobs — item data arrives in
  server snapshots (`0x737A6DF9`). The encrypted RDB templates are decrypted **only for the crafting/recipe UI**,
  using keys the server streams for the specific recipe. This is item-template DRM: the client ships the
  encrypted DB, the server hands out per-recipe keys on demand.

### FULLY CRACKED + validated (2026-06-08)
The keys were never missing — **they're the per-item `ProtoDecryptionKey` the server sends in inventory, which we
already capture and store as the item `Data1..Data4` quad** (mislabeled "item identity"). From the client's own
protobuf (`Faolan.Core/Protobuf/aoc-client-protos.proto`):
```
ProtoDecryptionKey { fixed32 key1; key2; key3; key4 }          // 4 LE u32 = the 128-bit TEA key
ProtoAcgItem { ProtoDecryptionKey key1,key2,key3; fixed32 template1,template2,template3; uint32 level; fixed32 hash }
ProtoInventoryGameEntry { ... ProtoAcgItem item_data = 3; ... } // inventory entries embed the keys
```
This maps onto our `SnapshotItemBuilder` tail: the three `{f1..f4 fixed32}` "identity payloads" (= `Data1..Data4`)
are `key1/key2/key3`; `tail.f4/f5` (= `Item.Id`) are `template1/template2`; `tail.f8` (we called "archetype") is
`hash`.

**Decryption recipe (verified):** RDBH blob → ciphertext = `entrySize-20` → **TEA in ECB** (not CBC; the earlier
"not ECB" assumed a shared key, but **each item has its own key** so blocks never collide) with key =
`(Data1,Data2,Data3,Data4)` as 4 LE u32 → truncate to `plainLen` → `IOz1` + `uncompressedSize` + zlib → inflate →
item template (`12B RDB-Id hdr: type,id,marker=1000000` + binary fields).

**Proof:** the item Daggamalt `type 1000020 id 180450`, key from `faolan.db Items.Key1..4` =
`807024719, 3628954689, 2634994156, 3227556839`, decrypts to a valid 329-byte template with header
`type=1000020 id=180450 marker=1000000`. (Script: `/tmp/tea_decrypt.py`.)

**So:** every captured item (anything with `Data1..4` in `faolan.db`) can now have its **full canonical RDB
template** extracted — coverage = items we hold keys for (= captured items), but yielding the complete template
vs the partial snapshot.

### Implemented in Faolan.Importer (2026-06-08)
- `Tea.cs` — TEA (32-round, ECB) cipher.
- `RdbReader.ReadDecrypted(entry, teaKey)` — detects `RDBH`, TEA-ECB-decrypts, truncates to `plainLen`, unwraps
  the inner `IOz1`/zlib.
- `RdbItemImporter` + `--rdb-items` (with optional `--dump-templates <dir>`): for every DB item with a key
  (`Key1..4`), looks up RDB `1000020`/`1000620` id=Item.Id, decrypts, validates the RDB-Id header.
  **Result on our DB: 387/388 keyed items decrypt + validate** (the 1 "miss" is a DB id that collides with a
  *plain* TESTITEM entry, correctly rejected). 
- **Template string fields** are `tag 0x21 (u32) + len (u16) + flags (u16) + ASCII`. The length is a **u16**, not a
  u32 — the next u16 is a flags word that is **nonzero whenever the string is backslash-escaped** (e.g. an
  apostrophe `\'`). Reading the length as a u32 folds that flags word into the high bytes (e.g. `0x0018000f` ≈ 1.5M),
  which a sane length guard then rejects — that was the #202 bug: names with `flags == 0` worked, escaped names were
  dropped. Read len as u16, skip the flags word, unescape `\'`/`\"`/`\\`. The first underscore-free such string is the
  display name (internal mesh/asset names contain `_`). The importer fills `Item.Name` only when empty (captured names
  stay authoritative). The earlier "localized-string id" theory for the blanks was **wrong**: those names are inline,
  just mis-framed — fixing the length read recovered **225 more** ("Aspirant's Cap", "The Conqueror's Crown", …; commit
  `06fda38`). The genuinely-blank remainder (~839) are NPC/TEST/internal items whose only string is an asset-mesh ref —
  they have no player-facing name. Guarded by `RdbItemNameTests`.
### Template body format (characterized 2026-06-08)
The decrypted template body (after the 12-byte RDB-Id header) is a **fixed-layout struct**, NOT a generic TLV.
Evidence: aligning two unrelated items by their constant values, the constants land at **identical byte offsets**
(`@20=76, @28=30, @44=298, @52=88, @60=499, @68=496, @76=360` in both a simple key and a weapon) — i.e. fixed
stat *slots* whose ids are the same for all items, with the per-item *values* in interleaved fixed fields. There
is also a **packed field** (e.g. `@40 = 0x0X000001`, X differs per item) that is not a plain u32, which is why a
naive `[id u32][value u32]` walk only consumes the body exactly for 160/387 (and mis-fires when a value == 33,
the string marker). **Strings** are `0x21 (u32) + len (u16) + flags (u16) + ASCII`, unaligned (no realign after),
holding the name (done) and usually a description. (The `len` is a u16 + a u16 escaping-flags word — see the #202
note above; do not read it as a u32.)
- **Conclusion:** correct stat extraction needs the struct's **field map** (offsets → meaning). RE pass 2 traced
  the parser chain (anchors below), which revealed *why* this is non-trivial:
  - Factory: `0x10603b0(creatorFn, type)` registers a creator per RDB type; type `1000020`/`1000620`/`1040005`/
    `1000621` all use creator **`0x4e29f0`** → worker **`0x4e8600`** which allocs a 0x90-byte object, sets vtable
    **`0x1fdd11c`**, class RTTI = **`RDBItemData_c`**.
  - `RDBItemData_c` is a **lazy buffer wrapper**: vtable slot 17 (`0x8d68f0`) stores the source buffer at
    `[obj+0x88]` and reads only the **12-byte RDB-Id header** (`call [stream_vt+0x18]`, len `0xc`); the field
    getters (slots 7-16, `0x4ec7xx`) are **stubs** (`ret`/`return 1`/`return [ecx+0x78]`). So there is **no single
    deserialize** that reads the stat fields - consumers read them on demand from the stream (read-u32 primitive
    `0x48f900`; the base header reader `0x1060370` calls it ×3). Mapping every field therefore means tracing the
    distributed consumer reads - a sizeable effort, not a quick pin.
  - **Resume anchors:** `RDBItemData_c` vtable `0x1fdd11c`; buffer at obj `+0x88`; stream read-u32 `0x48f900`;
    the name field is the `0x21`-tagged string in the stream; the fixed stat-slot offsets (@20=76, @60=499, …) and
    packed `@40` are the on-disk landmarks. Cross-validate any field map against the `--dump-templates` blobs.
  - Alternative: **(b) dynamic dump** - run the client (Wine) with our inventory and hook `RDBItemData_c`'s reads
    to log offset→value; authoritative but needs a full client-run + debugger setup (also not quick).
- **Deliberately NOT shipped** as a parser — a fixed-struct guess without the field map would write wrong stats to
  the DB. The decrypt + `--dump-templates` infra preserves every template for that RE whenever it's picked up.
- **Done into `Item`:** `Name` (143 filled). **Remaining:** level/stats once the struct field map is reversed.

### Typed protobuf decoding (Faolan.Core/Protobuf/aoc-client-protos.proto → C#)
The recovered schema is generated to **`Faolan.Core/Protobuf/AocProtocol.g.cs`** (protobuf-net, the
project's existing protobuf runtime). Test `AocProtocolDecodeTests` decodes a **real captured** inventory entry
(Slave Manacles) with the generated `ProtoInventoryGame.InventoryListEntry` and asserts the decoded
`ProtoAcgItem.key1` (a `ProtoDecryptionKey`) == the item's `Data1..4` and `template1` == the item id — proving the
schema matches the wire and that the "identity quad" is the TEA key. This makes the whole IIR/inventory/character/
looks/quest protocol available as typed C# classes for the server/analyzer (richer than the hand-rolled
`ProtoMessage` byte-walking: `stat_list`, `creator_name`, gem slots, `NpcStats`, etc.).

**Proven end-to-end:** TextStrings entry (type 1030002, id 211656, file 8) → read 10402 B → `IOz1`/uncomp=28704
→ zlib-inflate to exactly 28704 B → `TDC2` string collection → real strings extracted: *Flags, Health, Volume
Mass, Attack Speed, Clan, Team*.

**What's available (entry counts in our `le.idx`):** TextStrings `1030002`=**1,103**, MONSTERDATA `1010201`=
**3,185** (model/skeleton/mesh defs — NOT combat stats; see the MONSTERDATA note below), TextScript
`1010035`=819, Playfield `1070003`=520, Treasure `1000083`=1,018, **item-template `1000020`=98,048 (RDBH-
encrypted — see the encryption section)**, Filenames `1000010`=1 (the id→filename table). TextStrings = the
dialogue/UI text (the `TDC2` format is documented); `1030002` category + the `/data/text/*.tdbl` language lists
map strings to languages.

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
Item-template type = **1000020** (98,048 entries; captured Daggamalt 180450 = record index 31805). Decisive
cross-check against ground truth: of **191** DB buildable items, **190** are present in RDB(1000020), and **all
190 are `RDBH`-wrapped** (0 plain), high-entropy (~7.0), no standard decompression (zlib/IOz1/raw) yields
anything; **0 of the 190 contain the known item `Data1` quad value, and 0 contain the `Archetype` value.** The
plain ~28% of 1000020 resources are test items ("TESTITEM Spell uploader"), not shippable identity. Conclusion:
the buildable identity (archetype + `Data1..4` quad) is **absent / intentionally `RDBH`-encrypted** in the RDB
and **cannot be extracted** without breaking the `RDBH` cipher (a separate major effort). **Buildable items must
still come from captures** (ArchetypeHarvester on `0x737A6DF9` snapshots) — the RDB does not shortcut this.

**MONSTERDATA checked 2026-06-08 — it is NOT combat stats, and NOT encrypted.**
1010201 (3,185 entries) decodes to **plain XML** (12-byte RDB-Id header `type,id,1000002`, then `<?xml …>`;
no `IOz1`/`RDBH`). But it's the monster **model/render definition** — `<monsterdata animsys= skeleton= name=
movementset= …>` with `<mesh>`, `<previewset>`, `<soundscript>`, `<fxscript>`. **No HP / level / armor /
damage.** A sweep of every XML resource type found **no `<creature>`/`<npc>` stat table**: the gameplay-adjacent
plain-XML types are `1010206 <combo>` (player ability chains — `level`/`stamina` attrs, not creature HP),
`1010219 <fatalitygroup>`, `1070003 <playfielddescriptiondata>`, `1000083 <treasuredata>` (empty in this
client), text `1010035`/`1030002`, `1010030 <MeshIndexFile>`, `1010214 <effectset>`. **Conclusion: NPC combat
stats (HP) are not in the client RDB** — they were server-authoritative in retail (the `Archive/` C++ `p_*`
tables). Combat HP must come from **captures or the Archive C++ server data**, not the RDB. Of the gameplay
data only items (`1000020`) are `RDBH`-encrypted; everything else is plain XML and extractable if ever needed.
(Index note: detail entries with file byte `0xff`/255 are absent/empty sentinels — skip them.)

**Next (structured data → DB):**
1. ~~Item-template identity~~ — **blocked** (`RDBH`-encrypted, identity absent; from captures only).
2. ~~MONSTERDATA → NPC HP~~ — **not applicable** (1010201 is the model def, not stats; HP isn't in the RDB).
   NPC combat stats come from captures / the `Archive/` C++ server data instead.
3. (optional, if ever needed) the plain-XML gameplay types above are trivially extractable via `RdbReader`
   (combos, fatalities, playfield/treasure, text). None are required for the current create→login→play work.

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
  (`docs/subsystems/player-stats.md`), and the conversation nodes — use these to validate any RDB parser.
