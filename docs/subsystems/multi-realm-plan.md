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
5. **Chat + guild services are global singletons** — one `ChatServerListener` (chat) and one
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
  services** (`ChatServerListener` chat, `CsPlayerAgentListener` guilds/teams) — because chat and guilds are
  realm-local, they move OUT of the shared tier and INTO each realm process. Bound to that realm's `IpAddress:Port`
  (+ the per-realm chat/cs ports); reads its own realm id from config/CLI (`--role realm=<id>`). Shares `faolan.db`,
  scoping every dynamic query by realm.

So `Program.cs` gains a **role**: `--role login` (or default) runs the router tier; `--role realm=<id>` runs a realm game
host. `--role` is repeatable (`--role login --role realm=<id>` co-hosts both), but at most one realm role per process
(co-hosting realms would share chat/session state across them). One login process + N realm processes, shared DB.

## Phased plan (each phase test-gated; no wire regressions)

- **Phase 0 — Realm registry.** Mostly done. Seed >1 `Realm` row; a GM/config way to add realms. `SendRealmList`
  already serves them. **Verify in-client** that the client accepts a >1 realm list + per-realm char counts (we've
  only ever sent 1 — retail-leading: confirm the multi-realm list bytes against a capture/Ghidra before relying on it).

- **Phase 1 — Realm-routed handoff + realm-scoped char list** *(client-free to build; the smallest visible slice)*.
  Replace the static `Program.GameServerPort` (Inbound.cs:1264) + the `Configuration.ServerIpAddress` handoff sends
  with the *selected character's* `Realm.IpAddress:Port` — look the character's realm up at `EnterWorld` and thread
  it into the GameServer/CsPlayerAgent/ChatServer handoff addresses. Scope `SendSmallCharList` (and the char-realm
  counts) to the client's selected realm. Two realm rows pointing at the SAME process proves routing before the
  process is actually split.

- **Phase 2 — Process role split** *(the heart of B)*. `Program.cs` gains a role: `--role login` (default) starts
  ONLY `UniverseAgent` + `PlayerAgent` (+ the DB); `--role realm=<id>` starts a realm game host — `GameServerListener` +
  `WorldSimulation` + the realm-local `ChatServerListener` + `CsPlayerAgentListener` — bound to that realm's ports,
  with the realm id threaded through the whole world (simulation, dispatcher, MCP). The login tier no longer owns a
  world; the realm host no longer owns login. Run one login process + one realm process locally to validate.

- **Phase 3 — Realm-scope the shared dynamic data.** Add `RealmId` to `Guild`, `TradepostListing`, `Mail` (+ any
  world-global dynamic table); scope every query by realm; EF migration backfilling existing rows to realm 1. Now
  that chat/guild services live IN the realm process (P2), they are realm-scoped by construction — but the DB rows
  they read/write must carry `RealmId` so two realm processes on the shared DB don't cross-read.

  The bits that bite if missed (an **explicit** `RealmId` column, NOT derive-from-members — an empty/just-created
  guild has no members to infer a realm from, and a guild's realm is pinned at creation, not by transient membership):
  - **`Guild`**: add `RealmId` (FK), set at creation from the founder's realm. Change the unique index
    `[Index(Name, IsUnique)]` → **composite `(RealmId, Name)`** (Crom and Fury must each be able to have a "Knights").
    `CreateGuild(name, leaderCharacterId)` → take/derive `realmId`, and scope its name-uniqueness check + `GetGuildByName`
    by realm (both are GLOBAL today: `DatabaseRepository.CreateGuild`/`GetGuildByName`).
  - **`TradepostListing`** (auction house): `RealmId` = the seller's realm; the AH is realm-local (a realm process
    shows only its own market). Scope every search/list/buy query by realm.
  - **`Mail`**: `RealmId` = the recipient's realm (no cross-realm mail in retail); scope inbox/send by realm.
  - **Character-owned data** (inventory, quests, shortcuts, …) is already transitively realm-scoped via
    `Character.RealmId` — no column needed there.
  - Migration backfills all existing rows to realm 1 (the current single realm), then a query audit ensures no
    guild/AH/mail read path is missing the realm filter (the cross-realm-leak risk).

- **Phase 4 — Isolation hardening + N processes.** Run N realm processes against the shared DB; audit for
  cross-realm leakage (dispatch, name/handle lookups, DB queries missing the realm filter); confirm a player on
  realm A never sees realm B. Config/orchestration for launching the fleet (one login + N realm hosts).

  **Character names per realm (P4, DONE):** `Character.Name` is now composite `(RealmId, Name)` unique, and
  `GetCharacterByName(name, realmId)` is realm-scoped with all 6 callers passing their realm (guild invite, GM
  resolve, tradepost seller/recipient/sender, create-flow name check). Whispers address by ID, not name.

  **Cross-realm-leak audit (P4, DONE — CLEAN):** every query on a realm-scoped table (`Guilds`/`TradepostListings`/
  `Mails`/`Characters`) was checked. No leaks: the by-name/search queries are realm-scoped (P3/P4); the rest are
  by-unique-id lookups (`GetCharacter(id)`, `GetGuild(id)`, `GetMailByWireId`, `GetTradepostListingBySaleId`),
  intentionally-global (character-id allocation over the shared id space; the login tier's cross-realm char list),
  or realm-agnostic static metadata (the AH item-search-category inference). Runtime isolation also holds by
  construction: a realm process only holds its own connected sessions/zones (P2).

  **Fleet compose (P4, DONE):** verified 1 login + 2 realm processes run concurrently against the shared DB, each
  binding its own realm's ports from its `Realm` row (login 7010/7011; realm 2 → 7014/7012/7001; realm 3 →
  7024/7022/7021), no collisions. So the process-per-realm topology composes.

  **Realm up/down status (P4, DONE):** `RealmStatusService` (`Faolan/Helpers/`, IHostedService, realm role only)
  flips this realm's `Realm.OnlineStatus` → `Online` on startup and → `Offline` on graceful shutdown, via
  `IDatabaseRepository.SetRealmOnlineStatus(realmId, status)` (updates by id, so hosts on a shared DB never touch
  each other's rows). Registered last in the `runRealm` block, so it comes up after the listeners bind and — hosted
  services stopping in reverse — flips Offline first as shutdown begins. The login tier's `SendRealmList` already
  reads `OnlineStatus`, so this is what makes the realm list reflect reality. **Caveat:** a hard crash / SIGKILL
  skips `StopAsync`, leaving the row stale-`Online`; a heartbeat/last-seen TTL is the robust follow-up if that
  becomes a problem in practice. (Committed `faolan.db` carries the stopped-state default, `Offline`.)

  **Remaining (P4, needs a client):** the end-to-end in-client check — a character on realm 2 vs realm 3 routes to
  the right process and sees only its realm's world/chat/guild/AH — plus the P0 gate (does the client accept a >1
  realm list). Both need the retail client, so they're deferred to an in-client session.

- **Phase 5 (optional) — Per-realm DBs.** If shared-DB contention or stronger isolation warrants it, give each realm
  process its own DB (static content cloned/shared read-only, dynamic per-realm) instead of `RealmId` columns.

## Database provider (SQLite → server DB, when Docker-deployed)

SQLite is fine for dev and a single process, but a **shared DB across N realm containers** wants a real server DB
(Postgres): SQLite's single-writer file locking is awkward for concurrent container access. The swap is **~half a
day**, not a rewrite — EF Core is code-first and provider-agnostic, and `faolan.db` is *built by the importer*, so
there's no manual data migration:
- Add the provider package, swap `UseSqlite` → `UseNpgsql` at the ~4 production call sites (`DatabaseServiceCollectionExtensions`,
  the `Program.cs` realm probe, the design-time factory, the importer CLI); `DatabasePath` becomes a connection string.
- **Regenerate migrations** for the new provider (they don't port; squash to one initial migration).
- **Re-run the importer** against the new DB (it's the source of truth - no export/import).
- Watch-outs: `ulong` columns → Postgres `bigint` is *signed* (audit large ids; most are already capped to
  `long.MaxValue`); one SQLite-ism to fix (`INSERT OR IGNORE` at `DatabaseRepository.cs`, → `ON CONFLICT DO NOTHING`).

**Tests stay on SQLite regardless** - they validate code-builds-from-data, not the storage engine. The golden
tests keep an embedded SQLite fixture (`Faolan.Tests/TestDb.ReadOnly`, the single seam); provider-specific fidelity
(the ulong/`ON CONFLICT` cases) is a *small* separate Testcontainers-Postgres suite, not the whole golden suite.
See the `TestDb` doc-comment for the full rationale.

## Retail constraints (retail is leading)

- The realm-select flow (`RequestCharRealmData` → realm list → char-realm-data) already matches retail for 1 realm.
  **Before advertising >1 realm**, confirm the multi-realm list record format + the char-per-realm counts against a
  capture (or the client reader in Ghidra) — a guessed list that the client rejects stalls at server-select.
- The enter-world handoff (0x2002) carrying a per-realm IP:Port is exactly what `Realm.IpAddress:Port` is for;
  confirm the client honors a non-default host:port in the handoff.

## Risks / unknowns

- **Multi-realm list bytes** not capture-verified (only ever sent 1 realm) — Phase 0 gate.
- **Chat/guild/AH realm-scoping is the invasive part** (touches `ChatServer` / `CsPlayerAgent` / economy) — the
  bulk of Phase 3.
- **DB model**: shared `faolan.db` + `RealmId` columns (simplest, recommended) vs per-realm DBs (stronger isolation,
  more ops). Recommend shared + `RealmId`.

## First concrete step

Phase 1 on two realm rows that both point at the current single world: pick a character on realm 2 → the handoff
routes to realm 2's port. Proves the routing + char-list scoping with zero world-isolation work; Phase 2 then
splits the worlds behind that routing.
