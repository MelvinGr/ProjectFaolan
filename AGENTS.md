# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Project Faolan is an Age of Conan server protocol emulator (C#, .NET 10). **Packet captures are the primary truth
source**: the code mirrors retail traffic byte-for-byte rather than abstracting it, and where local behavior diverges
from a retail capture, local is fixed to match. This file plus `docs/` are the deep-dive (architecture, protocol notes,
RE methodology); `README.md` is the regular repo readme (overview, build, run). Open work is tracked as **Gitea issues**
in the `projectfaolan` repo on `git.groenendaal.io`, grouped by `area:*` labels, never in a TODO file. Create and close
issues there as work lands (the agent memory `issue_tracking_in_gitea` has the API recipe).

## Commands

```zsh
# Build (repo root)
dotnet build ProjectFaolan.slnx

# Tests: xunit.v3 on Microsoft.Testing.Platform. global.json opts `dotnet test` into MTP, so the target is
# `--project` and filters are MTP options after `--` (the VSTest `--filter` form does not exist).
dotnet test --project Faolan.Tests
dotnet test --project Faolan.Tests -- --filter-class '*GuildPacketTests'  # single class
dotnet test --project Faolan.Tests -- --filter-class '*Shop*'             # class-name substring (--filter-method for methods)

# Run the server. The CWD must be the repo root: Program.cs loads faolan.json and faolan.db from the CWD, and both
# live at the repo root. Running from bin/Debug fails with "faolan.json not found".
dotnet run --project Faolan

# Drive the retail client unattended against the local server (auto-login -> world)
dotnet run --project Faolan.ClientLauncher -- --console --patch-universeaddr -u faolan -p any -c <char>

# Rebuild DB content from captures. The importer is module-based (docs/tooling/importer.md): each subsystem is a
# selectable module; `--dry-run` writes nothing (runs in a rolled-back transaction and prints the per-table delta).
dotnet run --project Faolan.Importer -- --skip characters     # idempotent except characters; skip it on a populated DB
dotnet run --project Faolan.Importer -- --list                # every module, and which are opt-in
dotnet run --project Faolan.Importer -- --report list         # every --report (the table in ReportCli.cs is the index; a typo answers did-you-mean)
dotnet run --project Faolan.Importer -- --only npcs --mode overwrite   # one module
dotnet run --project Faolan.Importer -- --only rdb-maps --Content:RdbDir <dir> # rdb-* and mounts are opt-in (not in the default run)
dotnet run --project Faolan.Importer -- --report burst-coverage       # read-only diagnostics
dotnet run --project Faolan.Importer -- --report sql-seed             # (re)write faolan.sql from the DB
dotnet run --project Faolan.Importer -- --report apply-seed --dry-run # upgrade an existing db's content from faolan.sql
dotnet run --project Faolan.Importer -- --report timeline --report-arg <capture>.tshark.json --Content:RdbDir <dir>
# ^ one capture as a flat chronological story, ids resolved and LDB text decrypted inline (`./run.sh --timeline <dump>`;
#   see the `capture-timeline` skill). Decode lives in the codecs - add one there, never inline in a report.
# A grep over the RDB pack files misses most strings: records are mostly zlib-compressed, so text the client ships is
# absent from the file bytes. Search decompressed bodies instead:
dotnet run --project Faolan.Importer -- --report rdb-text-scan --report-arg "SomeName,Other:local"
dotnet run --project Faolan.Importer -- --report bcc-trees   # the behaviour-tree substate id->name table; RDB
#   1010203 is plain-text XML (its 1010501 twin is the merged binary) - some RDB types need no decode at all.
dotnet run --project Faolan.Importer -- --report rdb-names --report-arg index:1010029  # id->name table (1000010/1);
#   index:<rdbType> lists one type's names (1010029 = the server-side script catalog). Names are lowercased.
# Modes: --mode insert|upsert(default)|overwrite, or per module: --mode npcs=overwrite,items=insert.

# Wireshark plugin: full Conan + chat dissection (auto-follows the session from port 7010).
# Build and install once, then open captures in Wireshark (filter: faolan) or dump to JSON.
cmake -S Faolan.Wireshark -B Faolan.Wireshark/build && cmake --build Faolan.Wireshark/build -j \
  && cmake --build Faolan.Wireshark/build --target install-faolan-plugin
tools/capture/tshark_dump.sh Captures/<capture>.pcapng   # the canonical *.tshark.json the importer reads:
# TCP-reassembled, one record per packet, including the compressed download packets.
# Lossy captures in the Wireshark GUI: set the TCP pref analyze_sequence_numbers off (the script does).

# EF migrations. The context lives in Faolan.Core.Database; migrations are per provider, in
# Faolan.Core.Database.Migrations.{Sqlite,Postgres}/Migrations.
# Scaffold the SQLite migration only (the maintainer's standing decision). Each chain is a single `InitialCreate`
# (squashed 2026-09-11), so Postgres is level with the model as of that date and does not track later changes. The
# Postgres provider code stays: Faolan.Core.Database is provider-agnostic and faolan.sql loads into both.
# Bringing Postgres level again = delete its Migrations/ and scaffold one fresh initial migration from the current
# model, never a chain.
# A squash also rebuilds faolan.db on the fresh schema - create a fresh db from the new migration, copy every row
# across, verify per-row parity - rather than only rewriting its `__EFMigrationsHistory` row: columns that incremental
# migrations added via ALTER TABLE carry a DEFAULT the model does not declare, so a re-pointed db is not
# schema-identical to a fresh one.
# The Postgres `InitialCreate` is verified against a real postgres:17: it applies, and faolan.sql loads into it under
# ON_ERROR_STOP at zero errors with full row parity. Hand-written SQL in the model must quote its identifiers: raw
# CHECK SQL goes into CREATE TABLE verbatim, and Postgres folds an unquoted identifier to lower case while SQLite is
# case-insensitive (`CK_ItemInstance_OneOwner` and `ConstrainToEnumValuesAttribute` both quote).
# The EF tools run at design time with CWD = the project dir. Global.ConfigPath searches up from the CWD (then up from
# the exe dir) for faolan.json, and a relative SQLite `Data Source` resolves against the directory that file was found
# in, so both commands work from anywhere in the tree. FAOLAN_CONFIG wins if set.
# The two --project flags are required: `.config/dotnet-ef.json` carries them as defaults, but that file is an EF 11
# tooling feature and the installed tool (10.0.11) ignores it. It is committed so the bare commands work once the tool
# is on 11.
dotnet ef migrations add <Name> \
  --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
# `database update` likewise (or just start the server: it migrates, loads faolan.sql into a database with no content,
# and seeds the default realm and account at startup):
dotnet ef database update \
  --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
# For pure data changes (no schema change) update faolan.db directly and commit it - no migration.
```

## Architecture

**Server flow:** `UniverseAgent (7010, the only fixed port) -> PlayerAgent -> GameServer`. Each service hands the
client the next address in its payload. `Faolan/Program.cs` runs the protocol services as hosted services:
`UniverseAgentListener`, `PlayerAgentListener` (the login/router tier), `CsPlayerAgentListener` (guilds, teams, group
finder), `ChatServerListener` (chat) and `GameServerListener` (the world; one listener serves both the create-editor and
world connections).

**Process role** (multi-realm, `docs/subsystems/multi-realm.md`): by default `dotnet run --project Faolan` is
all-in-one (login plus a game host for the first realm). `--role login` runs only the router tier; `--role realm=<id>`
runs one realm's game host (GameServer, realm-local chat and guilds, bound to that realm's ports). `--role` is
repeatable (`--role login --role realm=<id>` co-hosts both), but a process hosts at most one realm, because co-hosted
realms would share chat and session state. One login process plus N realm processes form a multi-realm deployment
against the shared DB; guilds, AH, mail and character names are realm-scoped (`RealmId`) and routed by the character's
realm. Realms are declared in the `Realms` section of faolan.json: that is the static half of a `Realm` row
(name, address, ports, type, language), reconciled into the table at startup. The live half (`OnlineStatus`,
`LastSeenUtc`, `LoadStatus`) is written only by the host serving the realm and is never declarable. A row is never
deleted, because four tables reference it. A deployment that declares no realms gets the default realm inserted into
an empty table.

**Projects:**
- `Faolan/` - the server host.
- `Faolan.Core/` - the base layer: shared types, crypto, the `Protobuf/` schema, and the opcode/IIR catalogs in
  `Faolan.Core/Enums/Opcodes.cs`.
- `Faolan.Core.Database/` - EF Core: `DatabaseContext`, `Tables/`, `DatabaseRepository`.
- `Faolan.Core.Network/` - networking and the byte-exact packet codecs (`Codecs/`, by domain: `Spawn/`, `World/`, ...).
- `Faolan.Core.Rdb/` - client RDB extraction.
- `Faolan.Importer/` - tshark dump + RDB -> DB command line.
- `Faolan.ClientLauncher/` - the unattended retail client.
- `Faolan.Mcp/` - the in-process MCP server (DB, live-session and GM-command tools).
- `Faolan.Web/` + `Faolan.Web.Content/` - the Blazor armory, 3D viewer, GM tools and CDN proxy.
- `Faolan.Tests/` - the test suite.
- `Faolan.Wireshark/` - the Conan-protocol dissector plugin: framing, zlib, RpcHeader endpoints, named opcodes and IIR
  types (generated from `Opcodes.cs`) and handoff auto-follow. It is the ground truth for captures (TCP-reassembled; use
  `-o tcp.analyze_sequence_numbers:FALSE` on lossy captures).
- `tools/` - `capture/` Python dissectors and `tshark_dump.sh`, `ghidra/` RE scripts, `frida/` live-client scripts.
- `Captures/` is not in this repo; it lives in the private ProjectFaolan-Data repo (see **Capture data**).

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
  character id", which `ConnectionRegistry` (`Faolan.Core.Network`) answers: `ServerBase` owns one and delegates
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

Listeners derive from `RpcServer` or `ChatServerBase` (both `ServerBase`, in `Faolan.Core.Network/Server.cs`);
per-connection state lives on `INetworkClient.Session`. Cross-listener actions go through dispatcher seams
(`IChatServerDispatcher` for chat and system messages, `IGameServerDispatcher` for world actions), never direct
references.

**Database:** SQLite via EF Core (`Faolan.Core.Database`: `DatabaseContext` + `Tables/`), a pooled `DbContextFactory`,
short-lived per-unit-of-work contexts. Lazy loading is deliberately not used; load navigations explicitly with
`Include`. All access goes through `IDatabaseRepository`, which is the composite of 13 per-domain slices, each declared
beside its implementation (`Repositories/DatabaseRepository.<Domain>.cs`). The character's five slices are split by the
question a member answers: `ICharacterRepository` (the row and its 1:1 satellites), `ICharacterInventoryRepository`
(what it carries), `ICharacterQuestRepository` (what it is on), `ICharacterProgressionRepository` (what it has learned)
and `ICharacterStatRepository` (what it adds up to).
- **A service takes the slices it calls, not the composite**: one to three slices as separate constructor parameters
  (`_catalog`, `_inventory`, `_world`, ...). A consumer of four or more slices (`WorldEntryService`, `GmCommandService`,
  `TradepostService`, ...), or one that hands the repository to a helper needing the composite, keeps
  `IDatabaseRepository`.
- Both realm containers forward every slice to the one singleton: a new slice must be added to both
  `AddRealmGameServer` and `AddRealmCsPlayerAgent` (the composition tests catch a miss).
- `faolan.db` carries the game data (built by the importer, committed to git); the server only migrates and
  reads/writes at save/load points. The sqlite3 CLI has foreign keys off by default: run `PRAGMA foreign_keys=ON`
  before manual deletes or you orphan child rows.

**Model conventions** (enforced by `Faolan.Tests/DatabaseConventionTests.cs`):
- **A foreign key is declared with `[ForeignKey]` on the navigation property, never fluently.** Reading
  `Tables/Foo.cs` must tell you what `Foo` points at. A composite key is no exception: `[ForeignKey]` takes a
  comma-separated column list, kept rename-safe with `nameof`:
  `[ForeignKey($"{nameof(ConversationNodeId)},{nameof(PacketSequence)}")]`. What legitimately stays fluent is
  everything that is not the FK column mapping: `.OnDelete(...)` (no attribute exists), `.ValueGeneratedNever()`, and
  the `HasMany`/`WithOne` pairing that names both ends.
- **A table the importer deletes and reinserts has a natural or derived key, never an AUTOINCREMENT surrogate.** A
  surrogate is reassigned on every re-harvest, so a reimport churns thousands of `faolan.sql` lines for no semantic
  change and cascades into every child FK. In order of preference:
  1. **Natural composite** - `[PrimaryKey(...)]` on a plain class (not `BaseEntity`), e.g. `ShopItem` `(ShopId, Pos)`,
     `NpcSpawnEquipment` `(NpcSpawnId, Pos)`. In a tree each child is keyed by its parent's key plus its own
     `Sequence` (`ConversationPacket` -> `ConversationScreen` -> `ConversationScreenParameter`).
  2. **Derived id** (`DerivedKey` / `IDerivedKey`) when the identity is too wide to carry down a tree, contains floats,
     or is the target of a nullable FK (a composite cannot express `SetNull` when one of its columns is non-null). The
     entity exposes `ComputeId()`, `DatabaseContext` stamps it on insert, and `DatabaseConventionTests` checks the
     shipped rows satisfy `Id == ComputeId()`. Used by `ConversationNode`, `NpcSpawn`, `NpcSpawnArea`, `WorldObject`.
  To check whether a table needs this: `sqlite_sequence.seq / COUNT(*)` is how many times it has been renumbered.

**SQLite is interim.** The goal is a real database with the schema and seed as a `.sql` file, dropping the binary.
- **`faolan.sql` is the reviewable mirror of `faolan.db`'s data, and a seed that loads into both SQLite and Postgres.**
  The `.githooks/pre-commit` hook regenerates it whenever `faolan.db` is committed, dumping the staged blob so it
  matches the commit exactly. Never hand-edit it; to regenerate by hand run
  `dotnet run --project Faolan.Importer -- --report sql-seed`.
  - **Run `git config core.hooksPath .githooks` once per clone.** Git does not share hooks; without it `faolan.sql`
    silently goes stale and the personal-name guard does not run (`.githooks/pre-commit` chains
    `.githooks/personal-names.py`, which blocks the maintainer's retail account and character names from entering a
    repo meant to go public). The guard's logic is tracked; its identity list is not - it reads the untracked
    `.git/hooks/personal-names.json` (or `$FAOLAN_PERSONAL_NAMES`). With no config it skips and says so; `--tree`
    audits every tracked file.
  - **Restore:** create the schema (`dotnet ef database update`), then run `faolan.sql`. It is one file, verified at
    zero errors with full row parity into a fresh SQLite and a fresh Postgres. No game-content table has a
    store-generated key, so there are no sequence high-water marks to restore;
    `DatabaseConventionTests.NoGameContentTable_HasAStoreGeneratedKey` keeps it that way, and `SqlSeedWriter` throws,
    naming the table, rather than emit a seed whose next INSERT would collide.
  - It is generated from the EF model (`SqlSeedWriter`), not by `sqlite3 .dump`, because a dump is SQLite-only:
    identifiers are quoted (Postgres folds unquoted `Items` to `items`), every row has an explicit column list (column
    order is irrelevant), literals come from each property's CLR type rather than the value the reader returns (SQLite
    returns 0/1 for booleans, which Postgres rejects), and tables are emitted in FK-dependency order so the file loads
    with constraints enforced. It contains no `CREATE TABLE` (the migrations own the schema) and no
    `__EFMigrationsHistory`. Characters and other player/deployment state are excluded; the excluded set is derived
    from the model. Rows are batched into multi-row `INSERT ... VALUES (...),(...)`, one row per line, so the diff stays
    reviewable.
  - **A database with no game content gets the seed loaded automatically** (`DatabaseInitializer.EnsureGameDataAsync`):
    delete `faolan.db` and the server rebuilds it from the migrations plus `faolan.sql`, on SQLite or Postgres. The probe
    is `Items` being empty, so a populated database is never touched. If the seed is missing when needed, startup fails
    with a clear error. `FAOLAN_SEED` overrides the path (`Global.SeedPath`). The importer opts out
    (`InitializeAsync(db, seedGameData: false)`), since its job is to build that content.
- **`journal_mode=WAL`** is a persistent file-header flag on `faolan.db`. Without it a reader blocks a writer, so any
  concurrent read (a `sqlite3` query, the MCP server, Faolan.Web, another realm process) makes a live world write fail
  and be lost. A busy timeout does not help (it is already 30 s by default): it only makes the writer wait while the
  reader still holds the lock.

**Wire protocol essentials:**
- `Rpc.Serialize()` (`Faolan.Core.Network/Protocol/Rpc.cs`) is the one place a Conan frame is encoded; it backfills
  the length and CRC. Bodies are built with `FaolanStream` inside the codecs. Integers are big-endian; protobuf fixed32
  floats are little-endian while raw-body floats are big-endian (`WriteFloatLittleEndian` and the big-endian
  `WriteFloat` are both intentional).
- **Offset-addressed reads and writes over a buffer go through `SpanExtensions`** (`Faolan.Core/Extensions`):
  `body.ReadU32Be(4)` / `template.WriteF32Le(PosXOffset, x)`, not `BinaryPrimitives.<Op>(body.AsSpan(4, 4))` and never
  a local helper. `byte[]`, `Span<byte>` and `ReadOnlySpan<byte>` all bind directly, and the set covers both
  endiannesses of every width. Raw `BinaryPrimitives` stays only where the span is the value itself rather than a field
  at an offset: a fixed-width scratch buffer flushed to a stream (`FaolanStream`, `GlbWriter`, `FctxEncoder`) or a
  4-byte length prefix accumulated across receive boundaries (`ConanMessageFramer._lengthBuffer`).
- **An RPC message is identified by its `method_instance`** (RpcHeader field 4, one varint), and every `*Methods` enum
  member is that constant: a registration index per interface, or a 32-bit registration hash on
  PlayerAgent/PlayerInterface. `Rpc` carries `Method` and nothing else, and a dispatch is
  `switch ((GameCharInterfaceMethods)packet.Method)`.
  `0x2NNN` is the wire rendering, not the identity: the method field's tag `0x20` plus the varint's first group, which
  is what a hex dump, the Wireshark plugin and a `.tshark.json`'s `"Opcode"` field show (`DispatchAddItem = 0x20` reads
  as `0x2020`). It is exact on the index-registered classes and lossy on the hashed pair (7 of 32 bits; `0x20EC` names
  two methods), so it never keys anything: use `WireOpcode.Of(method)` to render one and `CapturedPacket.Wire(...)` to
  compare against a dumped value. `docs/protocol/gen/opcodes.md` lists both columns.
  Both directions are recovered from the client (`docs/re/aoc-rpc-method-opcodes.md`, scripts in `tools/ghidra/`):
  the c2s order is `docs/re/aoc-client-rpc-methods.json`; the s2c "Dispatch" tables are the per-class dispatcher
  switches. The full per-class catalogs are in `Faolan.Core/Enums/Opcodes.cs`. 32-bit sub-opcodes inside a PassBlob
  are BKDR-31 hashes of the client's IIR type names (`docs/re/aoc-opcode-ids.md`).
- Session-volatile values (entity handles, shop ids, conversation ids) are never persistence keys; the stable key is the
  NPC/vendor name (conversations, shops) or the character id.

**Codec conventions** (`Faolan.Core.Network/Codecs/`). These rules are binding: `Faolan.Tests/CodecConventionTests.cs`
and the gates in `Faolan.Tests/CodecSurfaceConventionTests.cs` run over every folder under `Codecs/` with no skips, and
every allowlist is empty - every codec with a standalone body parses and re-builds every captured body byte-exact. An
allowlist is not a place to silence a gate; adding an entry means naming the decode issue that removes it.
- **One codec shape: two public method names.** A codec is a `static class` exposing exactly `byte[] Build(...)` and
  `T Parse(in ReadOnlySpan<byte> data)`, plus its `Opcode` / `HandledTypes` declaration. No
  `Encode`/`Decode`/`TryParse`/`BuildBody`/`ToArray`/public `Read`/`Write`, and no `ICodec<T>`.
  - **`Build(T value)` is canonical and is the only method that writes bytes.** `Build` may be overloaded in any shape
    (loose scalars, a DB row - whatever the caller holds), but every other overload delegates to `Build(T)`. `Parse` is
    never overloaded. One encoding path per codec is what makes the round-trip gate binding.
  - **`Parse` is strict: it throws `CodecParseException`** on a short or overlong body, a wrong type word, or a field it
    cannot account for - never a null, a `false` or a default. Callers scanning a mixed stream dispatch on the type
    word at offset 0 first.
  - **The value `T` is a plain record** named for the client IIR verbatim (`MagicBuffCodec.Parse` -> `MagicBuffIIR`).
    It does not serialize itself.
  - **Composition is `internal`:** `internal static T Read(ref SpanReader)` / `internal static void Write(FaolanStream,
    T)`. `InternalsVisibleTo Faolan.Tests` is global, so these stay testable.
- **String codecs follow the same rules with `string` in place of `byte[]`.** The `remoteformat` XML layer inside a
  byte codec and the row<->XML mappers around it (`InnerElementCodec`, `RemoteFormatScreenCodec`, `NpcChatterCodec`,
  `ConversationScreenCodec`, `AttributeChangedCodec`, ...) expose `string Build(T)` / `T Parse(string)`. The `byte[]`
  form stays as a delegating overload wherever the text is the whole wire payload
  (`=> Encoding.Latin1.GetBytes(Build(value))`; Latin1 is the wire's string encoding). A codec is one shape or the
  other, never both `Parse` forms.
- **Read sequentially through `SpanReader` (`Faolan.Core/Extensions`); no const offsets; `Parse` ends with
  `EnsureAtEnd()`.** A `private const int FooOffset = 17` means the bytes before 17 were never decoded, so the codec
  cannot tell a correct body from one that merely has the right word at 17. `SpanReader` owns the bounds checks. The
  rule is about reading, so the gate covers files that declare a `Parse`: a build/patch-only neighbour
  (`SpawnTransformPatch`, `SpawnPacketBuilder`) may address a fixed-width head by offset.
- **`Build` returns the body without the length prefix.** The u32 length belongs to the framing sender.
- **Dispose the stream:** `using var s = new FaolanStream(); ... return s.ToArray();`, never
  `return new FaolanStream().Write(...).ToArray();` (`FaolanStream : MemoryStream` is `IDisposable`).
- **Every codec with a standalone body declares its identity in one uniform place:**
  `public const uint Opcode = (uint)IirTypes.<Type>;` (single type) or `public static readonly IirTypes[] HandledTypes`
  (a genuine family). An IIR opcode never appears as a bare hex literal in `Codecs/`; the test fails on it. This is what
  makes `grep IirTypes.<Type>` find every emitter, and what `docs/protocol/gen/coverage.md` reads to attribute each
  type to its builder. One IIR, one codec: two codecs declaring the same opcode must be merged.
  **RPC-message codecs** (whole method bodies: `GuildSearchResultCodec`, `MailMessageCodec`, ...) declare
  `public const uint Method = (uint)<Interface>Methods.<Method>;` on every interface, hash-registered or not, and are
  named for the client's RPC method. When several methods share one body (`DispatchTeamJoin` / `DispatchTeamLeave` /
  `DispatchSetNewTeamLeader`), declare `public static readonly <Interface>Methods[] HandledMethods` instead. There is no
  `*Opcodes` enum outside the two `ChatServer*Opcodes` (chat carries no RpcHeader, so its numbers really are opcodes).
  **Sub-record codecs** (`CharacterStatBlock`, `StatList`, `MoveTaskNode`, ...) are composed inside a larger body,
  never sent alone, and declare nothing.
  A c2s IIR whose sub-opcode travels in the RPC frame has a body that starts at the entity class; such a codec declares
  `public const bool OpcodeIsInTheFrame = true` (`DeleteItemCodec`, `MoveInventoryItemCodec`), which the surface gate
  reads.
- **Wire encode/decode lives in `Codecs/`, nowhere else.** A service, listener or importer that needs a message calls
  its codec. An IIR opcode may only be named inside `Codecs/`, plus as a `case` label of the listener's routing switch.
  The frame layer is the exception (`Protocol/Rpc.cs`, `ChatServerMessage`, the framers), because it owns the length
  prefix and CRC, which a codec must not touch.
- **Count-framed lists come from `ListFrame`, never a hand-written `0x3F1`.** A list is prefixed by a u32
  `(count + 1) * 0x3F1` (guilds, AH, quests, spell book, shortcut bar, stat lists, spawn scaffold, resurrection spots).
  A one-element list frames as `0x7E2` and an empty one as `0x3F1`; because single-element and empty lists dominate
  the captures, both read as magic constants. They never are: of the 136 functions in `AgeOfConan.exe` that touch the
  word, 77 divide by `0x3F1` to read a count and 59 multiply to write one, and none compares it to a constant
  (`tools/ghidra/DumpListFraming.java`). A value equal to `n * 0x3F1` is a list count; distrust any grammar pinned only
  by single-element or empty captures. `CodecConventionTests` enforces this across `Codecs/` and `Faolan/`.
- **Convenience forms are value factories, not `Build` overloads,** where several semantic forms share a parameter
  list (`MoveTaskCodec`'s path and patrol are both "a waypoint list plus a float", so they are `MoveTaskNodes.Path` /
  `.Patrol`). Everything next to a codec that is not encode/decode is named for what it is and stays off the codec's
  surface: row<->column mappers (`WorldObjectColumns`, `ConversationScreenColumns`), live-value policy
  (`RemoteFormatSession`, `GameClock`), route/geometry math (`PatrolRoute`). A DB-typed `Build` overload is legal (it is
  one of the shapes `Build` may take); it either delegates to a named mapper (`MailColumns.ToMessage`,
  `NpcChatterColumns.ToBark`, `ConversationScreenColumns.ToScreen`) or is a single-expression value construction.
- Files are one type per file, named after that type (a tightly cohesive group may share one file, e.g.
  `FullCharSnapshot/FullCharSnapshotSubClass.cs`), filed under their domain folder.

**Capture workflow for protocol work:** decode first, implement second. Each decoded subsystem gets a Python dissector
in `tools/capture/decode_*.py` that parses the relevant packets out of the capture JSONs and validates the grammar to
the packet end, then a C# builder proven by a byte-exact golden test in `Faolan.Tests` against the captured bytes. The
direction is "construct, don't replay": structured DB rows plus builders replace verbatim blob replay subsystem by
subsystem; remaining verbatim blobs are deliberate interims, marked as such, awaiting decode or in-client validation.
Worked example: NPC spawns are built whole from typed columns (the three-tier `NpcTemplate` type / `NpcSpawn`
placement / live `WorldEntity` model, `docs/subsystems/npc-three-tier-architecture.md`), and `TryBuildLive` is the only
spawn-body path.

**GM commands** (`Faolan/GameServer/Gm/GmCommandService.cs`, account type >= GameMaster, typed as `.cmd` in chat) are
the standard test bootstrap for features whose retail trigger is not wired yet (`.additem`, `.teleport`, `.ah`,
`.setnpcgroup`, ...). The registered set is the table `BuildTable` builds in that file (one instance per realm); check it
before assuming a command exists, since MCP tools wrapping a removed command are stale.

**MCP server:** the running server hosts an in-process MCP endpoint at `http://127.0.0.1:7099/mcp` (DB queries,
live-session inspection, GM-command routing).

## Capture data

The retail traces (~350 MB), the Ghidra project and the baked terrain live in the separate private repo
**ProjectFaolan-Data** on the same Gitea host, so this repo stays small and can go public. Clone it near this repo (it
holds `Captures/`, `Ghidra/`, `Terrain/`) and pass its `Captures/` dir to importer commands as
`--captures <data-repo>/Captures` (and `--Content:RdbDir` likewise). You only need it to rebuild the fixture archive or
to run `Faolan.Importer` and its reports. `Terrain/` is the `Content:TerrainDir` key in `faolan.json`; if it is missing
the server logs that ground-snap and line-of-sight are disabled and carries on, so check that log line rather than
assuming. The importer's DB comes from the `Database` section of faolan.json (`Database:Provider`,
`Database:ConnectionString`), the same as the server; override it with
`--Database:ConnectionString "<conn string>"`.

**The test suite does not need the data repo.** Every test runs from a plain clone, off one committed file:
`Faolan.Tests/Assets/fixtures.bin` (~2.8 MB, `FixtureArchive`), which holds every byte of capture-derived data the suite
reads. It has two halves, and the difference decides where a new gate belongs:
- **Named fixtures** (`assets/recipe-book-reply.bin`, `assets/mount-spawns.bin`, `golden/SpawnNpcCasilda.bin`, ...): a
  specific packet or one capture's worth, read via `CapturedFixture` (whole Conan packets concatenated; a packet
  self-frames), `TestPaths.AssetBytes`/`TestPaths.Asset` for inputs, or `Golden.Bytes` for expected outputs. Use these
  for a gate that only needs particular packets. They are hand-carved and cannot be re-derived from the captures, so a
  regeneration carries them over untouched.
  - Add or replace one with `--report fixture-set --report-arg <key>=<file>`; remove one with an empty source
    (`<key>=`). Once a gate's expectation has become a SHA-256 pin, remove its retail bytes from the archive
    (`EditorAvatarSnapshotTests` / `GuildPacketTests` are the pattern). A digest only substitutes for a golden the gate
    never reads: a parse input or a partial-span compare still needs the bytes.
  - Re-key one with `--report fixture-rename --report-arg <oldKey>=<newKey>`, in that single operation rather than
    set+remove, so the archive never holds the fixture under both keys or neither. Fixture keys must not carry
    character names.
- **The corpus slice** (~45k packet bodies), read via `CorpusExtract`, is for gates whose assertion is breadth: the
  snapshot round-trips, the protobuf re-encode oracle, the world-object column lift. They would be vacuous on a fixture.
  It is derived: rebuild it with the data repo present (the named half is preserved):

  ```zsh
  dotnet run --project Faolan.Importer -- --report fixtures --captures <data-repo>/Captures
  ```

  A regeneration against a partial corpus would silently weaken every whole-corpus gate while they all still pass, so
  `CorpusExtract.ExpectedCounts` is a ratchet: a regeneration may only add packets, and a short archive throws.
- **A gate comparing shipped data to the corpus belongs in a `--report`, not a test.** The slice is frozen while
  `faolan.db` is built from whatever dumps exist, so the two diverge as captures are added and such a test fails on
  data that is more correct than the evidence it demands. The `*-check` reports are those gates, run against the real
  dumps where there is no second population: `npc-pose-check`, `item-level-check`, `buff-duration-check`,
  `acg-token-check`. A test is the right home only for a property of the code (a round-trip, a field sweep), never of
  the data. A check prints its populations rather than pinning them, and asserts only the direction that can only be a
  defect (`buff-duration-check` fails on an apply that outlasts a row stating a real duration, and merely reports rows
  still at the empty placeholder). `acg-token-check` shows why: its condition is a birthday problem in the item
  population (~1.5 expected collisions per `AcgHash.Salt`, so any salt is clean ~22% of the time) that gets likelier
  with every capture harvested, and its only fix - re-rolling the salt - is a coin flip; as a report it prints the
  count, the expectation to compare against, and the next clean salt.

**The archive carries no capture file names.** `ProtocolCitationTests.V2Citations_AreWellFormed` checks a `[V2:]`
citation's form rather than resolving it, and `Captures/...` paths in docs are not linted, so no listing of the private
corpus ships with a checkout; do not add one. Each corpus entry carries a failure-provenance `s` as an opaque capture id
(the first 8 hex digits of the SHA-256 of the dump's file name), because the dumps are named after the characters
played. `--report fixture-source --report-arg <id> --captures <data-repo>/Captures` maps an id back to its dump, and
`FixtureArchiveTests` keeps a re-harvest from putting names back.

**Character names in the packet bytes** are replaced by equal-length pseudonyms that stay readable against the display
names the source and docs use: `Assas23`, `Demo80`, `Necr80` (for `AssassinL23`/`DemoL80`/`NecroL80`, squeezed to the
byte length the wire fixes). `--report fixture-names --report-arg <name>[,<name>]` reports which fixtures and IIRs still
contain a name and the framing around each hit - feed it the list the commit hook blocks on
(`.git/hooks/personal-names.json`); the residue is protobuf length-delimited fields, so an equal-length substitution
ripples nowhere. `--report fixture-substitute --report-arg "<old>=<new>[,...]" [--dry-run]` performs the substitution
(equal length enforced, framed occurrences only, verifies the old names are gone). Tests that carry the names in their
own inline retail hex (`UnsentMethodCodecTests`, `ItemCreatorNameTests`, `MailMessageCodecTests`,
`TradepostMailBodyTests`, `KnubotCodecTestData`) use the same pseudonyms, and `GuildPacketTests`' SHA-256 pin is derived
from the substituted bytes (its remark explains why that is still a retail pin). If the substitution is ever re-run:
the archive is Brotli-compressed, so an equal-length substitution still changes the file size while every body keeps
its length; and the writer rewrites the whole file, so an interrupted run leaves a truncated archive - `git checkout`
it and re-run. Capture file names in comments and docs are out of scope for this: `.githooks/personal-names.py --tree`
accepts them, and they are how a dump is found in the data repo.

## Conventions

- Commit each verified milestone immediately (conventional style, e.g. `feat(economy): ...`); `faolan.db` rides along
  in the commit.
- **A task is done when the code, the tests and the record agree.** Update whatever the change made stale:
  - **Docs:** the relevant `docs/` pages. If you changed `Opcodes.cs` or a codec, regenerate the `gen/` pages
    (`FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'`). Repoint any doc
    or code pointer you moved or deleted; add `[Vn: ...]` tags for new protocol claims.
  - **Gitea issue** (the `projectfaolan` repo): comment the outcome and close it when the work lands (or strip
    `needs-testing` per its rule after an in-client check, pass or fail). Create an issue for any follow-up, gap, or
    `[V0: ...]` unknown you surfaced, so it lives on the backlog.
  - **Agent memory:** record anything non-obvious and durable (a gotcha, a decision and its reason, a stable pointer)
    so the next session starts with it; do not record what the code or git history already shows.
- **Comments and docs describe the current system, not its history.** No "used to", "previously", issue or wave
  numbers as history, or dated narratives; git holds the history. When a past mistake taught a lasting lesson, state the
  current rule and its reason. The one exception is a present-tense pointer to open work: `Open work tracked in Gitea
  #NNN.` Prefer XML documentation (`<summary>`, `<remarks>`, `<param>`, ...) on types and members over loose `//`
  comments, and comment the why, the contract or the constraint rather than narrating the code. Evidence pointers that
  justify a constant or a byte layout - a capture name, a Ghidra address, an RDB id, a `[Vn:]` tag - are current facts,
  not history.
- Do not "refactor away" magic packet constants or verbatim payloads unless the replacement is proven byte-for-byte
  against a capture. Wrong-looking values are often capture-derived; check the comment and the referenced capture first.
- **Never commit a machine-local absolute path** (`/home/<user>/...`, `/mnt/...`, a `/tmp/claude-...` scratchpad, a
  `C:\Users\...` path) into any tracked file - code, tests, docs, scripts, config. The repo is meant to go public and
  must stay machine-agnostic. Instead:
  - **Runtime code / config:** take the location from a CLI flag or env var, falling back to a clear error. The client
    RDB dir is the `Content:RdbDir` key (`--Content:RdbDir` / env / faolan.json), blank in `Faolan.Web`'s committed
    `appsettings.json` and set in the gitignored `appsettings.Development.json` or the environment.
  - **RE / tool scripts:** write outputs to an env-var dir falling back to the system temp dir, never a hardcoded path
    and never a repo dir. Every `tools/ghidra/*.java` script resolves
    `static final String OUT = getenv("FAOLAN_GHIDRA_OUT", java.io.tmpdir) + "/"` and writes `OUT + "<name>.txt"`;
    copy that line into a new script. Python/C# equivalents: `tempfile.gettempdir()`, `Path.GetTempPath()`.
  - **Docs / prose:** use a placeholder - `<data-repo>/Captures`, `<rdb-dir>`, `$PWD/faolan.db`. Concrete local paths
    belong in agent memory only (the `no_local_paths_in_repo` memory).
  - **This checkout's own values** go in `faolan.user.json` at the repo root: gitignored, layered on top of
    `faolan.json` by the shared config builder (`Global.UserConfigPath`; `FAOLAN_USER_CONFIG` overrides), same keys and
    shapes, and it wins. It holds `Content:RdbDir` and a LAN `Cdn:PublicAddress`, so no importer command needs
    `--Content:RdbDir` / `--rdb-proxy` and the committed `faolan.json` stays machine-agnostic.
  - `run.sh` is the one deliberate exception (a personal dev launcher, kept local); its Windows twin `run.bat` takes
    `FAOLAN_CDN_STORE` / `AOC_EXE2` instead.
- **Docs carry confirmed information only; progress lives in Gitea.** No "Remaining / Next steps / Open items /
  In progress / status / handoff" sections and no `*-handoff.md` docs. Keep the verified facts (decoded grammar,
  shipped behaviour, code maps) and replace any open-work list with a one-line pointer to the Gitea issue.
- **`docs/` is organized by kind** (index: [`docs/protocol/README.md`](docs/protocol/README.md)):
  - `docs/protocol/` - the verified wire reference. Every fact carries a `[Vn: ...]` verification tag, and
    `docs/protocol/gen/` is generated from the code (opcodes, IIR types, protobuf, coverage, per-message index); never
    hand-edit it, regenerate it with
    `FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'`.
  - `docs/subsystems/` - how this server implements a domain (combat, chat, guilds, inventory, economy, ...).
  - `docs/rdb/` - client asset formats. `docs/tooling/` - launcher, capture, importer. `docs/re/` - Ghidra methodology
    and raw decode artifacts.
  Three tests keep them honest: a `[Vn: ...]` claim must cite a real artifact (`ProtocolCitationTests`); a change to
  `Opcodes.cs` must regenerate the `gen/` pages in the same commit (`ProtocolDocsTests`); and every backtick type name
  (`FooService`, `BarTests`, ...) and repo-relative file path in `docs/` must resolve (`DocReferenceTests`; add a
  retired reference to its allowlist with a reason rather than leaving it dangling).
