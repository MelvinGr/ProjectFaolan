# Server architecture roadmap — from replay bridge to a live MMORPG server

> **Thesis:** Faolan has solved the hard, AoC-specific part — the **protocol**. What's missing is the
> **simulation layer**. Today the GameServer is a *per-session, event-driven replay/construct bridge* with no
> shared world model, no game loop, and no cross-player visibility. Becoming a "proper" server means putting an
> authoritative, ticking, in-memory world on top of the wire layer that already works.

Written 2026-06-11. Grounds: the GameServer/AgentServer code as it stands; see also
`world_packets_construct_not_replay`, `protocol_stats_and_combat`, `docs/chat.md`, `docs/quests-and-combat.md`.

> ## PROGRESS (2026-06-11) — the milestone-1 foundation is in (harness-verified; the in-client payoff is pending)
> - **In-memory world model** ✓ `WorldEntity`/`Zone`/`WorldState`; players registered on entry, removed on
>   disconnect. **`WorldEntity` is the single source of truth for live transform**; `Character` is the persisted seed.
> - **Interest management** ✓ `InterestManager` (spawn/move/despawn diff, unit-tested) + the cross-player broadcast
>   (capture each player's spawn body, re-frame per recipient, forward moves) — **NEEDS 2-client in-client check**.
>   **Despawn (`n3ToClientQuit` 0x04B82B2D) now DECODED** (2026-06-11, all 3271 records): 21 B,
>   `[len=0x11][04b82b2d][ack=0000c350][handle = entity to remove][0x01 marker][u32 playfield-instance token]`. The
>   token is *not* a handle — it's the per-playfield id the server announces at world-entry (`0x1A07` init + once in
>   the world-burst playfield header) and stamps into every despawn; a server-global monotonic counter (two pools:
>   `0x004f–0x0050xxxx` / `0x01eaxxxx`), constant per session, new on map change. **Buildable:** we replay the burst
>   verbatim, so the client already learned the captured token (beach = `0x004fe428`) — a despawn reuses it.
>   **Despawn now WIRED** (`WorldDespawn.BuildBody` + `SendWorldDespawn`): emitted on disconnect
>   (`BroadcastPlayerDespawn` in `ClientDisconnected`) and on range-out (the `Despawn` branch of `DispatchInterest`).
>   The playfield token is extracted at entry from the `0x737A6DF9` snapshot we send (framed by `000000BF`…`0000040B`,
>   `WorldDespawn.TryExtractPlayfieldToken` → `Zone.PlayfieldToken`), so it matches what the client learned by
>   construction. Every zone is also minted a token by a `WorldState` monotonic allocator (`NextPlayfieldToken`) on
>   creation as a fallback (the burst value overrides it for a replayed playfield); this allocator becomes the
>   authoritative source once we GENERATE playfields and write the token into the generated entry packets. 6 unit
>   tests. Still **2-client in-client gated** on the one open question: whether the client enforces the token match or
>   removes regardless.
> - **Persistence** ✓ event-driven (disconnect + teleport + `.save` flushes live world state) + a 5-min autosave
>   timer. No game loop needed for it.
> - **Game loop** ✓ a **10 Hz base tick** (`GameTickLoop`) measuring real wall-clock dt. Continuous systems run every
>   tick with that dt (`WorldState.Tick` — respawn now, AI later); rate-based systems run via a per-behaviour
>   `TickAccumulator` at their own interval, dt-scaled (vitals regen at 1 Hz, pushes only on change). A long stall
>   fires a behaviour once with the whole elapsed (no catch-up storm). Ready for ~10–20 Hz AI/movement.
> - **NPC death → respawn** ✓ NPC vitals live on `WorldEntity` (seeded level-scaled at zone load, world-authoritative).
>   Combat routes NPC damage through the model (`ApplyNpcDamage`/`ResolveNpcVitals`); on death the NPC is marked dead +
>   despawned for nearby players (`BroadcastNpcDespawn`), and `WorldState.Tick` counts down `RespawnDelaySeconds` and
>   revives it at its spawn point, re-rendered via `BroadcastNpcRespawn` (the NPC's `NpcSpawnBuilder` body). 5 unit
>   tests. Visual confirmation is 2-client/in-client gated; the lifecycle logic is harness-verified.
> - **XP on kill** ✓ killing a model NPC awards level-scaled XP (`CombatResolver.ResolveKillXp`) to `Character.Experience`
>   and pushes an `Exp` (0x0034) stat update via `0x96B8DC59` so the bar moves; XP persists via the usual save points.
> - **Loot on kill** ✓ a kill may drop a loot bag (`LootResolver` coin-flip): a world object (`LootBagFullUpdateIIR_c`
>   0xF096251E) built from a captured "Bag of loot" row at the death spot with a fresh handle, added to the zone +
>   spawned for nearby players, and expired off the tick (`WorldState.Tick` → `LootExpired` → despawn + remove). 4
>   tests. **Looting the bag (open → contents → take) is the remaining piece** (`lootbag_open` 0x642CD3D6 →
>   `LootRemoveItemsIIR_c` 0x70010162 → grant item) — that interaction protocol needs decoding (gated). Level-up
>   (XP curve → `Level++` + `MaxStats` recompute) is the other open progression piece.
> **NEXT:** loot open/take interaction + level-up; then NPC AI movement (the pinned MoveTask patrol grammar) so the
> tick drives motion and the burst becomes a pure seed — where the loop and "construct, don't replay" meet. Then quests.

## What we already have (strong foundation)

- **Protocol mastery — the moat.** Framing, opcodes, and **byte-exact** world packets: spawn (`FA015807`),
  MoveTask (`96C46740`), world objects (`66AEDD50`/`F096251E`/`33A56FB0`/`6BD56736`/`1FD6412B`), inventory
  (`737A6DF9`), the IIR record family, chat. Most emulators never get the wire format right; this one did.
- **DB as source of truth** — EF Core, pooled `DbContextFactory`, repository, short-lived per-unit-of-work
  contexts. Structured tables: `NpcSpawns`, `WorldObjects`, `MapWorldBurst(Entity)`, `Items`, `Spells`,
  `Abilities`, `Characters`/inventory/equipment/scaffold. Populated by `Faolan.Importer` (captures + client RDB).
- **World rendering** — per-map world-burst replay renders a world with NPCs + objects; the player's own spawn is
  fully **DB-generated** (identity, race/sex/level, vitals, equipment, scaffold frame).
- **Construct-don't-replay machinery** — `NpcSpawnBuilder` / `WorldObjectBuilder` / `MoveTaskBuilder` build packets
  byte-exact; a coverage gate (`Faolan.Importer --burst-coverage`, ~29%) tracks how much of each map is rebuildable
  from tables vs still replayed.
- **Combat scaffolding** — `CombatState` (in-memory vitals per handle), `CombatResolver` (provisional damage), NPC
  max HP now seeded from the spawn's `CharacterStats.health`.
- **Chat** — AgentServer channels (`0x0041`) + proximity local say (`0x0022`). See `docs/chat.md`.
- **Login/auth + the 5-connection handshake** (UniverseAgent/PlayerAgent/GameServer/AgentServer/CsPlayerAgent).

## The architectural gap (the core thing)

From the code as it stands:

- **No game loop / tick.** `Server<...>` is purely reactive — work happens only when a packet arrives. There is no
  per-zone `Update(dt)`. Nothing moves, regenerates, respawns, or thinks on its own.
- **No shared world model.** State is **per-session**: each connection has its own `Session.Character`, its own
  `SpawnedNpcNamesByHandle` / `SpawnedNpcMaxHealthByHandle`, its own `CombatState`. There is no per-map registry of
  "all entities here". The AgentServer even loads a *separate* `Character` per connection (why say-proximity is
  computed GameServer-side — only it has live positions).
- **No cross-player visibility.** A player's Move is echoed back to **only the mover**
  (`LoginWorldReceiver(client)`; the handler comment says *"single client for now"*). Other players arrive as
  **static burst spawns** that never update. Two logged-in players don't see each other move/fight.
- **NPCs are inert.** Replayed once from the burst; MoveTask is static; no AI, no aggro; combat only **acks the
  player's swing** (mobs don't fight back, die→respawn, or path). `CombatResolver` damage is provisional.

So today it behaves like **single-player instances stitched from captures**, not a live shared world.

## What's needed, in priority order

### 1. Authoritative in-memory world model + game loop (the keystone)
- A **`Zone` / `WorldInstance`** per map holding an **entity registry**: every player and NPC as a live
  `WorldEntity { handle, position, rotation, vitals, state, mapId }`. Promote today's per-session
  `Character` / `CombatState` / `SpawnedNpc*` maps into this shared, server-owned model.
- A **fixed-timestep tick** per zone (~10–20 Hz) driving movement, NPC AI, regen, buff/DoT ticks, respawn/spawn
  timers, leashing. The single biggest missing piece — everything dynamic hangs off it.

### 2. Interest management + spatial broadcast
- A spatial index per zone (a uniform grid is plenty) + a per-player **"known entities"** set. On
  move/spawn/despawn/act, broadcast to *nearby* players only, emitting enter/leave (`FA015807` spawn / quit) as
  players cross interest boundaries. This is what makes it multiplayer — and the byte-exact builders to emit those
  packets already exist. (The `SayRecipients` proximity helper added for chat is the seed of this.)

### 3. Server-owned entities (finish "construct, don't replay")
- To *own* entity state you must **generate** spawn/move packets from the world model, not replay capture bytes —
  exactly the pinned NPC-scaffold lift + MoveTask/PatrolPath work (`world_packets_construct_not_replay`). The burst
  becomes a *seed* for the zone's entity list; the live model then takes over.

### 4. NPC AI + a real combat system
- AI states (idle/patrol/aggro/chase/leash/flee), threat tables, spawn points + respawn timers. Combat: real
  damage/mitigation, abilities/spells with cooldowns + resources, death → despawn + loot + XP + respawn. The
  vitals / ApplySpell / DamageResult packets exist; the missing part is the *logic on the tick*. (NB: the
  NormalAttack BehaviorControlCenter FSM also needs the NPC's own counter-attack ack — see `docs/quests-and-combat.md`.)

### 5. Progression + quests
- XP / leveling (`CharacterMaxStats.CreateDefaults` is a stub). The quest/tag state machine — the **same Lua-VM**
  effort dialogue branching needs (pinned in `protocol_conversation_routing` / `docs/rdb-extraction-plan.md`).

### 6. State/persistence discipline & scaling (later)
- Cleanly split **transient world state** (positions, vitals, buffs — live in the zone model) from **persisted
  state** (inventory, equipment, stats, quest flags — periodic save points). Then per-zone threading / tick budgets
  as entity counts grow.

## Pragmatic first milestone

Build **one zone as a live model**: a `Zone` with an entity registry seeded from the existing burst, a tick loop,
and interest-managed Move broadcast so **two players see each other move**. That single step flips the architecture
from "replay bridge" to "live simulation" and reuses everything already built (the packet builders, live positions,
the proximity logic from chat). Everything else (NPC AI, combat, respawn) then hangs off that tick + registry.

## Dependency summary

`construct-don't-replay` (own the entities) → world model + tick (own the simulation) → interest management
(make it multiplayer) → AI/combat/quests (make it a game). The protocol foundation — the expensive 80% most
projects never finish — is done; the rest is a well-understood MMO-server simulation layer.
