# Inventory: the FullChar snapshot, delivery model, and equip moves

Status 2026-06-12: **solved end-to-end in-client** (bag renders, equipment doll renders, equip/unequip
persists and survives relog). Commits `20f2cc1` (delivery), `226b16f` (equip/unequip protocol), `24fbed6`
(the socket partial-write fix that masked everything), `135b06f` (dead-path cleanup).

## The snapshot (0x737A6DF9 FullPlayerCharacterIIR)

Body = `[subop][0000C350][instance]` + **8-byte IIR prefix** + **protobuf root** + **tails**.

- Prefix = `[u32 base version = 2][varint = 4483 + rootLength][81 06]`. The varint is the client's root
  delimiter: regenerate the root and the varint MUST track its length (`AdjustSnapshotPrefixRootLength`,
  or `BeachSnapshotPrefix.Build` from scratch) or the client misframes the tails version
  (`CoreFullPlayerCharacterIIR::ReadSubClass() Wrong IIR_t version ...`).
- Root = `ProtoFullCharacterIIR` (recovered schema, byte-exact round-trip on 37/39 captured snapshots):
  field 6 × N = the containers (Equipped 101 / Bag 104 / Resource 112 / Quest 113), then fields 7, 8.
- Tails = the 9282-byte record/XML region starting `00 00 00 18` (subclass version 24). Carried from the
  capture with live level/vitals/XP patched (`BigboyTailsFile`); a from-scratch template build exists
  (`BeachTailsBuilder` + `BeachSnapshotPrefix`, golden tests `BeachSnapshotConstructionTests`) for the
  construct direction.

Containers are built wholesale from the DB (`FullCharSnapshotBuilder.BuildContainers`:
`CharacterEquipment` + `CharacterInventory` rows; entry = `SnapshotItemBuilder.BuildListEntry`, identity =
`Item.Data1..4` quad + `Item.Archetype`, buildable iff `Archetype != 0`).

## Delivery model (the hard-won facts)

1. **The world-entry snapshot is THE inventory delivery** - it feeds both the bag window and the equipment
   doll, and nothing re-feeds them later. `BuildBeachInventoryPacket` substitutes the player's DB containers
   into the (instance-rewritten) captured snapshot, default ON (`FAOLAN_WORLDENTRY_DB_CONTAINERS=0` opts
   out to verbatim captured containers).
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
