# Inventory: the FullChar snapshot, delivery model, and equip moves

Status 2026-06-13: **solved end-to-end in-client** (bag renders, equipment doll renders, equip/unequip
persists and survives relog). Commits `20f2cc1` (delivery), `226b16f` (equip/unequip protocol), `24fbed6`
(the socket partial-write fix that masked everything), `135b06f` (dead-path cleanup),
`85c2886` (the BE32 root-length framing recovered via Ghidra — the final fix).

## The snapshot (0x737A6DF9 FullPlayerCharacterIIR)

The body is read by `n3InfoItemRemote::Construct` (decompiled from `AgeOfConan.exe` — `docs/re/`,
`docs/db-container-handoff.md`), all **big-endian**:

```
[u32 KEY = subop = 0x737A6DF9][u32 identity.type = 50000 (0xC350)][u32 identity.instance]
[u8 flag][u32 BE blobLen][protobuf blob = blobLen bytes][u32 BE version = 0x18][tails]
```

- **The protobuf root is length-prefixed by a 4-byte BIG-ENDIAN length** (`FUN_00cbfb80` reads exactly
  `blobLen` bytes, then `ReadSubClass` reads the `0x18` version). The "blob" = `field5` (the constant
  `28 81 06` = protobuf field 5 varint 769) + the FullCharacterIIR root, so **`blobLen = field5Len + rootLen`**
  (`field5Len = prefix.Length - 5`). Regenerate the root and that BE32 length MUST be rewritten
  (`GameServerListener.SetSnapshotBlobLength`) or the client reads the version from the wrong place and rejects
  the FullChar (`CoreFullPlayerCharacterIIR::ReadSubClass() Wrong IIR_t version ...`). The captured 8-byte
  prefix `00 00 00 02 ae 28 81 06` is really `[00 = flag][00 00 02 ae = blobLen 686][28 81 06 = field5]`.
  (The earlier `varint = 4483 + rootLength` / `AdjustSnapshotPrefixRootLength` model was byte-misaligned and is
  retired; `4483` was a coincidence of two near-equal-length captures.)
- Root = `ProtoFullCharacterIIR` (recovered schema, byte-exact round-trip on the captured snapshots):
  field 6 × N = the containers (Equipped 101 / Bag 104 / Resource 112 / Quest 113), then fields 7, 8.
- Tails = the 9282-byte record/XML region starting `00 00 00 18` (subclass version 24). Carried from the
  capture with live level/vitals/XP patched (`BigboyTailsFile`); a from-scratch template build exists
  (`BeachTailsBuilder`, golden tests `BeachSnapshotConstructionTests`) for the construct direction.

Containers are built wholesale from the DB (`FullCharSnapshotBuilder.BuildContainers`:
`CharacterEquipment` + `CharacterInventory` rows; entry = `SnapshotItemBuilder.BuildListEntry`, identity =
`Item.Data1..4` quad + `Item.Archetype`, buildable iff `Archetype != 0`).

## Delivery model (the hard-won facts)

1. **The world-entry snapshot is THE inventory delivery** - it feeds both the bag window and the equipment
   doll, and nothing re-feeds them later. `BuildBeachInventoryPacket` substitutes the player's DB containers
   into the (instance-rewritten) captured snapshot unconditionally (and rewrites the BE32 blob length).
2. **The old "any container mutation stalls world-load/chat" rule is obsolete.** That bisect pre-dated the
   ContentMirror: the stall was the client thrashing on item resources it couldn't fetch from the dead
   retail CDN. With the mirror serving resources, substitution is in-client verified safe.
3. **A second 0x737A6DF9 for the same identity is dead on arrival.** The client either rejects it
   (ReadSubClass error - even an EMPTY rebuild) or constructs it and never re-reads it into the UI. There
   is no mid-session inventory delivery: `.additem` / `add_item` persist a row that appears on the NEXT
   login. (There is also no server "add item to bag" packet in retail - loot adds are client-side.)
4. **Bag-poison items**: an item only ever captured WORN (never inside an inventory container) can render
   the WHOLE bag empty when placed in `CharacterInventory` (repro: 13826 Combatant's Hauberk; client logs
   VisualMeshLoader 1010001 misses). Likely missing local bag/icon resources. Keep such items out of bags
   until the resource gap is solved (TODO).
5. **Wire health matters more than content**: `Socket.Send/SendAsync` partial writes used to truncate the
   ~10 KB snapshot mid-frame (send buffer full right after the world burst), making byte-identical
   snapshots fail "sometimes". Sends now loop to completion (`NetworkClient`).

## Equip/unequip moves (Interact 0x09) - decoded from capture

Capture: `account-stricken998-character-combat_armed-and-unarmed` (2026-06-12). Golden tests:
`BehaviorAckTests`.

- Request args protobuf: `{TypeId, Mode, Category:10, Field6:0}`. **Mode is the client's monotonic
  per-session action counter** (shared across all BCC interactions), NOT a direction. **TypeId selects the
  drag's window pair**: 42010/42011 (one gesture family), 41842/41843 (the backpack-window family) - same
  tail grammar.
- Into-slot tails: `[srcBagCell][destEquipSlot][itemTemplate][itemTemplate][0][flags]`;
  out-of-slot tails: `[srcEquipSlot][destBagCell][0...]`.
- Server reply = the behavior echo-ack (request echoed, seq+1, field3 = the request's Mode spliced into the
  args) with the tail's zero padding replaced by the identity block: `[Item.Data1..4 quad] x2 +
  [B2 90 80 5C 29 E6 27 CA] x2` on equip, the null filler x6 on unequip - plus a 0x201C record
  (`prefix 0x65, state = destSlot` on equip; `0x68, 0` on unequip).
- Persistence: `EquipmentPersistence.cs` (equip writes the slot + consumes the bag row; unequip parks the
  item at the exact bag cell).

Slot ids (equipped container `pos` = `CharacterEquipment` slot): chest 5, main hand 6, ring 9, legs 16,
wrist 18, second weapon/sheath 0x14 - verified against the captured container.
