# Handoff — Loot bag fixes (next: get loot bags fully right)

Three open loot-bag problems, in priority order. The loot bag PIPELINE works (drop → render → loot-all →
grant → fade), and the quest chain depends on it (#261 stage 2 = loot Tuut's bag). These are the remaining
correctness/cosmetic bugs. Verified state 2026-06-21 after #263 (mid-session render) landed.

## How loot bags work today (the parts that work)

- **Drop:** `WorldBroadcaster.ConvertCorpseToLoot(deadNpc)` (world tick, 3s after death) rolls loot
  (`LootResolver`, 50% + a guaranteed drop when the killer has an active LootFromNpc goal — `HasActiveLootGoalFor`)
  and calls `SpawnLootBagAt(mapId, pos, rot, items, cash, allowedLooterHandle, lootSourceName)`. `.spawnbag` GM
  cmd → `SpawnLootBagForPlayer` → same `SpawnLootBagAt` (at the player's feet).
- **Bag entity:** a DECOUPLED `IsLoot` `WorldEntity` with its OWN handle (`NextLootHandle`, base `0x00090000`),
  `LootItems` (template ids), `CashTin`, `AllowedLooterHandle` (the killer), `LootSourceName` (the corpse NPC,
  for the quest-goal match). Tick-expired via `TickExpiry`.
- **Body:** built FROM SCRATCH (#249) by `WorldObjectBuilder.BuildFromScratch` (LootBagFullUpdateIIR 0xF096251E,
  typetag 0xC87A, the live playfield token) from `LootBagTemplate` ("Bag of loot", id 13782, +
  `LootBagKdTreeTail.bin`); `SpliceLootContents` splices the actual items+cash in.
- **Render (#263, FIXED):** `SpawnLootBagAt` sends the `LootBagFullUpdateIIR` then a `NewAllowedLooterIIR`
  (`WorldLoot.BuildNewAllowedLooterBody`) to each in-range viewer — the looter packet is what makes the client
  INSTANTIATE the new world object mid-session (else it only renders on a playfield reload). Verified via
  `.spawnbag` (renders immediately).
- **Loot take:** `LootService.HandleLootAll` (0x2043, loot-all → grant every item, empty + fade), `HandleLootCash`
  (0x2045, auto-loots coins), `HandleLootItem` (single drag → grant one), `HandleCloseLootBag`. Grants ride
  `IEconomyOutbound.GrantItemToBackpack` (live `DispatchAddItem` 0x2020). `AfterLootTake` sends `LootRemoveItems`
  + tears the bag down only when fully empty.

## Problem 1 — corpse bag shows only its spawn EFFECT, not the mesh (Gitea #265)

A kill drop (at `deadNpc.Position`) shows only the spawn particle, not the bag model — looks like nothing
dropped (it IS there: log `dropped bag ... quest=True`, and it's lootable). `.spawnbag` at the player's feet
renders the FULL mesh. The ONLY difference between the two paths is the POSITION fed to `SpawnLootBagAt`
(player feet vs the NPC's death position); body builder, template, transform, token, and the NewAllowedLooter
packet are identical.

- **Most likely:** the NPC's death-spot Z is off the client's ground (the mob chased the player, so its server
  position is where it walked to — see [[npc_spawn_movetask_position]] / #190 position sync), so the mesh clips
  into/under terrain while the particle renders above. Or the corpse `BroadcastEntityDespawn` (fired right after
  the bag spawn in `ConvertCorpseToLoot`) interferes.
- **Next:** log the bag's emitted position vs the player's; compare Tuut's death Z to ground; try snapping the
  bag to the player's Z or a ground-cast Z. `WorldBroadcaster.SpawnLootBagAt` line builds with
  `position`/`Quaternion.Identity` — try `deadNpc.SpawnPosition` (the spawn point, known-good ground) instead of
  the live death position as a quick test.

## Problem 2 — dragging an item moves the WRONG one (NEW, high priority)

Dragging a specific item out of the bag grants/removes a DIFFERENT item than the one clicked. **Root cause
(strong):** an index-space mismatch. `WorldBroadcaster.SpliceLootContents` builds the DISPLAY list skipping
`Archetype == 0` items (`if (item == null || item.Archetype == 0) continue;`) and assigns display `slot`
0..n-1 over only the kept items. But `LootService.HandleLootItem` → `WorldBroadcaster.TakeBagItem(bagHandle,
index)` indexes into the FULL `bag.LootItems` list (which still contains the skipped items). So the client's
display index ≠ the server's `LootItems` index whenever any rolled item has `Archetype == 0`, and the drag
removes/grants the wrong entry.

- **Fix:** make the display index and the take index share ONE ordering. Either (a) drop `Archetype == 0` items
  from `bag.LootItems` at spawn (so `LootItems` == the displayed set), or (b) have `TakeBagItem` map the display
  index through the SAME archetype-filter `SpliceLootContents` uses. (a) is simpler and also fixes Problem 3's
  count. Confirm the client's drag opcode carries the display Pos (it does — `HandleLootItem` reads `itemIndex`).
- Also re-check the drag opcode id: `GameServerListener` dispatches `HandleLootItem` (single drag); #226 calls it
  `TryLootItem 0x2046`, the inline comments say 0x2044 — verify against the dispatch switch.

## Problem 3 — bag window doesn't display ALL items (Gitea #226)

A bag with N items shows N-1 in the window (the client still GRANTS all N — cosmetic). Contents are spliced
into the LootBagFullUpdateIIR via `SpliceLootContents` (`LootBagMessageCodec` + `SnapshotItemBuilder`). Tried
`MaxSize` = item count (truncated by one) then the captured fixed `MaxSize=10` (items at Pos 0..n-1, the decoded
`Chest [N7FU]` bag). Likely related to Problem 2's archetype skip (a skipped item = one fewer shown) OR an entry
byte the client rejects.

- **Next:** decode a real captured loot-bag item entry (the 113-byte ProtoInventoryGameEntry) from
  `Hoxxling_..._login_gameplay` and diff it byte-for-byte against our spliced entry (`SnapshotItemBuilder.
  BuildListEntry`) to find the missing/extra stat. Fixing Problem 2 (a: drop archetype-0 from `LootItems`) may
  resolve the count too.

## Key files

- `Faolan/GameServer/World/WorldBroadcaster.cs` — `SpawnLootBagAt`, `SpliceLootContents`, `TakeBagItem`,
  `TakeBagItems`, `AfterLootTake`, `ConvertCorpseToLoot`, `BagSourceName`.
- `Faolan/GameServer/World/LootService.cs` — `HandleLootAll` / `HandleLootItem` / `HandleLootCash` / close.
- `Faolan/GameServer/World/WorldLoot.cs` — looter / LootRemoveItems / open-window body builders.
- `Faolan/GameServer/Spawn/LootBagMessageCodec.cs` — the ProtoLootBagIIR contents codec (cash + items + looters).
- `Faolan/GameServer/Economy/LootBagTemplate.cs` (+ `LootBagKdTreeTail.bin`), `Faolan/GameServer/Spawn/WorldObjectBuilder.cs`.
- `Faolan.Tests/`: `LootTests`, `LootBagMessageTests`, `WorldLootTakeTests`, `LootBagTemplateTests`.

## Captures

- `Captures/Hoxxling_mage-heraldofxotli_login_gameplay.tshark.json` — gameplay with loot (21× LootBagFullUpdateIIR);
  the real bag item entries to diff against live there. Loot-all finish decoded from its frame ~4470.
- `Captures/Blablatest_..._casilda_quest_first_stage.tshark.json` — the kill→loot-bag mid-session SPAWN (fr1358:
  LootBagFullUpdateIIR + NewAllowedLooterIIR), the #263 ground truth.

## Related

Gitea #265 (mesh render), #226 (display + single-drag remove), #261 (quest chain — stage 2 loots Tuut's bag).
Memories: [[loot_bag_midsession_render_263]], [[protocol_loot_interaction]], [[npc_spawn_movetask_position]].
