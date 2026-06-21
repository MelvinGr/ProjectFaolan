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

## Problem 1 — corpse bag shows only its spawn EFFECT, not the mesh (Gitea #265) — FIX APPLIED, awaiting in-client test

A kill drop (at `deadNpc.Position`) shows only the spawn particle, not the bag model — looks like nothing
dropped (it IS there: log `dropped bag ... quest=True`, and it's lootable). `.spawnbag` at the player's feet
renders the FULL mesh. The ONLY difference between the two paths is the POSITION fed to `SpawnLootBagAt`
(player feet vs the NPC's death position); body builder, template, transform, token, and the NewAllowedLooter
packet are identical.

- **Most likely cause:** the NPC's death-spot Z is off the client's ground (the mob chased the player, so its
  server position is where it walked to — see [[npc_spawn_movetask_position]] / #190 position sync), so the mesh
  clips into/under terrain while the particle renders above.
- **Fix applied:** `WorldBroadcaster.ConvertCorpseToLoot` now resolves the killer (`LastAttackerHandle`) in the
  zone and drops the bag at the KILLER's live position/rotation when it's a player — the proven-good `.spawnbag`
  ground — instead of `deadNpc.Position`. Falls back to the death spot when the attacker can't be resolved. The
  log line now prints both the drop position and the death spot so the in-client test can confirm the cause.
- **Verify in-client:** kill a mob that chased you, confirm the bag MESH (not just the particle) renders. If it
  still fails at the player's position, the cause is NOT the death-spot Z — re-investigate the corpse
  `BroadcastEntityDespawn` (fired right after the bag spawn) interfering, or a body/token issue.

## Problem 2 — dragging an item moves the WRONG one (NEW, high priority) — FIXED in code, awaiting in-client test

Dragging a specific item out of the bag grants/removes a DIFFERENT item than the one clicked. **Root cause:**
an index-space mismatch. `SpliceLootContents` built the DISPLAY list skipping `Archetype == 0` items and
assigned display `slot` 0..n-1 over only the kept items, but `TakeBagItem(bagHandle, index)` indexed into the
FULL `bag.LootItems` list (which still held the skipped items). So the display index ≠ the `LootItems` index
whenever a rolled item had `Archetype == 0`, and the drag removed/granted the wrong entry.

- **Fix applied (approach a):** new `WorldBroadcaster.ResolveBuildableLoot` resolves the rolled templates to the
  buildable set (in the item table AND `Archetype != 0`) ONCE in `SpawnLootBagAt`; that single filtered list
  seeds BOTH `bag.LootItems` AND the spliced display (`SpliceLootContents` is now synchronous and takes the
  pre-resolved `(TemplateId, Item)` list). So the display Pos == the `LootItems` index by construction — the
  single-drag take can't desync. This also fixes Problem 3's off-by-one count (a skipped item no longer hides a
  displayed slot).
- **Opcode verified:** the single-drag is `TryLootItem = 0x2046` (`Opcodes.cs`), dispatched via
  `GameServerOpcodes.TryLootItem` → `LootService.HandleLootItem`, which reads the display `itemIndex`. The
  WorldBroadcaster comment was already correct (0x2046); no stale 0x2044 remained in the loot path.
- **Verify in-client:** drop a multi-item bag (`.spawnbag` or a kill) where at least one rolled template is
  Archetype-0, drag a specific item, confirm THAT item lands in the backpack and the window re-indexes correctly.

## Problem 3 — bag window doesn't display ALL items (Gitea #226) — likely fixed by Problem 2, verify in-client

A bag with N items shows N-1 in the window (the client still GRANTS all N — cosmetic). Contents are spliced
into the LootBagFullUpdateIIR via `SpliceLootContents`. The strong hypothesis was Problem 2's archetype skip (a
skipped item = one fewer shown). The Problem 2 fix removes Archetype-0 templates from BOTH lists at spawn, so a
bag now displays exactly the items it holds — this should resolve the count.

- **Verify in-client:** drop a bag with several buildable items, confirm the window shows ALL of them.
- **If it still shows N-1 with all-buildable items:** the cause is NOT the archetype skip — decode a real
  captured loot-bag item entry (the 113-byte ProtoInventoryGameEntry) from `Hoxxling_..._login_gameplay` and
  diff it byte-for-byte against our spliced entry (`SnapshotItemBuilder.BuildListEntry`) for a missing/extra stat.

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
