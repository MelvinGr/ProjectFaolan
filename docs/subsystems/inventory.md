# Inventory: the FullChar snapshot, delivery model, and equip moves

Status 2026-06-13: **solved end-to-end in-client** (bag renders, equipment doll renders, equip/unequip
persists and survives relog). Commits `eb776cdf` (delivery), `2ea23c33` (equip/unequip protocol), `d73fe78a`
(the socket partial-write fix that masked everything), `7c83d2df` (dead-path cleanup),
`ae5a3cb4` (the BE32 root-length framing recovered via Ghidra — the final fix).

## The snapshot (0x737A6DF9 FullPlayerCharacterIIR)

The body is read by `n3InfoItemRemote::Construct` (decompiled from `AgeOfConan.exe` — see `docs/re/`),
all **big-endian**:

```
[u32 KEY = subop = 0x737A6DF9][u32 identity.type = 50000 (0xC350)][u32 identity.instance]
[u8 flag][u32 BE blobLen][protobuf blob = blobLen bytes][u32 BE version = 0x18][tails]
```

- **The protobuf root is length-prefixed by a 4-byte BIG-ENDIAN length** (`FUN_00cbfb80` reads exactly
  `blobLen` bytes, then `ReadSubClass` reads the `0x18` version). The blob **IS** the whole FullCharacterIIR
  root, so the prefix is exactly 5 bytes and **`blobLen = rootLen`**. Regenerate the root and that BE32 length
  MUST be rewritten (`GameServerListener.SetSnapshotBlobLength`) or the client reads the version from the wrong
  place and rejects the FullChar (`CoreFullPlayerCharacterIIR::ReadSubClass() Wrong IIR_t version ...`). The
  captured beach prefix `00 00 00 02 ae` is `[00 = flag][00 00 02 ae = blobLen 686]`.
  (Two earlier models are retired. The `varint = 4483 + rootLength` / `AdjustSnapshotPrefixRootLength` one was
  byte-misaligned — `4483` was a coincidence of two near-equal-length captures. The one after it treated the
  next 3 bytes `28 81 06` as a constant "field5" and read `root = blobLen - 3`; those bytes are the root's OWN
  first field, `subscription_flags` (field 5, varint 769), which is only 3 bytes long on the L1 beach captures.
  A character with `character_tag` (field 1, packed) leads with that instead, so the fixed skip landed
  mid-varint and EVERY level-80 snapshot failed to deserialize — see #381.)
- Root = `ProtoFullCharacterIIR` (recovered schema, byte-exact round-trip on the captured snapshots):
  field 6 × N = the containers (Equipped 101 / Bag 104 / Resource 112 / Quest 113), then fields 7, 8.
- Tails = the 9282-byte record/XML region starting `00 00 00 18` (subclass version 24). The live world-entry
  path builds them from scratch with this character's live level/vitals/XP via
  `FullCharSnapshotSubClass.BuildFromScratch`, proven byte-exact against the captured `BeachTailsTemplate.bin`
  oracle (golden tests `BeachSnapshotConstructionTests`).

Containers are built wholesale from the DB (`FullPlayerCharacterBuilder.BuildContainers`:
`CharacterEquipment` + `CharacterInventory` rows; entry = `SnapshotItemBuilder.BuildListEntry`, identity =
the `Item.Key` (Data1..4) quad + `Item.Hash`, buildable via the hash-gated snapshot iff `Hash != 0`).

## Delivery model (the hard-won facts)

1. **The world-entry snapshot is THE inventory delivery** - it feeds both the bag window and the equipment
   doll, and nothing re-feeds them later. `SendBurstlessWorldEntry` builds the snapshot from scratch — the
   player's DB containers become the FullCharacterIIR root (`BuildBurstlessSnapshotRoot`) — and writes the
   BE32 blob length to match (`SetSnapshotBlobLength`). (No captured snapshot is spliced into any more; the
   old `BuildBeachInventoryPacket` substitution path is retired.)
2. **The old "any container mutation stalls world-load/chat" rule is obsolete.** That bisect pre-dated the
   ContentMirror: the stall was the client thrashing on item resources it couldn't fetch from the dead
   retail CDN. With the mirror serving resources, substitution is in-client verified safe.
3. **A second 0x737A6DF9 for the same identity is dead on arrival.** The client either rejects it
   (ReadSubClass error - even an EMPTY rebuild) or constructs it and never re-reads it into the UI — so
   re-sending the whole snapshot is NOT how you deliver a mid-session item. **UPDATE 2026-06-13:** the real
   live-add IS a per-item packet — `DispatchAddItem` (s2c 0x2020), decoded + byte-exact + in-client verified
   (`GrantItemToBackpack`; `DispatchAddItemTests`). `.additem` / `add_item` now both persist the row AND push the
   live add (no relog needed). **Live item-REMOVE — `DispatchRemoveItem` (s2c 0x201F)** is now built too (2026-06-14):
   byte-exact builder (`WriteRemoveItemBody`/`SendRemoveItem`, body `[container ID32][u32 pos][u32 amount]`,
   `DispatchRemoveItemTests`), `RemoveItemFromBackpack` clears the persisted cell AND pushes the live remove, wired to
   the `.removeitem <slot>` GM command (inverse of `.additem`). Needs an in-client confirmation pass.
4. **Bag-poison items**: an item only ever captured WORN (never inside an inventory container) can render
   the WHOLE bag empty when placed in `CharacterInventory` (repro: 13826 Combatant's Hauberk; client logs
   VisualMeshLoader 1010001 misses). Likely missing local bag/icon resources. Keep such items out of bags
   until the resource gap is solved (TODO).
5. **Wire health matters more than content**: `Socket.Send/SendAsync` partial writes used to truncate the
   ~10 KB snapshot mid-frame (send buffer full right after the world burst), making byte-identical
   snapshots fail "sometimes". Sends now loop to completion (`NetworkClient`).

## Equip/unequip moves (Interact 0x09) - decoded from capture

Capture: `Stihetfv_soldier-conqueror_login_combat_armed-and-unarmed` (2026-06-12). Golden tests:
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
- Persistence: `Inventory/EquipmentService.cs` (equip writes the slot + consumes the bag row; unequip parks the
  item at the exact bag cell).

Slot ids (equipped container `pos` = `CharacterEquipment` slot): chest 5, main hand 6, ring 9, legs 16,
wrist 18, second weapon/sheath 0x14 - verified against the captured container.
