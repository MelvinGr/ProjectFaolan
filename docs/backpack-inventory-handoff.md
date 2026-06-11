# Handoff: make the player's backpack inventory render in-client

## Goal
A logged-in character's **real backpack items** should appear in the inventory bag grid in-client.
(Worn/equipped gear already renders, DB-driven, via the entity spawn — that path is done and chat-safe.)

## What this session landed (committed)
- `db8e053` chat channels fixed — world-entry inventory snapshot sent **verbatim**.
- `a8da341` tutorial overlay disabled (post-ready record 3).
- `39c87d7` archetype harvest from spawn bodies — **192 → 1,348 buildable items** (item identities are now correct/complete; this was the prerequisite for inventory + worn-appearance).
- `22b4617` character-creator loading-screen deadlock fixed.
- `334dd19` character ids allocated in the 4-byte varint range (created-char login crash).

## The hard constraints (PROVEN — don't re-derive; see memory `protocol_containeradditem`)
1. **Modifying the world-entry `0x737A6DF9` snapshot breaks chat.** ANY content change (in-place item swap OR wholesale container generation) → the client stalls world-load → AgentServer (chat, 7012) gets zero accepts → and nothing renders. Verified that this is NOT about item-identity resolvability (it breaks even with the now-correct identities) and NOT a structural/length bug (the modified protobuf is valid) and NOT a root↔tails mismatch (the 9 KB tails don't reference the item). The client depends on the exact captured snapshot bytes during load.
2. **There is no server "add item to bag" packet.** Traced every loot take (`0x2043`) in the captures: the server only sends `LootRemoveItems` + looter/visual updates (+ quest updates for quest items). The client adds looted items to the bag **locally** from the `LootBagFull` it already has. (Note: `0x79F8C7A1`/`0x63268258` are **quest** packets — QuestJournalIIR/QuestFullUpdateIIR — NOT ContainerAddItem; the docs note was wrong.)

So the ONLY server mechanism that delivers inventory is the world-entry snapshot, and it must be verbatim during load.

## The approach being tried (UNTESTED) + the WIP state
**Hypothesis:** send a SECOND, DB-built inventory snapshot **AFTER world-load** (after the post-ready burst, once the client is out of the constrained load phase and chat has connected). The world-entry snapshot stays verbatim (chat safe); the post-load one carries the player's real items and may update the bag without re-triggering the load stall.

**Groundwork is committed (a harmless no-op until the send below is wired):**
- `Faolan.Core/Network/NetworkClientSession.cs` — new `PendingPostLoadInventory` byte[] field.
- `Faolan/GameServer/Login/RetailLoginReplay.cs` — `BuildDbInventorySnapshot(...)` (generates the DB snapshot from the verbatim packet via `FullCharSnapshotBuilder.BuildContainers`, keeping prefix+tails), the `InventoryEquippedSlots` map, loads `inventoryRows`, and stashes the built snapshot into `client.Session.PendingPostLoadInventory` at the inventory-snapshot point in `ReplayMapWorldBurst` (world-entry snapshot still goes out verbatim). The stash is built but never sent yet, so current behavior is unchanged.

**REMAINING to make it testable (one edit):** in `Faolan/GameServer/Packets/Packets.cs`, right after `await SendPostReadyBurst(client);` (the existing-character branch, ~line 111), send the stash:
```csharp
if (client.Session.PendingPostLoadInventory != null)
{
    await client.SendAsync(client.Session.PendingPostLoadInventory);
    client.Session.PendingPostLoadInventory = null;
}
```

## How to test the backpack
1. Seed a char's bag with buildable items: `CharacterInventory` rows, `Container=104` (BagKind), `Slot` 2..N, `ItemId` = any item with `Archetype != 0` (e.g. 13817 Dusklight Tunic, 13826 Combatant's Hauberk, 117637 Soothing Remedy). Slot 1 is the bag-first (Royal Signet from `EnsureBeachStarterEquipment`).
2. Server from `Faolan/bin/Debug` (`dotnet Faolan.dll`); drive client: `./Faolan.ClientLauncher/bin/Debug/Faolan.ClientLauncher --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c <name>` (account `faolan`, pw `-`).
3. **Chat OK iff** server log shows `AgentServerListener[0] Client connected`.
4. **See the bag** (headless harness, WORKS): the client auto-opens an item-shop modal that **eats all input** — close it by clicking its X at mouse **(1342, 117)**: `DISPLAY=:1 xdotool mousemove 1342 117 click 1`; then `DISPLAY=:1 xdotool key --window $WID i` opens inventory. Screenshot: `WID=$(DISPLAY=:1 xdotool search --name "Age of Conan" | head -1); DISPLAY=:1 import -window $WID out.png` (`-window root` fails). Bag grid is the right window: `magick out.png -crop 420x420+1000+40 +repage -resize 280% c.png`. Slot 1 = top-left.

## Decision tree for the next session
- **If chat stays connected AND the bag shows the items → DONE.** Commit it (keep the post-load send; it's the backpack delivery). Consider gating it behind the player having buildable inventory, and persist real inventory rows.
- **If chat breaks** (the post-load snapshot got sent before chat connected and stalled the load): move the send to fire on the **chat-connect event** instead — `AgentServerListener` Hello handler → reach the GameServer session via `IGameServerDispatcher.RunGmCommand(clientInstance, ...)` (inject `IGameServerDispatcher` into `AgentServerListener`) and flush the stashed snapshot then.
- **If chat stays but the bag does NOT update** (client ignores a post-load `0x737A6DF9`): the post-load-snapshot approach is dead. Fall back to the open RE in `protocol_containeradditem`: (a) check whether the snapshot is validated against a PRIOR packet (PlayerAgent/CSPlayerAgent character-inventory summary we replay verbatim-as-Strie), or (b) disassemble the client's inventory-load path. This is the genuinely hard, open problem.

## Key memories
`protocol_containeradditem` (the whole inventory-delivery investigation + the snapshot-rejection RE), `protocol_chat_connection_regression` (the verbatim-snapshot fix), `inventory_db_items_work`, `rdb_extraction`, `project_create_editor_flow` (create flow + the deadlock fix), `client_launcher_testing`.
