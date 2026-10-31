# Multi-realm (multiple game servers)

Project Faolan supports N realms (AoC "servers" — Crom/Fury/…), each an isolated world a character belongs to,
hosted as separate processes. The wire + data model is realm-aware, and the dynamic data is scoped per realm.

## Where a realm is defined

A realm row is three things wearing one name, and knowing which is which decides where each part lives:

| Half | Columns | Owner |
|---|---|---|
| Static deployment config | `Name`, `IpAddress`, `Port`, `ChatServerPort`, `CsPlayerAgentPort`, `Type`, `Language` | the `Realms` section of faolan.json |
| Live | `OnlineStatus`, `LastSeenUtc`, `LoadStatus` | the host that serves the realm |
| Relational anchor | `Id` | the four tables that FK it |

The static half is declared in configuration and reconciled into the table at startup (`FaolanRealms` reads the
section, `DatabaseInitializer.ReconcileRealmsAsync` writes it), so a realm's name, address and ports are reviewable
in a text file.

The other two halves cannot move to a file, and the reconciler never writes them:

- `OnlineStatus` + `LastSeenUtc` are how a login process learns whether a realm host — possibly on another machine —
  is still serving. A file cannot observe an absence. The process owning a realm's ports is the only thing that
  writes its liveness, which is why a realm-role process reconciles only its own row (`onlyRealmId`) while the login
  tier reconciles the whole declared set.
- `LoadStatus` is the realm's population, which nothing computes. Declaring it would prevent it ever being computed:
  every restart would overwrite whatever a live count had put there.

A deployment that declares no realms is left alone — the "insert the default realm if the table is empty, else do
nothing" path (`EnsureDefaultRealmAsync`). Config is authoritative only once somebody writes it down, so a database
whose realm carries some other id never gains a duplicate from a declaration it never made.

Two more rules follow from the anchor half. Ids are explicit and must stay stable, because `Character.RealmId` is
persisted — an implicit ordinal would silently repoint every character the day somebody reordered the file. And a
row the config has dropped is reported, never deleted: four tables FK a realm, so removing one is a data migration
rather than a config edit.

`Language` is the client's vocabulary: "country" does not occur once in the 34 MB `AgeOfConan.exe`, while the client
has `VerifyLanguageSetting(const int nClientLanguage)`, `DispatchResetLanguage(nNewLanguage)`, and the login LDB page
carries *"Your account does not have any valid **language flags**"* in the same block as every dimension error — so
dimension access is gated on the account's language flags, which is what a per-dimension language is for.

Declarations are validated at config time rather than at wire-write time, where a mistake would surface only after a
player had picked the realm. A non-IPv4 address (the client is IPv4-only; open work tracked in Gitea #845), a zero
port, a duplicate id or name, and two realms sharing a port on one address are all refused, naming the offending key.

## What the client does with the realm list

`SetDimensionList` is built by `SetDimensionListCodec`, and that codec is the record of the layout: each entry maps
onto the client's `Dimension_t`, whose fields were read off its per-entry reader at `0x005888C0`. Six of the eleven
fields have a consumer:

| offset | what | consumer |
|---|---|---|
| `+0x04` | online status | the row renderer's `== 2` gate |
| `+0x08` | name | rendered |
| `+0x24` | language flags, a bitfield | resolved to a language id in `FUN_00A69180` |
| `+0x30` | load level, the 0-4 enum | `FUN_011F4490`, display code = value + 1 |
| `+0x38` | type, high byte | compared against 7 |
| `+0x40` | language id | tested non-zero only, to show the flag-button row |

`+0x30` takes the load-level enum itself, not a `== Full` boolean (with every realm `Recommended` both spellings are
0, so a wrong one is invisible). `+0x24` is the realm's language bitfield, not a constant 1 (which sets the English
bit).

**The two language fields are not redundant.** `+0x24` says what language a realm IS, as a bitfield: Russian
`0x400`, Polish `0x800`, Korean `0x100000`, and bits 0 or 1 for English. `+0x40` only says whether it has one at
all. French, Spanish and German have no bit of their own, and that is the finding rather than a gap in it — the
widget row this pairs with holds exactly `EnglishFlag`, `GermanFlag`, `FrenchFlag` and `SpanishFlag`, so one
Western realm serves all four and the player picks with the flag row, while Russian, Polish and Korean get realms
of their own.

The language **ids** are the client's own, from the table in `FUN_011D7740` that turns the running client's
two-letter code into the int it sends: `en=0 fr=1 es=2 de=3 pl=4 ru=5 ko=6`, with its inverse feeding
`DispatchResetLanguage`.

`DimensionList_t`'s hand-written getters say nothing about any of this: all eight have zero callers, because the
client inlines the map lookup at each site instead.

## Architecture

- **N game worlds, one per realm**, each isolated: its own `WorldSimulation` / `WorldState` / zones / interest /
  persistence. A player on realm A never sees realm B's entities, chat, guilds, or AH.
- **Shared login/routing tier** (`UniverseAgent` + `PlayerAgent`): authenticates once, shows the realm list
  (`SendRealmList` loops every realm row), and routes each player to their selected character's realm by
  `Realm.IpAddress:Port` in the enter-world handoff.
- **Realm-local dynamic data**: characters (`Character.RealmId`), guilds, AH listings, mail, chat channels.
- **Shared static content** (realm-agnostic reference): maps, NPC/item/spell templates, RDB assets. One copy,
  read by every realm.

## Deployment model: process-per-realm

Each realm is its own Faolan game process bound to its `Realm.IpAddress:Port` (any host), with a shared login/routing
tier and a shared database. A realm can live on its own machine, and a crash takes only that realm down.

- **Login/router process (one):** `UniverseAgent` (7010, the only fixed port) + `PlayerAgent` — auth, the realm
  list, the character list, and the enter-world routing that hands the client to the selected character's realm.
  Owns no world.
- **Realm game process (one per realm):** `GameServerListener` + `WorldSimulation` + the realm-local chat/guild
  services (`ChatServerListener`, `CsPlayerAgentListener`), bound to that realm's world/chat/cs ports, with the realm
  id threaded through the simulation, dispatcher and MCP.

`--role login` runs the router tier; `--role realm=<id>` runs a realm game host; the default is all-in-one. `--role`
is repeatable (`--role login --role realm=<id>` co-hosts both), but at most one realm role per process (co-hosting
realms would share chat/session state across them). Role validation is a command-line error, not a crash:
`ServerCommand.TryParseRoles` (shared by the option's validator and its reader) answers `--role realm=1 --role
realm=2`, `--role realm=abc` and a plain typo with one line plus usage and exit 1.

One login + 2 realm processes run concurrently against the shared DB, each binding its own realm's ports from its
`Realm` row (login 7010/7011; realm 2 → 7014/7012/7001; realm 3 → 7024/7022/7021), with no collisions.

## Realm-scoped data

Guild, AH and mail rows carry an explicit `RealmId` — not derived from members: an empty or just-created guild has no
members to infer a realm from, and a guild's realm is pinned at creation.

- **`Guild`**: `RealmId` set at creation from the founder's realm; the name is unique per `(RealmId, Name)` (Crom and
  Fury can each have a "Knights"), and `GetGuildByName` is realm-scoped.
- **`TradepostListing`**: `RealmId` = the seller's realm; every search/list/buy query is realm-scoped.
- **`Mail`**: `RealmId` = the recipient's realm (no cross-realm mail in retail).
- **`Character.Name`** is unique per `(RealmId, Name)`, and `GetCharacterByName(name, realmId)` is realm-scoped.
  Whispers address by id, not name.
- Character-owned data (inventory, quests, shortcuts, …) is transitively realm-scoped via `Character.RealmId`.

`Faolan.Tests/RealmIsolationTests` gates the isolation: it seeds two realms whose rows collide on everything except
the realm — the same guild name, sale id, seller and bank slot, mail recipient, character name — and asks each
realm's own reader for its own, reading back a value that differs per realm rather than a count (a missing `WHERE`
fails quietly, with realm B's rows simply appearing in realm A). Dropping the filter from `GetGuildByName`,
`GetGuilds` or `DeleteTradepostListing` fails it. The deliberate exceptions are asserted as such:
`GetCharactersByAccount` spans realms (the selection screen's wire row carries `character.RealmId` per entry), and
`GetItemSearchCategory` reads the catalog fact across realms by design. The other reads on realm-scoped tables are
by-unique-id lookups (`GetCharacter(id)`, `GetGuild(id)`, `GetMailByWireId`, `GetTradepostListingBySaleId`) or
intentionally global (character-id allocation over the shared id space). Runtime isolation holds by construction: a
realm process holds only its own connected sessions and zones.

## `OnlineStatus` means serving, not "began starting"

The login tier's realm list is driven by `Realm.OnlineStatus`, so a wrong value points players at a realm that is not
there. `RealmStatusService` (realm role only) writes it at the two moments that mean something:

- **`StartAsync` claims the row**, writing Offline. That clears a stale Online left by this realm's own previous
  life. It is safe because owning the row means owning the ports: the listeners create their sockets in their
  constructors, and hosted services are constructed before the first `StartAsync` runs, so by then this process has
  bound the realm's world/chat/cs ports and no other process can be serving that realm.
- **`IHostApplicationLifetime.ApplicationStarted` flips it Online**, which fires only once every hosted service has
  started and the web host (when there is one) has bound. Graceful shutdown flips it Offline.

Writes go through `IDatabaseRepository.SetRealmOnlineStatus(realmId, status)`, by id, so hosts on a shared DB never
touch each other's rows. A realm that is `kill -9`'d and restarted into an occupied port claims the row, fails the
bind, and reads Offline with nothing listening. Gated by `RealmStatusServiceTests`. The committed `faolan.db` carries
the stopped-state default, Offline.

**A crashed realm decays out of the list.** A hard crash skips `StopAsync`, and with no restart nothing reclaims the
row, so the absence has to be observed: `Realm.LastSeenUtc` is stamped by the serving host every
`RealmLiveness.HeartbeatInterval` (30 s) and every reader takes the pair through `RealmLiveness.Effective`, which reads
Online-but-stale as Offline after `StaleAfter` (2 minutes — four intervals, so three missed stamps do not take a
healthy realm down). A stamp rather than a lease or a reaper keeps the invariant: a realm process writes only its own
row, and the login tier only reads. A `NULL` stamp is not serving even with the flag set, and a stamp in the future is
accepted, since the two processes may be on different machines with some clock skew. Open work tracked in Gitea
#922.

## Ports: everything a realm host binds is per-realm

A realm's world / chat / cs ports come off its own `Realm` row. The MCP endpoint is derived: `Mcp:Port` is a single
global key and MCP runs only in a realm host, so a realm host binds `Mcp:Port` plus its realm's offset from the first
realm:

| realm | MCP endpoint |
|---:|---|
| 1 | `http://127.0.0.1:7099/mcp` |
| 2 | `http://127.0.0.1:7100/mcp` |
| 3 | `http://127.0.0.1:7101/mcp` |

The offset rather than the raw id keeps realm 1 on the configured port exactly. The process prints its own endpoint at
startup (`[mcp] http://127.0.0.1:<port>/mcp (realm <id>)`), and a bind failure names the MCP port and suggests
`--Mcp:Enabled false` / `--Mcp:Port <port>`.

Realm ids are not promised to be small or contiguous; an id far enough out to run a `ushort` past its end falls back
to the base port rather than wrapping into a port that means something else. Set `Mcp:Port` per process there.

`Configuration.McpUrl` is derived from the base port, so it is the first realm's endpoint. That is right for
`Faolan.Web`, which is not realm-aware; anything wanting another realm's endpoint has to derive it the same way.

## Database

A shared database across N realm processes needs concurrent access: on SQLite that depends on `journal_mode=WAL`
(see `AGENTS.md`), and the Postgres provider is the server-database option. Tests stay on SQLite regardless — they
validate code-builds-from-data, not the storage engine; `TestDb.ReadOnly` is the single seam for the real-data golden
fixture.

## Retail constraints

The realm-select flow (`RequestCharRealmData` → realm list → char-realm-data) matches retail for one realm. No capture
carries a multi-realm list, so the >1-realm list record and the char-per-realm counts are built against the client's
`Dimension_t` reader above, and the client honouring a non-default host:port in the enter-world handoff (0x2002) is
not capture-confirmed.
