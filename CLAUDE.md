# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Project Faolan is an Age of Conan server protocol emulator (C#, .NET 10). **Packet captures are the
primary truth source** — the code intentionally mirrors retail traffic byte-for-byte rather than
abstracting it; if local behavior diverges from a retail capture, fix local to match. This file plus `docs/`
are the deep-dive (architecture, protocol notes, RE methodology); `README.md` is the regular repo readme
(overview + build/run). Open work is tracked as **Gitea issues** (`melvin/projectfaolan` on
`git.groenendaal.io`), grouped by `area:*` labels — NOT in a TODO file. Create/close issues there as work
lands (see the agent memory `issue_tracking_in_gitea` for the API recipe). Inline `TODO §N` / `TODO.md`
mentions still scattered through **code comments** are historical pointers (left as-is, not a live file); the
`docs/` ones were repointed at Gitea.

## Commands

```zsh
# Build (repo root)
dotnet build ProjectFaolan.slnx

# Tests (xUnit; byte-exact golden round-trips are the acceptance gates)
dotnet test Faolan.Tests
dotnet test Faolan.Tests --filter GuildPacketTests          # single class
dotnet test Faolan.Tests --filter "FullyQualifiedName~Shop" # by substring

# Run the server (cwd MUST be the repo root: Program.cs loads faolan.json + faolan.db from the CWD,
# and both live at the repo root). Running from bin/Debug fails with "faolan.json not found".
dotnet run --project Faolan

# Drive the retail client unattended against the local server (auto-login -> world)
dotnet run --project Faolan.ClientLauncher -- --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c <char>

# Rebuild DB content from captures. The importer is MODULE-BASED (docs/tooling/importer-plan.md): each subsystem
# is a selectable module; `--dry-run` writes nothing (runs in a rolled-back txn, prints the per-table delta).
dotnet run --project Faolan.Importer -- --skip characters     # idempotent EXCEPT characters; skip on a populated DB
dotnet run --project Faolan.Importer -- --list                # catalog every module (+ which are opt-in)
dotnet run --project Faolan.Importer -- --only npcs --mode overwrite   # one module; overwrite = the old --reseed-npcs
dotnet run --project Faolan.Importer -- --only rdb-maps --rdb <dir>    # rdb-* + mounts are OPT-IN (not in the default run)
dotnet run --project Faolan.Importer -- --report burst-coverage       # read-only diagnostics (was --burst-coverage / --dump-*)
# Modes: --mode insert|upsert(default)|overwrite (per-module: --mode npcs=overwrite,items=insert). NO old-flag
# aliases - the legacy one-off flags (--reseed-*, --rdb-maps, --archive, --dump-*) don't exist; use the module CLI.

# Wireshark plugin: full Conan+chat dissection (auto-follows the session from port 7010);
# build+install once, then open captures in Wireshark (filter: faolan) or dump to JSON.
cmake -S Faolan.Wireshark -B Faolan.Wireshark/build && cmake --build Faolan.Wireshark/build -j \
  && cmake --build Faolan.Wireshark/build --target install-faolan-plugin
Captures/scripts/tshark_dump.sh Captures/<capture>.pcapng   # the canonical *.tshark.json the importer reads:
# TCP-reassembled, one record per packet, includes the compressed DL packets.
# Lossy captures in the Wireshark GUI: set TCP pref analyze_sequence_numbers off (the script does).

# EF migrations (context lives in Faolan.Core.Database, migrations in Faolan/Migrations).
dotnet ef migrations add <Name> --project Faolan
# GOTCHA: plain `database update` runs with CWD = Faolan/ and creates a stray Faolan/faolan.db.
# Use the absolute connection override (or just start the server - DatabaseMigrator migrates at startup):
dotnet ef database update --project Faolan --connection "Data Source=$PWD/faolan.db"
# For pure DATA changes (no schema change) just UPDATE faolan.db directly and commit it - no migration.
```

## Architecture

**Server flow:** `UniverseAgent (7010, the only fixed port) -> PlayerAgent -> GameServer`. Each service
hands the client the next address in its payload. One host (`Faolan/Program.cs`) runs every protocol
service as a hosted service: `UniverseAgentListener`, `PlayerAgentListener`, `CsPlayerAgentListener`
(guilds/teams/groupfinder), `AgentServerListener` (chat), `GameServerListener` (the world; one listener
serves both the create-editor and world connections).

**Projects:** `Faolan/` server
host · `Faolan.Core/` BASE shared types + crypto + the `Protobuf/` schema (the layer everything builds on) ·
`Faolan.Core.Database/` EF Core DB — `DatabaseContext`, `Tables/`, `DatabaseRepository` · `Faolan.Core.Network/`
networking + the byte-exact packet CODECS (`Codecs/` by domain: `Spawn/`, `World/`, …) + `Constants/Opcodes.cs`
· `Faolan.Core.Rdb/` client RDB extraction · `Faolan.Importer/` tshark-dump+RDB -> DB CLI ·
`Faolan.ClientLauncher/` unattended retail client · `Faolan.Mcp/` in-process MCP server (DB + live-session +
GM-command tools) · `Faolan.Web/` + `Faolan.Web.Content/` Blazor armory/3D-viewer + GM tools + CDN proxy ·
`Faolan.Tests/` the test suite · `Faolan.Wireshark/` full Conan-protocol dissector plugin - framing + zlib +
RpcHeader endpoints + named opcodes/IIR types (generated from `Opcodes.cs`) + handoff auto-follow (ground truth, TCP-reassembled; use
`-o tcp.analyze_sequence_numbers:FALSE` on lossy captures) · `Captures/` reference traces + Python dissectors.
(The old C# `Faolan.PacketAnalyzer` GUI/CLI was retired in #82 - it had no TCP reassembly and dropped ~2/3 of
the compressed GameServer downloads; the Wireshark plugin + `Captures/scripts/tshark_dump.sh` fully replace it.)

**Listeners** are partial classes split across many files — inspect all partials before changing
behavior. `GameServerListener`'s partials are organized by role: `Inbound/` (opcode handlers, `Handle*`),
`Outbound/` (packet builders, **behavior-named** e.g. `VitalsUpdateBuilder`/`GuildTagBuilder` — never
opcode-named), and the per-flow/domain folders (`Login/`, `CreateFlow/`, `Combat/`, `Economy/`, `World/`,
`Inventory/`, `Dialogue/`, `Quest/`, `Session/`, `Spawn/`, `Scripting/`); GM commands are the
`GameServerListener.GmCommands.cs` partial. NB a partial-class file keeps
`namespace Faolan.GameServer` regardless of its folder (the `// CheckNamespace` suppression), so its folder
is organizational only.

**Prefer composed collaborators over growing the partial.** Domain logic is pulled OUT of the listener into
folder-namespaced service classes that take their deps by ctor and the client per call:
`CombatService` (Combat/), `WorldBroadcaster` + `WorldSimulation` + `PlayerPersistenceService` (World/),
`EquipmentService` (Inventory/), `ConversationService` (Dialogue/), and
`ShopService`/`BankService`/`TradepostService` (Economy/). The
listener holds them as `_combat`/`_shop`/... fields and forwards. When a service needs a cross-cutting
OUTBOUND op that lives on the listener (e.g. the wallet update or the live item-grant, shared by several
subsystems), reach it through a **narrow port the listener implements** rather than passing the whole
listener — keeps the service free of the god-class while making its dependency surface explicit. A single
shared op can be a `Func<…>` delegate seam (e.g. `ClientForCharacter` into `GuildService`); once several
services share the same cluster of ops, collapse them into one named interface the listener implements
(`IEconomyOutbound` = wallet + item-grant for Shop/Bank/Tradepost; `IWorldOutbound` = the simulation tick's
wire side). Do NOT pass the listener itself — that re-introduces the circular dependency the extraction
removed. (See Gitea #200 for the remaining extraction backlog.)

Listeners inherit `Server<TPacket,...>` (`Faolan.Core.Network/Server.cs`); per-connection state lives on
`INetworkClient.Session`. Cross-listener actions go through dispatcher seams (`IAgentServerDispatcher` for
chat/system messages, `IGameServerDispatcher` for world actions), never direct references.

**Database:** SQLite via EF Core (`Faolan.Core.Database`: `DatabaseContext` + `Tables/`), pooled
`DbContextFactory`, short-lived per-unit-of-work contexts — lazy loading is deliberately NOT used; load
navigations explicitly with `Include`. All access goes through `IDatabaseRepository`. `faolan.db` carries the game data (built by the importer, committed to
git) and the server only migrates + reads/writes at save/load points. The sqlite3 CLI has foreign keys
OFF by default — `PRAGMA foreign_keys=ON` before manual deletes or you orphan child rows.

**Wire protocol essentials:**

- `PacketStream.WriteHeader(...)` + `SendAsync(...)` computes length + CRC; build packets with
  `PacketStream`/`FaolanStream` helpers. Integers are big-endian; protobuf fixed32 floats are
  little-endian while raw-body floats are big-endian (`WriteFloatLittleEndian` / `WriteFloat` BE are both
  intentional).
- 16-bit RPC opcodes are **method indices**: `wire = 0x2000 + registration index`, per interface, base
  0x2000 for every interface except PlayerAgent/PlayerInterface (32-bit hash dispatch — login, still open).
  Both directions are recovered from the client (`docs/re/aoc-rpc-method-opcodes.md`, scripts in
  `Captures/ghidra/`): c2s order == `docs/re/aoc-client-rpc-methods.json`; the s2c "Dispatch" tables are the
  per-class dispatcher switches (the old "drift" was just extra no-arg dispatch methods the string catalog
  missed). Full per-class catalogs are in `Faolan.Core.Network/Constants/Opcodes.cs`. 32-bit sub-opcodes
  inside 0x2000 are BKDR-31 hashes of the client's IIR type names (`docs/re/aoc-opcode-ids.md`).
- Session-volatile values (entity handles, shop ids, conversation ids) must never be persistence keys;
  the stable key is the NPC/vendor NAME (conversations, shops) or the character id.

**Codec conventions** (`Faolan.Core.Network/Codecs/`, enforced by `Faolan.Tests/CodecConventionTests.cs`):

- Codecs come in **two shapes** — don't try to unify them under one interface (a C# `static class` cannot
  implement one, and most codecs are static):
  - **Wire value (DTO):** an instance `record`/`class` that knows its own layout → implements `ICodec<T>`
    (`Write(stream)` + `static Read(stream)`, a matched round-trip pair).
  - **Codec module:** a `static class` of stateless ops → `Build(...)` / `Parse(...)` / `TryParse(...)`.
    No interface.
- **Every codec that emits an IIR declares which one**, in one uniform place: `public const uint Opcode =
  (uint)GameServer0X2000Opcodes.<Type>;` (single type) or `public static readonly GameServer0X2000Opcodes[]
  HandledTypes` (more than one). An IIR opcode must **never** appear as a bare hex literal in `Codecs/` —
  the test fails on it. This is what makes `grep GameServer0X2000Opcodes.<Type>` find every emitter, and it
  is what `docs/protocol/gen/coverage.md` reads to attribute each type to its builder.
- Codecs with no opcode (sub-record helpers: `CharacterStatBlock`, `StatList`, `SnapshotItemBuilder`) declare
  nothing — that's fine and expected.
- Files are one-type-per-file named after that type (plural `*Codecs.cs` for a cohesive group, e.g.
  `DeadReckoningCodecs.cs`), filed under their domain folder.

**Capture workflow for protocol work:** decode first, implement second. Each decoded subsystem gets a
Python dissector in `Captures/scripts/decode_*.py` that parses the relevant packets out of the capture JSONs and
validates the grammar to the packet end, then a C# builder proven by a byte-exact golden test in
`Faolan.Tests` against the captured bytes. Direction is "construct, don't replay": structured DB rows +
builders replace verbatim blob replay subsystem by subsystem; remaining verbatim blobs are deliberate
interims (marked as such) awaiting decode or in-client validation. Worked example: NPC spawns build
WHOLE from typed columns (the three-tier `Npc` type / `NpcSpawn` placement / live `WorldEntity` model —
`docs/subsystems/npc-three-tier-architecture.md`); the captured `NpcSpawn.Data` blob is now **retired** — `TryBuildLive`
is the sole spawn-body path.

**GM commands** (`Faolan/GameServer/GameServerListener.GmCommands.cs`, account type >= GameMaster, typed as `.cmd` in chat)
are the standard test bootstrap for features whose retail trigger isn't wired yet (`.ah`, `.shop`,
`.guild create`, `.money`, `.teleport`, ...).

**MCP server:** the running server hosts an in-process MCP endpoint at `http://127.0.0.1:7099/mcp`
(DB queries + live-session inspection + GM-command routing).

## Conventions

- Commit each verified milestone immediately (conventional style, e.g. `feat(economy): ...`);
  `faolan.db` rides along in the commit.
- **When a task is done, close the loop before moving on** — a task isn't finished until the code, the tests,
  and the *record* of it agree. Update whatever the change made stale:
  - **Docs:** the relevant `docs/` page(s); if you changed `Opcodes.cs` or a codec, regenerate the `gen/` pages
    (`FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`); repoint any doc/code pointer you
    moved or deleted; add/verify `[Vn: …]` tags for new protocol claims.
  - **Gitea issue** (`melvin/projectfaolan`): comment the outcome and **close** it when the work lands (or strip
    `needs-testing` per its rule after an in-client check — pass OR fail); **create** an issue for any follow-up,
    gap, or `[V0: …]` unknown you surfaced, so it lives on the backlog, not just in your head.
  - **Agent memory:** if you learned something *non-obvious and durable* (a gotcha, a decision and its why, a
    stable pointer), write or update a memory file so the next session starts with it — but don't record what
    the code/git history already shows.
- Don't "refactor away" magic packet constants or verbatim payloads unless the replacement is proven
  byte-for-byte against a capture. Wrong-looking values are often capture-derived; check the comment
  and the referenced capture first.
- **`docs/` is organized by kind** (see [`docs/protocol/README.md`](docs/protocol/README.md) for the index):
  - `docs/protocol/` — the **verified wire reference**: every fact carries a `[Vn: …]` verification tag a test
    resolves, and `docs/protocol/gen/` is GENERATED from the code (opcodes, IIR types, protobuf, coverage,
    per-message index) — never hand-edit those; regenerate with
    `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.
  - `docs/subsystems/` — how OUR server implements a domain (combat, chat, guilds, inventory, economy, …).
  - `docs/rdb/` — client asset formats · `docs/tooling/` — launcher/capture/importer · `docs/re/` — Ghidra
    methodology + raw decode artifacts.
  Two tests keep this honest: a `[Vn: …]` claim must cite a real artifact (`ProtocolCitationTests`), and a
  change to `Opcodes.cs` must regenerate the `gen/` pages in the same commit (`ProtocolDocsTests`).
