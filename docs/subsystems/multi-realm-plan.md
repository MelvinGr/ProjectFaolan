# Multi-realm (multiple game servers) — architecture plan

Project Faolan today runs **one** game world. This plans the path to **N realms** (AoC "servers" — Crom/Fury/…),
each an isolated world a character belongs to. The good news: the wire + data model is already realm-aware; the
work is wiring the existing scaffold to N worlds and scoping the dynamic data per realm.

## Current state (realm-AWARE, single-INSTANCE)

Already realm-ready:
- **`Realm` table** — `Id`, `Name`, `Country`, `Type`, `OnlineStatus`, `LoadStatus`, and crucially its own
  **`IpAddress` + `Port`** (so a realm can live on any host:port).
- **`Character.RealmId`** — every character is FK'd to a realm.
- **`SendRealmList`** (PlayerAgent/Packets.cs) — already **loops all DB realms** (count-prefixed per-realm record);
  sending N realms is coded, the DB just has 1 row (`Project Faolan`, 127.0.0.1:7014).
- **`RequestCharRealmData` / `SendCharacterRealmData` / `CreateCharacter(accountId, realmId)`** — the realm-select
  wire flow exists.

Single-instance hardcoding (the gaps):
1. **`Program.cs` builds ONE `GameServerListener`** from `GetRealms().FirstOrDefault()` (its port), with ONE
   `WorldSimulation`. No per-realm loop.
2. **Enter-world handoff advertises a static `Program.GameServerPort`** (Inbound.cs:1264), not the selected
   character's `Realm.Port`/`IpAddress`.
3. **Char list is not realm-scoped** — `SendSmallCharList` sends all account characters regardless of realm.
4. **Dynamic data is GLOBAL** — `Guild`, `TradepostListing` (AH), `Mail` have **no `RealmId`**; guilds/AH/mail are
   shared across (what would be) realms. Character-owned data (`CharacterInventory`, quests, …) is transitively
   realm-scoped via the character.
5. **Chat + guild services are global singletons** — one `AgentServerListener` (chat) and one
   `CsPlayerAgentListener` (guilds/teams) for the whole process; no realm dimension.

## Target architecture

- **N game worlds, one per realm**, each isolated: its own `WorldSimulation` / `WorldState` / zones / interest /
  persistence. A player on realm A never sees realm B's entities, chat, guilds, or AH.
- **Shared login/routing tier** (`UniverseAgent` + `PlayerAgent`): authenticates once, shows the realm list, and
  routes each player to their selected character's realm game service by `Realm.IpAddress:Port`.
- **Realm-local dynamic data**: characters (already `RealmId`), guilds, AH listings, mail, chat channels.
- **Shared static content** (realm-agnostic reference): maps, NPC/item/spell templates, RDB assets. One copy,
  read by every realm.

## Deployment model — CHOSEN: (B) process-per-realm

Each realm is its **own Faolan game process** bound to its `Realm.IpAddress:Port` (any host), with a **shared
login/routing tier** directing players and a **shared `faolan.db`** (per-realm DBs are a later option, P5). True
isolation + horizontal scale (a realm can live on its own machine; a crash takes only that realm down).

Considered and rejected for now: (A) in-process multi-host (one process loops realms) — simpler ops but shares
process/resources and couples realm lifecycles. `Realm.IpAddress` already exists precisely to enable B.

### Process topology under B

- **Login/router process (shared, one):** `UniverseAgent` (7010, the only fixed port) + `PlayerAgent` — auth, the
  realm list, the character list, and the **EnterWorld routing** that hands the client to the selected character's
  realm. Owns NO world. Reads `faolan.db` for accounts/characters/realms.
- **Realm game process (one per realm):** `GameServerListener` + `WorldSimulation` **+ the realm-local chat/guild
  services** (`AgentServerListener` chat, `CsPlayerAgentListener` guilds/teams) — because chat and guilds are
  realm-local, they move OUT of the shared tier and INTO each realm process. Bound to that realm's `IpAddress:Port`
  (+ the per-realm chat/cs ports); reads its own realm id from config/CLI (`--realm <id>`). Shares `faolan.db`,
  scoping every dynamic query by realm.

So `Program.cs` gains a **role**: `--role login` (or default) runs the router tier; `--realm <id>` runs a realm game
host. One login process + N realm processes, all against the shared DB.

## Phased plan (each phase test-gated; no wire regressions)

- **Phase 0 — Realm registry.** Mostly done. Seed >1 `Realm` row; a GM/config way to add realms. `SendRealmList`
  already serves them. **Verify in-client** that the client accepts a >1 realm list + per-realm char counts (we've
  only ever sent 1 — retail-leading: confirm the multi-realm list bytes against a capture/Ghidra before relying on it).

- **Phase 1 — Realm-routed handoff + realm-scoped char list** *(client-free to build; the smallest visible slice)*.
  Replace the static `Program.GameServerPort` (Inbound.cs:1264) + the `Configuration.ServerIpAddress` handoff sends
  with the *selected character's* `Realm.IpAddress:Port` — look the character's realm up at `EnterWorld` and thread
  it into the GameServer/CsPlayerAgent/AgentServer handoff addresses. Scope `SendSmallCharList` (and the char-realm
  counts) to the client's selected realm. Two realm rows pointing at the SAME process proves routing before the
  process is actually split.

- **Phase 2 — Process role split** *(the heart of B)*. `Program.cs` gains a role: `--role login` (default) starts
  ONLY `UniverseAgent` + `PlayerAgent` (+ the DB); `--realm <id>` starts a realm game host — `GameServerListener` +
  `WorldSimulation` + the realm-local `AgentServerListener` + `CsPlayerAgentListener` — bound to that realm's ports,
  with the realm id threaded through the whole world (simulation, dispatcher, MCP). The login tier no longer owns a
  world; the realm host no longer owns login. Run one login process + one realm process locally to validate.

- **Phase 3 — Realm-scope the shared dynamic data.** Add `RealmId` to `Guild`, `TradepostListing`, `Mail` (+ any
  world-global dynamic table); scope every query by realm; EF migration backfilling existing rows to realm 1. Now
  that chat/guild services live IN the realm process (P2), they are realm-scoped by construction — but the DB rows
  they read/write must carry `RealmId` so two realm processes on the shared DB don't cross-read.

- **Phase 4 — Isolation hardening + N processes.** Run N realm processes against the shared DB; audit for
  cross-realm leakage (dispatch, name/handle lookups, DB queries missing the realm filter); confirm a player on
  realm A never sees realm B. Config/orchestration for launching the fleet (one login + N realm hosts).

- **Phase 5 (optional) — Per-realm DBs.** If shared-DB contention or stronger isolation warrants it, give each realm
  process its own DB (static content cloned/shared read-only, dynamic per-realm) instead of `RealmId` columns.

## Retail constraints (retail is leading)

- The realm-select flow (`RequestCharRealmData` → realm list → char-realm-data) already matches retail for 1 realm.
  **Before advertising >1 realm**, confirm the multi-realm list record format + the char-per-realm counts against a
  capture (or the client reader in Ghidra) — a guessed list that the client rejects stalls at server-select.
- The enter-world handoff (0x2002) carrying a per-realm IP:Port is exactly what `Realm.IpAddress:Port` is for;
  confirm the client honors a non-default host:port in the handoff.

## Risks / unknowns

- **Multi-realm list bytes** not capture-verified (only ever sent 1 realm) — Phase 0 gate.
- **Chat/guild/AH realm-scoping is the invasive part** (touches `AgentServer` / `CsPlayerAgent` / economy) — the
  bulk of Phase 3.
- **DB model**: shared `faolan.db` + `RealmId` columns (simplest, recommended) vs per-realm DBs (stronger isolation,
  more ops). Recommend shared + `RealmId`.

## First concrete step

Phase 1 on two realm rows that both point at the current single world: pick a character on realm 2 → the handoff
routes to realm 2's port. Proves the routing + char-list scoping with zero world-isolation work; Phase 2 then
splits the worlds behind that routing.
