# Player spawn generation plan (removing spawn-player-template.bin)

Goal: generate the player's own `SimpleCharFullUpdate` (0xFA015807) spawn entirely from DB character data, with
**full equipment rendering**, so `spawn-player-template.bin` can be deleted. Chosen 2026-06-10.

## What's proven / available

- The char message inside the spawn body is a **`ProtoCoreCharacterIIR`** and decodes + re-encodes
  **byte-identically** via `FromProtobuf` / `ToProtobufBytes` (locked by `PlayerSpawnGenerationTests`). 469 bytes for
  Strie's spawn.
- **The equipment lives inside it**: `ProtoCoreCharacterIIR.InventoryEntries` (field 23, repeated `InventoryEntry`).
  Each `InventoryEntry { pos, item: ProtoAcgItem, gem_slots[] }`. The `ba 01 <len>` blocks in the body are these.
- `ProtoAcgItem { key1/key2/key3: ProtoDecryptionKey, template1/2/3: fixed32, level, hash }`.
- **`SnapshotItemBuilder.BuildEntry(...)` already builds a `ProtoAcgItem` from a DB `Item`** (key1=key2=key3 =
  ProtoDecryptionKey(Data1..4), template1=template2 = Item.Id, hash = archetype). Reuse this for the spawn slots.
- `CharacterEquipment` holds the worn items; `ResolveDbInventory` / `EnsureBeachStarterEquipment` already resolve
  `CharacterEquipment` -> buildable DB items for the inventory snapshot. Reuse for the worn/visible subset.
- `SpawnPacketBuilder` already constructs transform + name (`SetSpawnName`) + stats (`SetCharacterStats`), adjusting
  the three enclosing length prefixes (@0 body length, @133 `NameOuterLengthOffset`, @304 `NameInnerLengthOffset`).

## Body layout (Strie template, 990 bytes; offsets are body-relative)

```
[0:20]    IIR header: bodyLen, subOp=FA015807, ack=C350, handle, fieldA
[20:~66]  transform: ProtoGameCodeDynelIIR (0x2C group: 0A vec3, 12 quat, 25 scale, 18 playfield)
[~66:133] attribute scaffold (mostly zeros)
@133      outerLen (802) enclosing [137:939]
  [137:304] scaffold-A (attribute table, mostly zeros; SOME char-variant)
  @304      innerLen (469) = ProtoCoreCharacterIIR length prefix
  [308:777] ProtoCoreCharacterIIR (name/firstname/lastname/stats/npc_stats/player_stats/bcc/.../inventory_entry)
  [777:939] scaffold-B: [id][value][float 1.0] tables (skills/feats - char-variant)
[939:990] tail: more [id][value] tables
```

Cross-capture diff (player FA015807, different chars: 970 / 2319 / 2742 bytes): char-variant = transform, the
CharacterIIR (name/stats/**equipment via inventory_entry** - the bulk), and the scaffold-B / tail skill tables.

## Implementation steps

1. **`SpawnPacketBuilder.SetCharacterMessage(body, ProtoCoreCharacterIIR)`** - replace the CharacterIIR component
   `[NameTagOffset : NameTagOffset + innerLen]` with the re-encoded message; adjust @0 / @133 / @304 by the delta
   (same three prefixes `SetSpawnName`/`SetCharacterStats` adjust). Unit-test: decode template CharacterIIR ->
   `SetCharacterMessage` with it unchanged == byte-identical (build-side oracle).
2. **Equipment InventoryEntries from `CharacterEquipment`**: for each worn/visible item, build
   `InventoryEntry { pos = slot, item = ProtoAcgItem(from SnapshotItemBuilder logic) }`. Need the slot->spawn-pos
   mapping (decode a captured spawn's InventoryEntries to learn the `pos` values per equip slot).
3. **`TryRebuildPlayerSpawnFromDb`**: decode the template CharacterIIR, override `Name`, `Stats` (sex/race/class/
   level/health from the character), and `InventoryEntries` (step 2); keep player_stats/bcc/etc. from the template
   for now; re-encode; `SetCharacterMessage`. (Replaces the current `SetSpawnName`+`SetCharacterStats`.)
4. **Verify**: `tools/client-smoke-test.sh` 16/16. The player spawn is the FINICKIEST packet - even a rotation change
   hung the client (RetailLoginReplay:339) - so expect iteration; a hang = wrong bytes, revert and inspect.
5. Later: scaffold-A/B and the skill/feat tables from char data, then delete `spawn-player-template.bin` and the
   transform-template dependency. FullChar (0x737A6DF9) is the analogous next target (test `ProtoFullCharacterIIR`
   round-trip).

## Prerequisites (blockers found 2026-06-10 while wiring it live)

The packet-construction half is done (`SetCharacterMessage`, the round-trip, `CharacterInventory`). The live
integration is gated on **character data-model groundwork**, not on packet building:

1. **No race/class/sex/level on `Character`.** `Character` has Name/Experience/Map/Realm + nav props (Body, Equipment,
   MaxStats) but NOT race/class/sex/level. The spawn's `CharacterStats` (sex=1, side=2, race=3, life/health, level=18,
   …) currently comes only from the captured template. Need to surface race/class/sex/level (likely add to `Character`
   or `CharacterBody`; the create-character flow chooses them - check where they're persisted) and a
   `Character -> ProtoCoreCharacterIIR.CharacterStats` builder.
2. **Geared test character = necrol80 (from the captures).** The `account-faolan-character-me*` dumps have a fully
   geared character with plenty of inventory/equipment, and **necrol80's player spawn is already in `faolan.db`** as a
   burst entity (e.g. map 2000, the FA015807 whose handle == that burst's CapturedInstance, ~2319 bytes - vs Tester's
   ~970). So the data and the **byte-identity oracle** are already present: decode necrol80's captured `ProtoCoreCharacterIIR`,
   extract its `InventoryEntries` (pos + ProtoAcgItem keys = the worn items) + `Stats` (sex/race/class/level/health)
   into `CharacterEquipment` / `CharacterInventory` / a stats source for a "necrol80" Character, then GENERATE her spawn
   from those rows and assert it byte-matches the captured one. That extraction (captured spawn/FullChar ->
   structured character) is the next build; it doubles as the importer for real characters and gives the equip-slot ->
   `InventoryEntry.pos` mapping for free.

Once those exist: `TryRebuildPlayerSpawnFromDb` decodes the template `ProtoCoreCharacterIIR`, overrides Name + Stats +
InventoryEntries from the DB, and calls `SetCharacterMessage`; verify with the harness (expect iteration). Only name
is overridable today (which `SetSpawnName` already does), which is why the live step stops here.

See [[world_entry_from_db]] for the surrounding burst/server-generation architecture.
