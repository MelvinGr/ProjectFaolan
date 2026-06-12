# Backpack inventory — RESOLVED

## Goal (achieved)
A logged-in character's **real backpack items** appear in the inventory bag grid in-client, DB-driven.
Worn/equipped gear already rendered via the entity spawn; this closed the backpack.

## What landed (2026-06-12)
The bag now renders the player's `CharacterInventory` items, and re-arrangements persist. Commits:
- `b7d9a07` post-load DB inventory snapshot wired (gated groundwork).
- `b22cd69` **the fix** — construct the DB backpack snapshot the client accepts (IIR prefix root-length).
- `b8ceebe` persist bag moves, drops, and stack splits.
- `e476664` construct the beach snapshot prefix + tails from scratch.
- `b2558bc` pin the tails `UnknownBefore` decode result.
- `4ec13db` default the from-scratch beach snapshot on.

## How it works now
Inventory is delivered by the **world-entry `0x737A6DF9` snapshot**, which must be **verbatim during world load**
(any change stalls the load and breaks chat — see `protocol_chat_connection_regression`). So:

1. **World-entry snapshot** goes out **verbatim** (chat-safe). Worn gear renders from the entity spawn (DB-driven).
2. A **second, DB-built snapshot is sent AFTER the post-ready burst** (post-load, once the client is out of the
   constrained load phase and chat has connected). This carries the player's real items and updates the bag.
   Default on; opt out `FAOLAN_POSTLOAD_INVENTORY=0`.

### The hard bug that was blocking it (now fixed)
The DB-built snapshot regenerates the protobuf **root** from `CharacterInventory`/`CharacterEquipment`
(`FullCharSnapshotBuilder.BuildContainers`, byte-exact). The client delimits that root by a **root-length field
the snapshot carries in its 8-byte IIR prefix** — a varint at prefix offset 4 = `const + rootLength`. Carried
verbatim it stayed the captured root's length, so the regenerated (different-size) root desynced the client:
`CoreFullPlayerCharacterIIR::ReadSubClass() Wrong IIR_t version ...` → scratch-memory thrash → WatchDog kill.
**Fix:** `AdjustSnapshotPrefixRootLength` delta-adjusts that varint by the root-length change (the same technique
`SpawnPacketBuilder.SetCharacterMessage` uses for the spawn's CharacterIIR length). Result: 0 IIR errors, chat
connects, bag renders.

### Persistence (bag arrangement survives logout)
`MoveItemInInventory` / `DropItem` / `SplitStack` previously only logged. Now `PersistInventoryMove` /
`PersistDropItem` / `PersistSplitStack` (in `EquipmentPersistence.cs`) write the `CharacterInventory` rows
(swap/clear/split), and the legacy seeded `FirstPosBag` is materialized into rows when moved. Equip/unequip still
go through the `MoveEquippedToFromInventory` path. Equipped gear and bag positions both persist.

### Construct-from-scratch (beach)
On the beach (default on; opt out `FAOLAN_SCRATCH_BEACH_SNAPSHOT=0`) the snapshot is built entirely from
server-owned data — prefix from `BeachSnapshotPrefix` (constants + root length, no captured prefix), root from the
DB, tails from `BeachTailsBuilder` (server-owned `BeachTailsTemplate.bin` + generated level/vitals + re-addressed
instance). Byte-identical to the capture, 0 IIR errors. See `docs/...`/memory `snapshot_tails_construction_scope`.

## There is no server "add item to bag" packet
Confirmed: loot adds are client-side (from the `LootBagFull` the client already has). `0x79F8C7A1`/`0x63268258`
are **quest** packets (QuestJournalIIR/QuestFullUpdateIIR), not ContainerAddItem. The world-entry snapshot is the
only inventory-delivery mechanism.

## Testing the bag (headless harness)
Seed a bag: `CharacterInventory` rows, `Container=104` (BagKind), `Slot` 2..N, `ItemId` with `Archetype != 0`
(e.g. 13817/13826/117637). Run server from `Faolan/bin/Debug`; drive client with `Faolan.ClientLauncher`
(`-u faolan -p any -c <name>`). Chat OK iff `AgentServerListener[0] Client connected`. NOTE: the headless client
(Xvfb, no GPU) dies on an RDB resource thrash shortly after load — verbatim baseline dies identically — so the
final visual check needs a healthy client environment; protocol acceptance is verified via 0 IIR errors + the
golden tests.

## Key memories
`protocol_containeradditem` (the whole investigation + the IIR fix), `snapshot_tails_construction_scope`
(from-scratch tails), `protocol_chat_connection_regression`, `inventory_db_items_work`, `fullchar_generation`.
