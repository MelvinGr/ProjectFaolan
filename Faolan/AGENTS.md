# The server host: listeners and the service graph

**Listeners** are partial classes split across several files; inspect every partial before changing behavior.
`GameServerListener`'s partials are named by role: `.Inbound` (the routing switch and thin `Handle*` handlers) and
`.Outbound` (behavior-named senders such as `SendGuildTag`/`SendGroupMembership`, never opcode-named). Static senders
several services share (`SendStatUpdate`, `BuildMapChange`, `SendLearnSpell`, ...) live in `Wire/GameWire.cs`, and the
tier's sender endpoints in `Wire/GameEndpoints.cs`, so no service reaches back into the listener for them.
`ChatServerListener` follows the same `<Listener>.<Role>.cs` naming. A partial-class file keeps
`namespace Faolan.GameServer` regardless of its folder (the `// CheckNamespace` suppression), so its folder is
organizational only; that suppression is for partials only, and a non-partial type takes its folder's namespace.

Domain logic lives in composed service folders beside the listener (`Apprentice/`, `Behavior/`, `Combat/`,
`CreateFlow/`, `Dialogue/`, `Economy/`, `Feats/`, `Gm/`, `Inventory/`, `Progression/`, `Quest/`, `Scripting/`,
`Session/`, `World/`). Examples: `Session/WorldEntryService` (the ready bursts, the snapshot, the spawn poll),
`Gm/GmCommandService` (+ its `.Retail` partial), `Behavior/BehaviorService` (the client's action switch - the whole
`BehaviorControlCenterIIR` family: attack, use dynel, use character, equip, mount).

**The GameServer's services are container-registered** (`Faolan/GameServer/GameServerRealmServices.cs`,
`AddRealmGameServer`), not constructed by hand; the listener takes them by constructor injection.
- **Register a service where its dependencies already are.** MS.DI resolves by type, so the file's order is
  documentation only, but it is the one place the graph's layering is legible: keep it in dependency order.
- **A missing registration is not a build error.** `GameServerCompositionTests` is the gate: `ValidateOnBuild` walks
  every constructor and names every unsatisfied dependency without invoking a factory. Run it rather than booting.
- Every composed service takes a bare, realm-tagged `ILogger` (`...GameServerListener-<realmId>`), registered by
  `AddRealmGameServer` and therefore scoped to a process that hosts a realm. Any other container-resolved type asking
  for a non-generic `ILogger` would silently inherit that category; the same test fails if a listener does.
- **The CS tier is registered the same way** (`Faolan/CSPlayerAgent/CsPlayerAgentServices.cs`,
  `AddRealmCsPlayerAgent`, gated by `CsPlayerAgentCompositionTests`), with two differences: its services take
  `ILogger<CsPlayerAgentListener>` (a bare `ILogger` would inherit the GameServer's realm tag), and its
  `ConnectionRegistry` is a keyed singleton (`[FromKeyedServices(CsPlayerAgentServices.ConnectionsKey)]`), because the
  GameServer registers the unkeyed one for the world connections and the two are different sets.

**Prefer composed collaborators over growing the listener.** Domain logic belongs in folder-namespaced service classes
that take their dependencies by constructor and the client per call (`CombatService`, `WorldBroadcaster`,
`WorldSimulation`, `EquipmentService`, `ConversationService`, `ShopService`, `TradepostService`, ...). The listener holds
the ones it routes to (`_shop`, `_bank`, ...) and forwards. Rules for the service graph:
- **Never pass the listener, and never hand out `this`.** A service that needs a cross-cutting outbound operation
  reaches it through a narrow port. No port is implemented by the listener: each is its own container-registered class,
  injected into the services that need it (`WorldOutbound`, `EconomyOutbound`, `TradeOutbound`,
  `QuestShareOutbound`, `QuestOutbound`, `ApprenticeOutbound`, `LevelUpOutbound`, `EnvironmentOutbound`,
  `TravelOutbound`, `SpellweaveOutbound`, `SpellEffectOutbound`). The listener implements one interface,
  `IGameServerProvider` (the cross-listener dispatcher seam), and passes `this` only to `IGameServerDispatcher.Register`.
  A single shared operation can be a `Func<...>` seam (`ClientForCharacter` into `GuildService`); once several services
  share a cluster of operations, collapse them into one named interface.
- **Check what an operation reads before calling it listener state.** Most wire code needs only "the client for this
  character id", which `ConnectionRegistry` (`Faolan.Core.Network`) answers: `ServerBase` is handed one and delegates
  `NetworkClients`/`ClientForCharacter` to it, so a wire class can take the registry instead of the listener. Likewise
  the energy loop's state is on `INetworkClientSession`, and a pet summon is broadcaster and simulation work
  (`EnergyService`, `PetService`, `TeleportService` are their own collaborators).
- **Construction cycles.** First look for a move: a cycle is usually between the class an operation sits in and its
  answer, not between the operation and its answer, and moving the operation to its own class built after the other
  side turns the seam into an ordinary constructor argument (`KnockbackService`, `WorldEntryService`). A cycle that
  survives is cut by a single-operation bound seam (`EconomyOutbound.BindCollectGoals`, `LevelUpOutbound.BindReseed`,
  `WorldSimulation.Start(token, output)`), never by a lazy `Func<>` closing over a not-yet-assigned field.
- **Split a large class by the question each member answers**, not by state-versus-wire. `WorldBroadcaster` answers
  *who can see what* and the spawn/despawn/move/entry-burst wire that follows; interacting with a world object
  (`WorldObjectService`), what an NPC does (`NpcBroadcaster`), what dropped (`LootBroadcaster`), what the client is told
  about quests (`QuestBroadcaster`, `QuestOutbound`) and the shove (`KnockbackService`) are separate classes.
  `WorldSimulation` answers *what is in the world and what it is doing this tick*; `PlayerDeathService`,
  `BuffTickService` and `NpcCombatService` are separate. Every cut must leave the dependency one-way. Prefer the cut
  that lets a caller drop the big class entirely. Subject is the first approximation; the question is the test.
- **Inject the state, not its owner.** Services that read the world take `WorldState` and `PlayerRegistry` (the
  connection->entity map; it derives from `ConcurrentDictionary` so the container can name it) rather than the
  simulation, which keeps the dependency one-way. Each binds `IWorldOutbound` at `Start`, as the simulation does.
- Two traps neither the compiler nor the tests catch when moving a port: `((IFoo)this)` still compiles after `IFoo`
  leaves a non-sealed class and throws `InvalidCastException` at runtime (grep every self-cast against the actual
  interface list), and a field read in a constructor before its own assignment line is silently null and captured
  forever (audit the constructor). See the agent memories `interface_removal_casts` and `ctor_use_before_assign`.

**Each zone owns its state through a `WorkLoop`** (`Faolan.Core/Helpers/WorkLoop.cs`), the way retail gave each
playfield its own server. Every client's packets arrive on its own connection thread, so without an owner two players'
handlers would interleave inside one zone. The rules:
- An in-world player's packets, the zone's tick (`WorldSimulation.TickZone`) and the world half of a disconnect run on
  that zone's loop. Before world entry nothing owns the player; `WorldEntryService` moves the entry onto the zone it
  resolves (`WorldSimulation.EntryZone`) before placing them.
- Anything else that touches a player's or an NPC's world state goes through `WorldSimulation.RunForPlayer` (another
  listener's call) or `RunInZoneOf` (a delayed action, a per-player loop; it does nothing once they have left).
- Work that outlives its item starts detached: `Task.RunAfter` or `WorkLoop.Spawn`, never `_ = SomethingAsync()` or a
  bare `Task.Run` from a handler. Started attached, it inherits the loop's "running here" mark, and its own `Run` calls
  execute inline beside whatever the zone is doing by then.
- **No loop waits on a zone's work**: from another zone or the CS tier, a call into a zone is queued, not awaited.
  The CS tier has one loop of its own (`CsPlayerAgentServices.LoopKey`), which a zone may await; a call it makes into
  a zone is queued. Two loops that could each wait on the other would deadlock both.
- A sweep the tick runs takes the `Zone` it ticks and walks `PlayerRegistry.In(zone)`, not every player.

Listeners derive from `RpcServer` or `ChatServerBase` (both `ServerBase`, in `Faolan.Core.Network/Server.cs`);
per-connection state lives on `INetworkClient.Session`. Cross-listener actions go through dispatcher seams
(`IChatServerDispatcher` for chat and system messages, `IGameServerDispatcher` for world actions), never direct
references.

**Repository slices.** `IDatabaseRepository` is the composite of per-domain slices
(`Faolan.Core.Database/AGENTS.md`):
- **A service takes the slices it calls, not the composite**: one to three slices as separate constructor parameters
  (`_catalog`, `_inventory`, `_world`, ...). A consumer of four or more slices (`WorldEntryService`, `GmCommandService`,
  `TradepostService`, ...), or one that hands the repository to a helper needing the composite, keeps
  `IDatabaseRepository`.
- `ConfigureFaolanDatabase` forwards every slice to the one singleton (`AddRepositorySlices` reads them off the
  composite's interface list), so a new slice needs no registration of its own.

**World invariants** that compile and pass tests when broken:
- **Never send a despawn (`n3ToClientQuit`) for a handle the recipient was never sent**: the client crashes on a null
  dynel. Every despawn path is gated on `viewer.Knowledge.Known.Remove(handle)`.
- **A player's wire handle, `Account.ClientInstance` and `Character.Id` are one number** by construction. NPC
  handles are spawn handles, unrelated to any character id.
- **Resolve a zone with `ZoneForEntity`, never `WorldState.TryGetZone(mapId)`**, which returns only the default
  instance. Tortage (4010), where most in-client testing happens, is one of the four `MapInstanceMode.Solo` maps, so
  a `TryGetZone` lookup there resolves nothing, and a guard that passes on an unresolved lookup silently disables its
  feature. A gameplay refusal logs at Information, so a play session read back from `faolan.log` shows it.
  `MeleeReachTests` is the pattern for testing the instanced case.
- **An entity spawned straight into a player's zone** (a pet, a GM spawn, any summon) takes `player.InstanceId` and
  joins the viewer's `Known` set at spawn, or the client drops it: the zone token would name the default instance,
  and the follow `MoveTask` is `Known`-gated.
- **Buffs have two registries behind one send.** A buff on the player lives in the player entity's `ActiveBuffs`
  (`BuffService`); a mob's registry is owned by the world tick (`BuffTickService.ApplyEffectToEntity`). Whether a
  record belongs in the player's registry is decided by whose bar it lands on, never by `applyTo == applyFrom`: that
  test reads as a correctness guard but excludes every effect a mob lands on the player, which would then get no
  dedup, no expiry and no end twin.
- **A loop or delayed action that outlives its packet ends on `client.IsStopped`** (the energy mirror, a cast timer,
  a `Task.RunAfter`). `Session.Account` stays set after a disconnect, and a send to a stopped connection is dropped
  rather than thrown, so neither tells a loop its player has gone.
- **When the live simulation mirrors a persisted value, check who writes it on every path** (world entry, level-up,
  GM commands, players and NPCs), not only how it is read: an unset mirror reads as 0 and resolves silently. A throw
  in a derived recompute is usually correct; trace the bad input instead of guarding the throw.
