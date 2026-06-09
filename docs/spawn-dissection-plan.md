# Plan: dissect the verbatim world packets → load players & NPCs from the DB

## Status (updated 2026-06-07)

- **Phase 0 DONE** — workbench (`dissect_spawn.py`), `SpawnPacketBuilder`, `SpawnHandleTable`, tests 5/5 pass.
- **Phase 1 DONE** — player spawn **position** rebuilt from DB in the Strie replay; confirmed in-client (loads +
  spawns at the edited DB position). Rotation kept verbatim (DB rotation unreliable + stalled loading).
- **Phase 2 DONE** — any beach character (`Character.MapId == 0xFAA`) uses the replay; the capture's instance is
  re-addressed to the live one (`RewriteBurstInstance`); SpawnCheck gated on `Session.UsedWorldBurstReplay`.
  Structurally verified (no-op/byte-identical for Strie); needs in-client confirm for a non-Strie id.
- **Name field dissected** — `0A <len> <utf8>`; length-change needs a nested-length rebuild (see below). Not done.
- **Three hard-won gotchas**: inject burst packets via the raw `client.SendAsync` path + manual CRC (NOT
  `PacketStream.SendAsync`, which reshapes TCP segmentation and stalls the client); don't rebuild the rotation
  yet; and the player handle == the client instance (the client never polls its own handle).

- **Phase 2 confirmed in-client** (id 0x02000005): receivers `85808010`, 50 polls + 641 in-world uploads.
- **NPC dialogue generalized** — confirmed in-client (opens for a non-Strie char; advancement is coverage-bound).
- **Per-character name confirmed in-client** — renamed char spawns with its own (different-length) name and loads.

So a non-Strie beach character now: renders the world+NPCs, spawns at its DB position with its DB name, and can
open NPC dialogue. Remaining for a fully DB-driven player: **appearance** (race/sex/class/morphs/equipment),
which is the bulk of the FA015807 body and the hardest dissection.

- **Transform now CONSTRUCTED** (2026-06): `SpawnPacketBuilder.BuildSpawnBody` builds the header + transform
  (position/rotation = typed `ProtoVec3`/`ProtoQuat`, i.e. `ProtoGameCodeDynelIIR`) instead of offset-patching a
  clone, then appends the undissected tail via `WriteArray`. Byte-identical round-trip kept (`SpawnPacketBuilderTests`).
- **Phase 3 landmark found — CharacterStats** (2026-06): the identity/stat component (sex/side/race/class/morph
  scale/life/health/...) is a `0x22` length-delimited field **immediately after the name**, carrying the generated
  `ProtoCoreCharacterIIR.CharacterStats`. Strie template: `0x22` @315, 67-byte payload (`SpawnPacketBuilder.
  CharacterStatsTagOffset`). Fields: 1=sex 2=side 3=race 4=monster_scale 5=monster_data 6=life 7=health ...
  Decoded across all player captures (sex/race vary: Strie 3/1, hoxxling/necrol80/demol80 2/3). The generated type
  **re-encodes byte-identical** (`CharacterStatsRoundTripTests`), so it can be built from a typed object — the
  build-side oracle for sourcing it from the DB is in place.

- **CharacterStats splice BUILT** (2026-06): `SpawnPacketBuilder.SetCharacterStats(body, stats)` rebuilds the
  component from a typed `CharacterStats`, length-adjusting @0/@133/@304 like `SetSpawnName`
  (`CharacterStatsRoundTripTests`: captured-stats splice is a byte-identical no-op; a growing override adjusts all
  three prefixes). NOT wired into the live replay yet — see the field-mapping blocker below.
- **Field-mapping status** (for sourcing from DB): `race` (field 3) maps cleanly to the `Race` enum
  (Aquilonian=1 / Cimmerian=2 / Stygian=3 == decoded values). `sex` (field 1) does NOT: the wire carries 2/3 while
  the `Sex` enum is Male=0/Female=1 (looks like an offset/encoding — UNCONFIRMED, needs a known-sex capture).
  `life`/`health` scale vs `CharacterMaxStats` also unconfirmed.

Next up:
1. **Phase 3 — wire CharacterStats from DB**: override `race` (clean) + the vitals once their scale is confirmed,
   via `SetCharacterStats` in `TryRebuildPlayerSpawnFromDb` (apply BEFORE `SetSpawnName`). Hold `sex` until the
   2/3-vs-0/1 mapping is pinned. CAUTION: identity fields must stay consistent with the appearance meshes
   (LooksPacket) or the client may render/​crash wrong — so the safe live cut-over may wait until looks are mapped.
   Then the LooksPacket (meshes/morphs/tints) — the bulk.
2. **Phase 4 — NPC spawns from `Npcs`/`NpcBodies`** (the builder already generalizes to NPC FA015807 bodies).
3. Conversation content import (full dialogue trees) — separate, larger.

## Goal

Replace the verbatim Strie-login replay (`RetailLoginReplay.cs` + `strie-login-worldburst.bin`) with
**dynamically constructed** world-entry packets sourced from the database, so that:

1. **Any** character logs in and renders with **its own** position, orientation, appearance (race / sex /
   class / morphs), name, equipment and stats — not Strie's captured bytes.
2. **NPCs** spawn from the `Npcs` / `NpcBodies` / `NpcEquipment` tables the same way.
3. Eventually: full inventory (`Items` / `CharacterEquipment`), stats, spells, quest state.

The verbatim replay stays as the reference oracle: for any field group we make dynamic, we rebuild it from
the DB values that match a captured character and assert the bytes are identical to the capture. Nothing
ships dynamic until it is byte-equal to retail for the known case.

## Packets to dissect (the `0x2000` world build, in priority order)

| sub-opcode  | what it is                          | source table(s)                         | priority |
|-------------|-------------------------------------|-----------------------------------------|----------|
| `FA015807`  | entity **Spawn** (player + NPCs)    | `Characters`,`CharacterBodies`,`-Equipment`,`Npcs`,`NpcBodies`,`NpcEquipment` | 1 |
| `96C46740`  | per-entity registration (paired w/ Spawn) | same as Spawn                     | 2 |
| `0x2008`/`0x2020`/`0x2076` | spawn-poll handshake + manifest | session handle table             | 2 |
| `737A6DF9`  | inventory dump                      | `Items`, `CharacterEquipment`           | 4 |
| `66AEDD50` `F096251E` `33A56FB0` | static world objects / geometry | `Maps`, `MapObjects`         | 5 |
| `5D85BFC7`  | MapChange                           | `Maps`, `Characters.MapId`              | 1 (already hand-built) |
| `0x2024`    | ReadyForPlayScreen                  | `Characters` position (BE floats)       | 3 |
| `0x2052`    | per-entity spawn data (pos/quats)   | spawn model                             | 3 |

## Method: differential analysis against multiple captures

We cannot generate new captures (no client on the dev side), but we have several characters with **known,
differing** attributes: `Strie`, `dikketest`, `strickenbla`, `demol80`, `faolanx` (+ the create captures).
Aligning the *same* packet type across two characters isolates each field:

- bytes that **differ** between two spawns ↔ the attributes that differ (name text/length, position floats,
  race/sex/class ids, equipment item ids, appearance morph ids, the entity handle).
- bytes that are **constant** across all spawns ↔ fixed scaffolding (tags, type markers, defaults).

`FA015807` is already partly tag-structured (protobuf-ish): e.g. position is
`2C 0A 0F 0D <x:f32> 15 <y:f32> 1D <z:f32>` and rotation `0D <rx> 15 <ry> 1D <rz> 25 <rw>` — field numbers
1–4, wire types fixed32. So most of it can be parsed as nested length-delimited + fixed32/varint fields with
the existing `ProtoMessage` parser; the rest is differential.

## Tooling to build (Phase 0) — DONE

Implemented: `Captures/dissect_spawn.py` (workbench: decodes handle/position/rotation/name per spawn, plus a
`diff` mode); `Faolan/GameServer/Spawn/SpawnPacketBuilder.cs` (static, template-based, overwrites the mapped
fixed-offset fields handle@12 BE + position@24/29/34 + rotation@41/46/51/56 LE; `spawn-player-template.bin`
embedded); `Faolan/GameServer/Spawn/SpawnHandleTable.cs` (per-session unique/stable handle allocator, base
0x00090000); tests in `Faolan.PacketAnalyzer.Tests/` (`SpawnPacketBuilderTests` byte-exact rebuild +
`SpawnHandleTableTests`, 5/5 pass). Phase 0 complete.

The handle allocator is built but NOT yet wired - it activates in Phase 2 when spawns are built with our own
handles instead of replayed with the capture's. Until then the verbatim handles are used.

Original outline:



1. **`Captures/dissect_spawn.py`** — for a given sub-opcode (default `FA015807`), extract every matching
   `0x2000` body from one or more capture JSONs, parse into a tag/length/value field tree (reuse the
   `ProtoMessage`-style walk), and print them **side-by-side** with a per-field "constant / varies" flag and
   the known character attribute next to each varying field. This is the dissection workbench.
   - Seed the known attributes per capture from `Characters` rows (Strie) + README reference values.
2. **`SpawnPacketBuilder`** (C#, `Faolan/GameServer/Spawn/`) — builds an `FA015807` body from a typed
   `SpawnModel { uint Handle; Vector3 Position; Quaternion Rotation; uint Race, Sex, Class; string Name;
   AppearanceMorphs; Equipment; Stats; ... }`. Start as a faithful re-expression of the captured Strie player
   spawn (so it round-trips byte-exact), then replace literal regions with model fields one at a time.
3. **Round-trip test** (`Faolan.PacketAnalyzer.Tests` or a small xunit) — `BuildSpawn(strieModel)` must equal
   the captured Strie `FA015807` bytes. Add a case per field group as it goes dynamic; this is the gate.
4. **Session handle allocator** — `SpawnHandleTable` mapping each spawned entity (the player + each NPC) to a
   server handle (retail used `0x000818xx`/`0x00081906`-style ids). Used consistently by Spawn, `96C46740`,
   the `0x2020`→`0x2008` SpawnCheck (drop the hardcoded `StriePlayerSpawnHandle`), and the `0x2076` manifest.

## Phases (each independently testable: byte-diff vs capture, then in-client)

**Phase 1 — Player self-spawn from DB.** DONE (position only).
`RetailLoginReplay.TryRebuildPlayerSpawnFromDb` detects the player's own `FA015807` in the world burst (the
one whose handle == client instance) and rebuilds its **position** from the DB `Character` via
`SpawnPacketBuilder.BuildSpawnBody`; everything else stays verbatim. For Strie the DB position equals the
capture, so output is byte-identical (no regression); editing the DB position moves the spawn.

GOTCHA 1 - send path: the rebuilt spawn MUST go out the same raw `client.SendAsync` path as the rest of the
burst. Sending it via `PacketStream.SendAsync` instead stalled the client on the loading screen even though the
bytes were byte-identical - most likely PacketStream's synchronous CRC work changed the TCP segmentation of the
tightly-batched burst and the client (no reassembly on the uncompressed download) misframed it. So we splice
the new body in, recompute the CRC in-place (`Crc32.Calculate(rebuilt[8..])`), and send raw. Important for the
whole plan: built packets injected into the burst should use the raw path + manual CRC, not PacketStream.

GOTCHA 2 - rotation: rebuilding the **rotation** from the DB also stalled loading (and existing characters have
unreliable rotation data), so rotation is kept verbatim from the template for now - revisit once DB rotation is
trustworthy. Still literal too: the embedded **name** (variable length - needs the length-aware rebuild) and
appearance/equipment.

**Phase 2 — Any character (instance generalization).** DONE (rendering; needs in-client confirm).
The login gate changed from `clientInstance == 0x02165EFF` to `Character.MapId == 0xFAA`, so ANY beach
character uses the world-burst replay. `RetailLoginReplay.RewriteBurstInstance` re-addresses the capture's
instance to the live one in every burst packet: the receiver-conversation varint + inventory nested varints
(`varint(0x02165EFF)=FF BD D9 10`) and the raw entity-handle/body uint32s (`02 16 5E FF`), both 4 bytes for
in-range ids so length-preserving, then recompute CRC and send raw. Verified structurally: 75 varint + 7 raw
substitutions, all 71 receivers + the player handle updated, lengths preserved; for Strie (new==old) it is a
no-op byte-identical (no regression). The SpawnCheck responder is now gated on the new
`Session.UsedWorldBurstReplay` flag (set in the replay) instead of the Strie instance; its status-0x02 handle
(`0x00081906`, the Casilda/intro NPC) is unchanged across characters, and the client never polls its own
handle, so no per-instance logic is needed.

NPC dialogue is now also generalized: `ConversationReplay.TryReplayConversation` substitutes the instance in
BOTH forms (raw uint32 in bodies + the varint in the receiver conversation ids, via `ReplaceAll`) and the
clicked NPC handle, and is gated on `Session.UsedWorldBurstReplay` instead of the Strie instance. No-op
byte-identical for Strie. Confirmed in-client: dialogue opens for a non-Strie character.

COVERAGE LIMIT (capture-replay): `extract_conversations.py` now MERGES all captures (dedup by
(TypeId,Mode,Field3), prefer a 4-byte-varint instance + most packets) -> 12 nodes for the beach NPC type. But
we can only replay branches that appear in the pcaps: e.g. the option `(42008, mode=2, 0)` is in no capture, so
clicking it does nothing (graceful). FULL dialogue-tree advancement (any option, every NPC) cannot come from
packets - it needs the game's conversation CONTENT (client drives options by category/key; server returns the
node from a content DB). That is a separate content-import phase, out of scope for replay.

Still the capture's appearance/name for the player (Phase 3 / name-rebuild).

**Name field — DONE (needs in-client confirm).** The entity name is a length-prefixed string `0A <len> <utf8>`
at body offset 308 in the Strie player template. The enclosing lengths were nailed down (the earlier `@296`
candidate was coincidental): name → `@304` (4-byte BE, 469) → `@133` (4-byte BE, 802) → body length `@0` →
packet length. Proven by self-consistency: within `@133`'s `[137,939)`, `167 + (4 + @304-block 469) + 162 =
802`. `SpawnPacketBuilder.SetSpawnName` splices a new name and adds the delta to `@304`/`@133`/body-length;
`RetailLoginReplay.TryRebuildPlayerSpawnFromDb` now applies it (after position) and reassembles the
variable-length packet (header + new body, set packet length, recompute CRC). Round-trip tests cover
same-length / unchanged / grow / shrink (9/9 spawn tests pass). 1-byte name length assumed (names < 128).

**Appearance dissection (2026-06-07, partial).** The attribute/appearance message is `field4` of the post-name
protobuf message (the message that starts with `field1`=name at body offset 308). Inside `field4`:
- `field1` = **Race** (enum-confirmed: Cimmerian=2, Stygian=3 -> Strie=3 Stygian, Faolanx/DemoL80=2 Cimmerian).
- `field2` = likely **Sex** (0=Male for all three test chars; unconfirmed - need a female capture).
- `field3` (1/3), `field4` (92/103) = other appearance params (face/body presets, TBD).
- `field6/7/8/9` = large values (9000/23000/...) = **morphs** (face/body customization).
- **Class is NOT in the spawn** (no 0x12-0x2C value anywhere) - it's char-list metadata, not spawn-visual.

MORPH PERSISTENCE — DONE (needs in-client test via the create flow). The create-editor handlers
(`HandleCommandPacket`) now populate `CharacterBodies`: `EnsureBody` lazily creates+tracks the body row;
`ChangeHeadMesh/Size/Voice` write to it (were no-ops); `SetMorphValue` body sliders (arm/chest/bosom/stomach/
arse/thigh/leg, value 0-100) map to the matching `CharacterBody` float fields; resource refs (`*hairmesh/
*texscript/*beardmesh`) and face sliders not yet seen are logged+skipped. Saved with the character at
name-commit. STILL TODO: dissect how these values encode into the spawn's `field4` morph fields (e.g. SetMorph
arm=59 vs spawn field6=9000) before appearance can actually render from the DB.

Earlier blocker (now addressed): morphs (`field6+`) need data from `CharacterBodies`, which WAS empty.

SPAWN MORPH ENCODING — BLOCKED (insufficient correlation data). Tried to map editor sliders -> spawn `field4`
morphs using the create captures. dikketest is the ONLY retail char with both a known slider set
(arm=59/arse=14/bosom=30/chest=59/leg=59/stomach=14/thigh=59) AND a captured world-spawn; strickenen's slider
set is known but its spawn was dropped by the compression-cut. The dikketest sliders do NOT appear in its spawn
as raw bytes (59 appears only 2x, 30 never), nor as floats (0.59/59.0/etc. - no match). field4 varint subfields
(field6=11000,7=10030,8=17200,9=1145) don't fingerprint the slider pattern, and diffing against same-race Strie
is confounded by face/individual differences. Cracking the encoding needs chars differing ONLY in body morphs,
or per-slider editor-preview spawns - neither is in the capture set, and we can't generate retail captures. So
full morph-accurate appearance is parked. Achievable appearance piece remains RACE (field1, enum-confirmed) +
likely Sex (field2) - but those need the Change{Race,Sex,Class} capture fixed first (editor sends 6/6/62, not
the enum values). Race/Sex ARE in
the `Characters` table and could be driven now (1-byte varints in `field4`, length-safe), but yield only a
race-correct body with the template's (Strie's) morphs - partial and visually mismatched. Field offsets shift
with the name length, so any appearance write must happen relative to the (possibly rebuilt) name.

**Phase 3 — Appearance & orientation fidelity.**
Dissect the morph/appearance region of `FA015807` and source it from `CharacterBodies` (populated from the
create-editor `SetMorphValue` stream — currently empty). Fix `0x2024` ReadyForPlayScreen to write **big-endian**
floats and the two distinct positions (`Packets.cs:516`). Add `0x2052` per-entity data from the model.

**Phase 4 — NPC spawns from DB.**
Reuse `SpawnPacketBuilder` for NPCs sourced from `Npcs` / `NpcBodies` / `NpcEquipment`, keyed by map. Replace
`SpawnTortageBeach`'s verbatim dikketest blobs with built spawns; allocate NPC handles from the table; add to
the `0x2076` manifest. Verify a beach NPC built from a seeded `Npcs` row matches its captured `FA015807`.

**Phase 5 — Inventory & equipment.** (IN PROGRESS — significant existing work.)
The `0x737A6DF9` giant inventory snapshot is already parsed: `Faolan/GameServer/Packets/0x737A6DF9_Test/`
(`InventorySnapshotInspector` = container/item/stats model; `BigboyTailsFile` = the stats/quest tails;
`Send0x2000_0x737A6DF9.cs` loads a captured "bigboy", parses, mutates, rebuilds). Structure: body =
prefix + protobuf root (containers) + tails. Containers are field6 messages keyed by kind: Equipped=101,
Bag=104, Resource=112, Quest=113; each has f1{f1=kind,f2=owner}, f2=capacity (NOT occupied count), f3=flags,
f4=repeated item entries. Item entry: f1=slot, f2=details {f1=itemId, f2=repeated props (23=record id, 26=stack
qty, 0=flags), f3=template tail with f4/f5=fixed32 template id, f8=fixed32 archetype}. Stats live in the tails.

Findings / constraints:
- Mutating an existing item's **stack count** (prop 26) and **stat values** (tails) is SAFE and works in-client.
- **Adding/removing an item entry STALLS the client on loading** (open problem; cause unconfirmed - possibly a
  count/occupancy field not yet found, or client-side state). This is the key blocker for arbitrary inventories.
- DB side is a STUB: `Items` table is just (Id, Name), `CharacterEquipment` empty, `Items` empty. There is no
  item-template schema (template id / archetype / stats), so DB-driven inventory needs that built first.
- Beach login currently sends the captured inventory verbatim from the world burst (instance-substituted), so
  every character gets Strie's items.

ADD/REMOVE STALL INVESTIGATION (2026-06-07): ruled out the packet-side causes.
- The prefix varint (`00000002 a9288106`) tracks the TAILS, not inventory: two Strie inventories with identical
  entry counts (5) differ in that varint by exactly the 5-byte tails delta. So it's not an item-count desync.
- The bag item's itemId / record-id (prop 23) do NOT appear in the tails - no dangling reference on removal.
- Parse -> re-serialize (Prefix + Root.ToArray() + Tails.ToArray()) is BYTE-IDENTICAL (round-trip test
  `InventorySnapshotTests.ParseThenReserialize_IsByteIdentical` passes). So the rebuild machinery is lossless;
  add/remove produces a structurally valid packet.
CONCLUSION: the stall is semantic / client-side (the client cross-references the inventory item set with other
state, or its own cache), NOT a packet bug. The safe capability is solid: lossless parse/modify/rebuild +
confirmed-OK stack-count and stat edits.

ARCHIVE BREAKTHROUGH (2026-06-07): the old C++ server (`Archive/`, extracted to `/tmp/faolan-archive/trunk`)
explains the add/remove stall and reframes the whole inventory approach. **Snapshot-injection is a dead end.**
- Items are NOT patched into a captured snapshot. The retail server adds an item via a **dedicated packet**
  (`WorldServer/WorldServerOpcodes.h` op `0x1d`); `WorldServer/CommandHandler.cpp::addItem` builds it as a
  48-byte `item_pack` header followed by `id, id, iLevel, unk(0x4f4f4939), data1, data2, data3, data4` and then
  the `data1..4` quad **repeated** (this duplicated quad is exactly the 20-byte tail "identity payload" we saw
  in the snapshot), ending `0x3f1, 0`.
- `data1..data4` come from the **`p_items` table** (`trunk/sql/p_items2.sql`: `p_items(name, id, level,
  data0..data4)`) — they are the item's stable, server-known identity. A fabricated/duplicated item with an
  invented identity is rejected by the client (the stall), which is why every add experiment hung even when the
  packet was byte-valid: the identity quad must be a REAL `p_items` row.
- The inventory is therefore built from `player_items` (the per-character equipped/bag rows; schema in
  `trunk/sql/aocdatabase.sql`) joined to `p_items` (the template identity), NOT reconstructed by editing a
  capture. `player_items` slot ids are equipment slots: neck=0x01, head=0x02, tokens=0x03, chest=0x05,
  main_hand_left=0x06, belt=0x07, off_hand_left=0x08, ring_left=0x09, ring_right=0x0a, ... (`npc_items` mirrors
  it for NPCs).

Reframed plan: (a) import `p_items` (item identity/template) into the C# DB + a `player_items`/`CharacterItems`
slot table; (b) build the `737A6DF9` snapshot from `player_items` ⋈ `p_items` (identity quads from real rows);
(c) implement the op-`0x1d` add path for live pickups. Meanwhile the safe shipped path stays: send the
instance-rewritten captured inventory verbatim, and drive counts/stats onto it.

ARCHIVE IMPORT — DONE (2026-06-07). Step (a) is implemented and verified. `Faolan/Helpers/ArchiveDataImporter.cs`
is an idempotent startup seeder (run from `DatabaseMigrator` after `MigrateAsync`) that parses the embedded
archive SQL (`Faolan/GameServer/Data/archive/p_items.sql` + `aocdatabase.sql`) and fills the DB when empty:
- `Item` gained the template identity columns `Level, Data0..Data4` (migration `ImportArchiveItemTemplate`);
  **219 item templates** imported from `p_items` (the Data1..Data4 identity quad the inventory/add packets need).
- **21 spells** from `spelldata` -> `Spells`.
- **4 Tortage-beach NPCs** (incl. Kalanthes 433376) from `npcs`/`npc_details`/`npc_items` ->
  `Npcs`/`NpcBodies`/`NpcEquipment`, equipment FKs resolved against the imported item templates.
Verified in `Faolan.PacketAnalyzer.Tests/ArchiveDataImporterTests.cs` (2 tests: full import + idempotency) and
against the live `faolan.db`. NOT imported (deliberately): reference tables already modelled as C# enums
(races/classes/archetypes/sex), the speculative stat/ability tables (`const_stats`/`const_abilitys`/`p_abilities`
- no consumer yet), `maps` (already seeded, superset), and the test account/character rows.

ENTRY-FROM-DB — PROVEN BYTE-EXACT (2026-06-07). The exact mapping from a `p_items`/`Items` row to a snapshot
container entry is nailed down and tested. Dissecting the captured Strie inventory, the **"Slave Manacles"**
entry (Equipped slot 18) matches the imported `Items` row 3361557 perfectly:
```
entry.f1                      = slot
entry.f2(details).f1          = itemId            (per-instance GUID, not from template)
details.f2 {f1=0,  f2=v}      = flags property
details.f2 {f1=23, f2=v}      = inventory record id (per-instance)
details.f2 {f1=26, f2=v}      = stack count
details.f3(tail).f1/f2/f3     = 3x identity payload {f1..f4 fixed32 = Item.Data1..Data4}   <- from p_items!
tail.f4 = tail.f5             = Item.Id           (template id)                            <- from p_items!
tail.f7                       = 1
tail.f8                       = archetype          (the ONE field NOT in p_items)
```
`SnapshotItemBuilder.BuildEntry(slot, itemId, recordId, count, Item, archetype)` constructs this; the test
`SnapshotItemBuilderTests` imports `p_items` into a temp DB, builds the entry from row 3361557 + the captured
per-instance state, and asserts it equals the captured entry **byte-for-byte**. So: p_items SQL -> DB -> builder
-> on-wire bytes is confirmed lossless. (The other 3 captured items aren't in the 219-row partial p_items, so
only Slave Manacles could be cross-checked - but it's conclusive.)

ITEM COVERAGE — FULL IDENTITY HARVESTED FROM CAPTURES (2026-06-07). Archetype (`tail.f8`) is per-template, NOT in
`p_items`, and looks like a hash (Slave Manacles = 1129664565 = 0x43555035; no derivation from the quad). It -
and the `Data1..4` quad - are harvested from real captured inventories, so any item a player has held is fully
buildable even without a `p_items` row:
- `ArchetypeHarvester` parses any 0x737A6DF9 packet and extracts every `templateId -> (Data1..4, archetype)`.
- `ArchetypeHarvestRegenerator` (a test, re-runnable) scans all captures (`.bin` raw + `.json` RawBytesBase64),
  merges, and writes the embedded `Faolan/GameServer/Data/archive/captured-items.tsv` (**177 items, 0 conflicts**).
- `ArchiveDataImporter` merges this into `Items`: it sets `Archetype` on matching `p_items` rows and INSERTS
  capture-only items (quad + archetype, no name/level). **Archetype now lives on `Item`** (new column; the old
  `ItemArchetypes`/`archetypes.tsv` are gone). An item is **buildable iff `Item.Archetype != 0`**.
COVERAGE: **~384 items total, 177 buildable** (every item seen in a capture), up from 8. Verified end-to-end in
`SnapshotItemBuilderTests`: building Slave Manacles from the DB Item alone reproduces the captured entry
**byte-for-byte**. Wider coverage now just needs more captures (re-run the regenerator).

STEP (b) CONFIRMED IN-CLIENT (2026-06-07). ✅ A beach character logs in and the DB-built **Royal Signet**
renders perfectly: correct name, slot type ("Ring - Left/Right Finger"), item level, full stats (+30
Constitution / +52 Magic Damage / +15 Hit Rating), vendor price and bind-on-equip - ALL resolved client-side
from the template id + identity quad we built from `p_items`. The client loads normally (no stall). This proves:
(1) DB-built item entries are accepted in-client; (2) the long-standing add/remove stall was ENTIRELY the
fabricated identity - real p_items-backed items work; (3) the whole item (stats/tooltip/slot) is driven by the
template id + `Data1..4` quad, so a fuller `p_items` is all that's needed for richer items. Snapshot-injection is
no longer a dead end when the identity is real. (Cosmetic: the swap targets the first bag ENTRY, which sits at
slot index 1 -> shows in the 2nd visual cell; fine.)

STEP (b) FIRST INCREMENT — DB ITEM INJECTED INTO LIVE INVENTORY (2026-06-07). Rather
than rebuild the whole snapshot from scratch (the tails are still unreconstructed), the beach-login replay now
REPLACES the bag's first entry IN PLACE with a DB-built entry, keeping the original slot/itemId/recordId so the
entry count and the tails stay byte-stable - the lowest-risk way to prove the client accepts a DB-built item.
- `RetailLoginReplay.ReplayStrieLoginWorldBurst` resolves a fully-buildable demo item (Royal Signet 3774542 -
  `TryResolveInjectableItem` via the new `IDatabaseRepository.GetItem`) and, if found, routes the inventory packet
  through `BuildBeachInventoryPacket` -> `ReplaceFirstBagItemFromDb` (builds via `SnapshotItemBuilder`, swaps in
  place, re-serializes with manual CRC). Falls back to the verbatim inventory if the item/archetype isn't
  available, so login stays reliable.
- Count forced to 1 (Royal Signet is non-stackable) to avoid a confound.
- Structurally verified by `SnapshotItemBuilderTests.ReplaceFirstBagItem_InPlace_KeepsCountAndSwapsTemplate`
  (re-parse: entry count unchanged, first bag item now reports template 3774542, itemId/record preserved).
IN-CLIENT TEST: log in a beach character; the first bag item should become "Royal Signet" and the client should
load normally. If it loads -> DB-built items are accepted in-client (the add/remove stall was the fabricated
identity, now solved). If it stalls -> the root-size change / tails need attention next.

STEP (b) — DRIVEN FROM CharacterEquipment (2026-06-07, pending in-client test). The hardcoded demo is replaced
by a real DB-driven path keyed off `CharacterEquipment` (which already has the per-slot Item navs - no new table):
- `EnsureBeachStarterEquipment(character)` loads (or creates) the character's `CharacterEquipment` and seeds
  default starter gear if empty: **Wrist = Slave Manacles (3361557)**, **FirstPosBag = Royal Signet (3774542)** -
  both fully buildable and slot-appropriate. Persisted to the DB (re-seeds after a faolan.db revert).
- `ResolveDbInventory` picks the slots that hold a fully-buildable item; `ApplyDbInventory` replaces those
  captured snapshot entries IN PLACE: the Equipped container by slot id (the archive player_items slot bytes:
  chest=0x05, main_hand_left=0x06, legs=0x10, wrist=0x12, ...) via `EquippedSlotItem`, and the first Bag entry
  from FirstPosBag. Each via `ReplaceEntryInPlace` (keeps itemId/record id -> count + tails byte-stable).
- Equipped items are slot-validated client-side, so only slot-correct items are seeded; the bag is permissive.
With the default seed the visible result equals the last test (Royal Signet in the bag; the wrist swap is
byte-identical since Slave Manacles is already the captured wrist item) - but now BOTH come from the DB
`CharacterEquipment`, persisted and extensible. To show a *different* equipped item we need that item's slot type
(learn per-item, or from a fuller item DB) so it lands in a slot the capture actually has (5/6/16/18).

EQUIP/UNEQUIP PERSISTENCE (2026-06-07, pending in-client test). The player's equip/unequip now writes back to
`CharacterEquipment`. Decoded the move command from the client logs: `0x642CD3D6` Interact, actionType
`MoveEquippedToFromInventory`, `InteractArgs.TypeId` = **42010 (unequip / EquippedToInventory)** or **42011
(equip / InventoryToEquipped)**; after the protobuf args a trailing struct of big-endian uint32s holds
**FromSlot, ToSlot, item-template-handle** (e.g. equip Royal Signet 3774542 -> slot 9 ring_left). Wiring:
- `Handle0x2000` move cases read the handle from the trailing bytes (`unk2`) and call `PersistEquip` /
  `PersistUnequip` (`EquipmentPersistence.cs`).
- `PersistEquip(targetSlot, handle)` resolves the handle via `Database.GetItem`, sets the mapped
  `CharacterEquipment` slot and clears the bag (FirstPosBag) if it held it. `PersistUnequip(fromSlot)` clears the
  slot and parks the item in the bag. Slot<->nav map (`Get/SetEquipmentSlot`) matches `CharacterEquipmentSlot`
  (Chest=5, MainWeapon=6, LeftRing=9, Legs=16, Wrist=18, ...), tested by `EquipmentSlotMappingTests`.
- `EnsureBeachStarterEquipment` now seeds defaults ONLY for a brand-new equipment row, so persisted player changes
  are never overwritten on relogin.
- Non-DB (captured-only) items can't be resolved to a DB row, so their moves are logged and skipped (harmless).

KNOWN LIMITATION (relogin rendering): persistence is correct in the DB, but reflecting it on next login is bound
by the in-place-replace approach - we can only swap entries the captured snapshot already has (equipped slots
5/6/16/18 + the first bag slot). An item equipped into a slot with no captured entry (e.g. a ring, slot 9) is
saved but won't render until we ADD equipped entries; an emptied slot still shows the captured item until we
support REMOVE. Those (add/remove of equipped entries) are the next rendering step.

NEXT: widen coverage (slot-correct buildable items; the equipped container only has entries for slots already in
the capture); ADD/REMOVE equipped+bag entries so persisted changes fully render on relogin (real-identity adds
are proven safe); model the bag as a list; then the full DB-assembled `737A6DF9` (containers + prefix + tails)
and the op-`0x1d` live add path.

**Phase 6 — Static world & retire the replay.**
DONE (2026-06-08, partial): the world burst is now **DB-driven per map**. `Faolan.Importer`
(`MapWorldBurstExtractor`) stores each captured map's full burst in the `MapWorldBurst` tables; on login
`ReplayMapWorldBurst` replays the burst for the character's map (re-addressed to the live char), so ANY captured
map renders - not just the beach. The embedded `strie-login-worldburst.bin` is retired (the beach burst is seeded
into the DB from that blob, byte-identical). The burst is still the captured RAW packets, not reconstructed from
`Maps`/`MapObjects` - so the LITERAL static-world dissection (`66AEDD50`/`F096251E`/`33A56FB0` -> DB-built) is
still the remaining work, but logins no longer need an embedded blob and any map with a capture works. KNOWN: a
couple of maps whose captured player instance is out of the 4-byte-varint range don't re-address cleanly yet.

## Phase 7 — NPC conversations (dialogue)

Clicking an NPC sends an `Interact` (`642CD3D6`, actionType `0x16` = TalkToNpc); the clicked NPC's entity
handle is in the args (field 10 / field 2, `args.SpellTarget.TargetHandle`). Retail replies on the player +
201C conversations with: interact echoes (`642CD3D6`), an entity-state block, a `0x2015` "select target",
then the **conversation window**: `4A3D899A` (conversation, id 12493) + `F164A4A5` (`remoteformat` XML, id
7615). All are keyed to the NPC handle and embed the player name/guild in the XML.

We have **many** captured conversations, including full branching trees - not just the intro. Survey
(`4A3D899A` node / category 50002 + `F164A4A5` remoteformat / category 50001):
- The Kalanthes post-creation intro is **id 12493** ("Stranger" greeting - a shared first-greeting node).
- The **strickenbla combat dump** has a fuller tree for its quest/combat NPC: `12493` (greeting) ->
  `12490 -> 20018 -> 20019 -> 20020` (with remoteformats `12621/12622/12623`) -> `19919, 26909, 12492,
  12494` as the player clicks through options.
- The **Strie-randomActions** and **demol80** login captures have yet more (`7731/19985/31636`, `21717/22047/
  35071`, etc.).

Mechanism: each player option-click is a `TalkToNpc` whose args encode the chosen branch (e.g. `105f` ->
`105f1820` -> `108c01`); the server replies with the next node(s). The dialogue **text is client-side**
(localized by id, e.g. `9411`); the server only sends node ids + parameters. So an NPC conversation is a
small **state machine** - `(current node, chosen option) -> next node id(s)` - directly extractable from
these captures. The 12493 "Stranger" node is generic; the NPC-specific tree follows it.

Current state: a **data-driven conversation replay engine** is implemented.
- `Captures/extract_conversations.py` extracts each `(TypeId, Mode, Field3) -> response packets` node from a
  capture into `Faolan/GameServer/Data/conversations-<tag>.bin` (embedded). Default source is the
  Strie-login-randomActions capture (same beach as the login replay); 5 nodes for the two beach NPCs.
- `ConversationReplay.cs::TryReplayConversation` (gated to the Strie instance) matches the client's TalkToNpc
  `(TypeId, Mode, Field3)`, substitutes the live NPC handle + instance into the captured response packets
  (4->4 bytes, so length prefixes stay valid; `PacketStream.SendAsync` recomputes the CRC), and sends them.
  The `TalkToNpc` handler tries this first, falling back to the create-world Kalanthes intro.

Remaining for this phase: capture more option branches (only the clicked-through nodes are recorded); the
fallback Kalanthes intro still embeds the "Dikketest" player name (rebuild per-character); generalise off the
Strie gate (instance substitution needs a varint-aware receiver rebuild when the live instance differs).

To finish:
- Source per-NPC conversation trees (the client drives them by category/key; the server sends the matching
  `remoteformat`). Likely a `NpcConversations` table or a content import keyed by NPC.
- Rebuild the `remoteformat` XML per-character (player name, guild) instead of the captured "Dikketest".
- Model dialogue-tree advancement (the follow-up nodes the player clicks through), not just the opening node.
- Restore the create-world intro to Kalanthes's approach moment specifically (it is intro content, not a
  generic click response).

## DB / model gaps to close along the way

- `CharacterBodies` & `CharacterEquipment` are **empty** — populate `CharacterBodies` from the create-editor
  morph stream (`SetMorphValue`/`TheNameIs` in `HandleCommandPacket.cs`) and give new characters default
  equipment. Until then Phase 3/5 fall back to captured literals.
- Need a handle allocator (above) — handles are server-assigned per session, not stored.
- Seed a few `Npcs` rows for the Tortage beach (from the dissected beach spawns) to drive Phase 4.
- Stats/level/class tables exist on `Characters`; map them to the relevant `FA015807` fields once dissected.

## Acceptance per phase

1. Round-trip unit test green (built bytes == capture for the known character/NPC).
2. `Captures/compare_log_to_dump.py` shows the dynamic path produces the same (family, opcode) profile.
3. In-client: a non-Strie character (and NPCs) render correctly at their own DB-driven position/appearance.

## Order of attack (smallest first)

Phase 0 tooling → Phase 1 (player position/name, highest value, smallest dissection) → Phase 2 (handles,
unblocks any-character) → Phase 4 (NPCs from DB) → Phase 3/5 (appearance, inventory) → Phase 6 (static world,
retire replay).

## Archive reference (the OLD C++ server) — a data & protocol goldmine

`Archive/` in this repo (extracted to `/tmp/faolan-archive/trunk`) is the **previous-generation Project Faolan
server written in C++**. It is NOT portable to the C# server (different language/architecture) but it is the
best **reference and DATA source** we have — it shipped real world content. What it gives us, project-wide:

- **`trunk/sql/aocdatabase.sql`** — the full retail-shaped schema: `player_items` / `npc_items` (equipment slots,
  see Phase 5), `npcs` (NPC spawn data: model, map, position), `playerstats` / stat tables, `classes`, maps, etc.
  This is the schema the C# DB should grow toward for DB-driven spawns/inventory.
- **`trunk/sql/p_items2.sql`** — the **item template table** `p_items(name, id, level, data0..data4)`. The
  `data1..4` quad is the item identity used in the inventory snapshot tails and the op-0x1d add packet. This is
  the missing item DB (the C# `Items` table is just `(Id, Name)`).
- **`trunk/sql/` NPC/world data** — `npcs` has named beach NPCs incl. Kalanthes (id 433376, model 2704, map
  4010) — drives Phase 4 (NPC spawns from DB) without needing new captures.
- **`StatIds.txt`** — the stat id ↔ name mapping (for the inventory tails / `playerstats`).
- **`WorldServer/WorldServerOpcodes.h`** — opcode names (e.g. add-item = `0x1d`), a Rosetta stone for our
  `0x2000` sub-opcodes / world ops.
- **`WorldServer/CommandHandler.cpp`** — reference implementations: `addItem` (item packet layout, see Phase 5),
  GM/command handling, spawn logic. Read it as protocol documentation, not code to port.
- **`classes` table** — confirms the `Class` enum (e.g. Priest of Mitra = 24 = 0x18).

How to use it: when a phase needs DATA (item templates, NPC rows, stat ids, equipment-slot meanings, class/race
enums) or a question about "what does retail send here", check the archive first — it usually has the answer that
the captures alone don't. Concrete near-term wins it unblocks: importing `p_items` + `player_items` for inventory
(Phase 5), seeding `npcs` for Phase 4, and `StatIds`/`playerstats` for stats.

NOT-YET-IMPORTED archive tables (kept on the shelf — embedded in `Faolan/GameServer/Data/archive/aocdatabase.sql`,
import on demand by extending `ArchiveDataImporter` if/when a consumer appears):
- `const_stats` (+ `const_stats_new.sql`) — per (level, race, class) base health/mana/stamina/exp and S/I/C/D/W
  stat progression. The real data behind a future stats system. (~70 rows.)
- `const_abilitys` / `p_abilities` — class abilities & ability templates (id + data0..4 quad, same shape as
  `p_items`) per class/level.
- `races` / `classes` / `archetypes` / `race_arche_class` — reference/enum data. Already expressed as C# enums
  (`Race`/`Class`/`ArchType` + the valid-combo list in `Statics.cs`); `classes` also carries the client class id
  (e.g. Barbarian=18). Import only if we want them DB-driven.
- `accounts` / `characters` / `body` / `face` / `player_items` / `playerstats` — test/empty rows, intentionally
  skipped (we have our own accounts/characters; `body`/`face`/`player_items`/`playerstats` ship no data rows).
