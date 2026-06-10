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
InventoryEntries from the DB, and calls `SetCharacterMessage`; verify with the harness (expect iteration).

## Unblocked (2026-06-10) - reader + extraction proven

- **`PlayerSpawnReader.TryReadCharacterMessage(body)`** decodes the `ProtoCoreCharacterIIR` from ANY spawn (proven on
  Strie's template AND necrol80's geared spawn from faolan.db map 2000). NecroL80: name=AssassinL23, sex=2/race=3/level=23,
  **17 worn `InventoryEntries`** each with slot `pos`, the item's `ProtoDecryptionKey` quad (= Item.Data1..4),
  `Template1` (= item id), and `Level`. So the equipment is fully extractable from a captured spawn.
- **`pos` -> slot = `CharacterEquipmentSlot` enum** (Neck=1, Head=2, Chest=5, MainWeapon=6, Belt=7, LeftRing=9,
  RightRing=10, Cape=11, Shoulder=12, Boots=14, Hands=15, Legs=16, Wrist=18). 13/17 of necrol80's slots map; pos
  3/8/20/21 are still unmapped (extend the enum - likely face/amulet, offhand, vanity, ...).

### Remaining build (all dependencies resolved)

1. **Extraction importer** (Faolan.Importer): for a captured char's spawn, `PlayerSpawnReader` -> create partial `Item`
   rows (Id=Template1, Data1..4=key quad, Archetype=hash, Level) + `CharacterEquipment` rows (pos->slot enum) + the
   `Stats` (needs the race/class/sex/level fields above) for a Character. Run it on necrol80 -> a geared test character.
2. **Equipment builder**: `CharacterEquipment` worn items -> `InventoryEntry { pos, ProtoAcgItem }` (ProtoAcgItem via
   `SnapshotItemBuilder` logic). Unit-oracle: build from necrol80's extracted rows == her decoded `InventoryEntries`.
3. **Wire `TryRebuildPlayerSpawnFromDb`** (decode template -> override name/stats/inventory -> `SetCharacterMessage`),
   harness-verify, then drop `spawn-player-template.bin`.

## Scaffold wire format (dissected 2026-06-10)

Data model done (commit 921ab0e): `CharacterAttribute {StatId, Value}` + `CharacterSkill {SkillId, Value, Weight}`.
The IIR scaffold-B (after the ProtoCoreCharacterIIR, before the tail) decodes as:
- **header** `00000000 00000004` (8 bytes, constant).
- **7 weighted attribute entries**, each ending in `3F800000` (=1.0): entry 1 = `[u32 2][u32 0x3F1][u32 value]
  [u32 0xB7]` (16b), entries 2-7 = `[u32 index 1..6][u32 0][u16 value]` (10b). The **value field is char-variant**
  (Strie 7/0x311/0x147/... vs necrol80 0xE/0x85/0x144/...); 0x3F1 / 0xB7 are constant. -> CharacterAttribute rows.
- **extended skill/feat list** (the tail): `[u32 index 7,8,9,...][u32 0][value+data]` entries, variable count
  (necrol80 has ~14+, Strie few) and mixed widths (some `01 XX <4 bytes>`). This is the per-character skill/feat set
  -> CharacterSkill rows. Full tail-entry widths still need nailing across more captures before byte-exact regen.

scaffold-A (before the CharacterIIR, 167b, ~constant across chars per the diff) = the attribute/derived-stat block;
mostly fixed, dissect last.

**Feat-list format CRACKED (2026-06-10, confirmed across fresh + geared chars).** The feat entries are UNIFORM
14 bytes: `[u32 index][u32 0][u8 type][u8 subindex][u32 value]`, index incrementing from 7. Verified on the create
captures' fresh chars (Hoxxling, Dikketest, Strickenen) AND the geared necrol80/2040 chars - all the same layout. type
is 0x00/0x01, subindex small, value a float-encoded 32-bit (e.g. 0x3D343958). The entry COUNT grows with level
(Strickenbla, leveled, has many; a brand-new char few) -> these are progression-gained skills/feats, NOT the fixed
face morphs. (Strie's spawn-player-template.bin login spawn looked different only because she'd progressed a little;
the base format is the uniform one.) Maps onto CharacterSkill {SkillId=index, Value=value, + type/subindex columns}.
The fresh-char scaffold (header + 7 weighted attribute entries + this uniform feat-list + the 0003F1 tail) is now
fully parseable -> generation is unblocked; scaffold-A (the ~constant attribute block before the CharacterIIR) is the
last piece to dissect.

**Tail = a feat/ability ID list (dissected further).** After the 7 weighted attribute entries: one more weighted
entry (idx 7, a float weight, not 1.0) then a **variable-width sorted ID list** with its own header (`01 2c ...`) of
the character's feats/abilities (Strie few, necrol80 many; IDs ascending, mixed 2-4 byte widths), ending in the
`...0003F1 00000000 ...0003F1` sentinel. Byte-exact regeneration needs the ID-list width/header encoding cracked
across more captures - a multi-capture RE task, not a quick finish.

**Conclusion (the binary choice to delete spawn-player-template.bin):** after all the char-data generation
(CharacterIIR: name/race/sex/level/vitals/equipment - done), the ONLY thing the template still supplies is the
non-CharacterIIR "scaffold frame" (~520 bytes: scaffold-A + the attribute/feat scaffold-B + tail). That frame can come
from exactly one of two places - (a) a documented C# constant (relocation, the RetailCreateWorldEntryBurst pattern;
removes the file but isn't "from char data"), or (b) generated from CharacterAttribute/CharacterSkill once the
feat-list wire format is fully cracked (the multi-session RE above). There is no third option; the frame is either a
constant or generated.

See [[world_entry_from_db]] for the surrounding burst/server-generation architecture.
