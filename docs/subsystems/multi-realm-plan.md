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

## Deployment model — the one real decision

**A) In-process multi-host (RECOMMENDED first).** One Faolan process; `Program.cs` loops realms and spins a
`GameServerListener` + `WorldSimulation` per realm on its own port; shared `faolan.db`; chat/guild services gain a
realm dimension. Simplest ops (one process, one DB); realms share process resources and a crash affects all.

**B) Process-per-realm (scale-out).** Each realm is its own Faolan game process bound to its `Realm.IpAddress:Port`
(possibly on different machines); a standalone login/routing tier directs players; shared DB or per-realm DBs. True
isolation + horizontal scale; more moving parts.

`Realm.IpAddress` already enables B. **Recommendation:** build A cleanly — every world path *parameterized by realm
id* rather than assuming a singleton — so B later is a **deployment change, not a rewrite** (split the process on
realm-id boundaries the code already respects).

## Phased plan (each phase test-gated; no wire regressions)

- **Phase 0 — Realm registry.** Mostly done. Seed >1 `Realm` row; a GM/config way to add realms. `SendRealmList`
  already serves them. **Verify in-client** that the client accepts a >1 realm list + per-realm char counts (we've
  only ever sent 1 — retail-leading: confirm the multi-realm list bytes against a capture/Ghidra before relying on it).

- **Phase 1 — Realm-routed handoff + realm-scoped char list.** Replace the static `Program.GameServerPort` in the
  enter-world handoff with the *selected character's* `Realm.IpAddress:Port` (look the character's realm up at
  EnterWorld). Scope `SendSmallCharList` to the realm the client selected. *Smallest visible slice:* two realm rows
  pointing at the same world proves routing before the worlds are actually split.

- **Phase 2 — Per-realm world instances.** `Program.cs` loops `GetRealms()` → one `GameServerListener` +
  `WorldSimulation` per realm on its port. Introduce a `realmId → listener/simulation` registry; key the
  `IGameServerDispatcher` + MCP live tools by realm. Each world loads the shared static content but keeps its own
  live sessions/zones.

- **Phase 3 — Realm-scope the dynamic data.** Add `RealmId` to `Guild`, `TradepostListing`, `Mail` (+ any other
  world-global dynamic table); scope every query by realm; EF migration backfilling existing rows to realm 1.
  Realm-scope the chat channels (`AgentServer`) and guild/team ops (`CsPlayerAgent`) — a realm dimension on the
  session so channels/guilds don't cross realms.

- **Phase 4 — Isolation hardening.** Audit for cross-realm leakage (interest, broadcast, dispatch, name lookups);
  per-realm persistence loops; ensure a name/handle is only ever resolved within its realm.

- **Phase 5 (optional) — Process-per-realm.** Extract the login/routing tier into its own host; each game process
  reads its realm id (config/arg) and binds `Realm.IpAddress:Port`; shared DB or per-realm DBs.

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
