# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Project Faolan is an Age of Conan server protocol emulator (C#, .NET 10). **Packet captures are the
primary truth source** — the code intentionally mirrors retail traffic byte-for-byte rather than
abstracting it; if local behavior diverges from a retail capture, fix local to match. `README.md` is the
deep-dive (architecture, protocol notes, RE methodology); `TODO.md` is the single tracker of open work.

## Commands

```zsh
# Build (repo root)
dotnet build ProjectFaolan.slnx

# Tests (xUnit; byte-exact golden round-trips are the acceptance gates)
dotnet test Faolan.Tests
dotnet test Faolan.Tests --filter GuildPacketTests          # single class
dotnet test Faolan.Tests --filter "FullyQualifiedName~Shop" # by substring

# Run the server (cwd MUST be bin/Debug - faolan.json is loaded via ../../../)
cd Faolan/bin/Debug && dotnet Faolan.dll

# Drive the retail client unattended against the local server (auto-login -> world)
dotnet run --project Faolan.ClientLauncher -- --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c <char>
# On client failures grab AgeOfConan.log (look for: stream bad, Dispatcher call ... failed,
# invalid camera path, does not exist in any RDB). Restore `git checkout -- faolan.db` after runs.

# Rebuild DB content from captures (idempotent EXCEPT characters - keep --skip-characters on a populated DB)
dotnet run --project Faolan.Importer -- --skip-characters    # [--captures dir] [--rdb dir] [--archive dir]

# Dump a pcap to the JSON the dissectors read
dotnet run --project Faolan.PacketAnalyzer -- --dump Captures/<capture>.pcapng

# EF migrations (context lives in Faolan.Core, migrations in Faolan/Migrations)
dotnet ef migrations add <Name> --project Faolan
# GOTCHA: plain `database update` runs with CWD = Faolan/ and creates a stray Faolan/faolan.db.
# Use the absolute connection override (or just start the server - DatabaseMigrator migrates at startup):
dotnet ef database update --project Faolan --connection "Data Source=$PWD/faolan.db"
```

## Architecture

**Server flow:** `UniverseAgent (7010, the only fixed port) -> PlayerAgent -> GameServer`. Each service
hands the client the next address in its payload. One host (`Faolan/Program.cs`) runs every protocol
service as a hosted service: `UniverseAgentListener`, `PlayerAgentListener`, `CsPlayerAgentListener`
(guilds/teams/groupfinder), `AgentServerListener` (chat), `GameServerListener` (the world; one listener
serves both the create-editor and world connections).

**Projects:** `Faolan/` server host · `Faolan.Core/` shared networking/DB/protobuf/crypto ·
`Faolan.Importer/` pcap+RDB -> DB CLI · `Faolan.PacketAnalyzer/` pcap -> JSON ·
`Faolan.ClientLauncher/` unattended retail client · `Faolan.Tests/` the test suite ·
`Faolan.Wireshark/` full Conan-protocol dissector plugin - framing + zlib + RpcHeader endpoints + raw
opcodes + handoff auto-follow (ground truth; the analyzer has no TCP reassembly and drops ~2/3 of
compressed GameServer downloads; use `-o tcp.analyze_sequence_numbers:FALSE` on lossy captures) ·
`Captures/` reference traces + Python dissectors.

**Listeners** are partial classes split across many files (`Packets.cs`, `GMCommands.cs`,
`GuildHandlers.cs`, burst files...) — inspect all partials before changing behavior. They inherit
`Server<TPacket,...>` (`Faolan.Core/Network/Server.cs`); per-connection state lives on
`INetworkClient.Session`. Cross-listener actions go through dispatcher seams (`IAgentServerDispatcher`
for chat/system messages, `IGameServerDispatcher` for world actions), never direct references.

**Database:** SQLite via EF Core, pooled `DbContextFactory`, short-lived per-unit-of-work contexts —
lazy loading is deliberately NOT used; load navigations explicitly with `Include`. All access goes
through `IDatabaseRepository`. `faolan.db` carries the game data (built by the importer, committed to
git) and the server only migrates + reads/writes at save/load points. The sqlite3 CLI has foreign keys
OFF by default — `PRAGMA foreign_keys=ON` before manual deletes or you orphan child rows.

**Wire protocol essentials:**
- `PacketStream.WriteHeader(...)` + `SendAsync(...)` computes length + CRC; build packets with
  `PacketStream`/`FaolanStream` helpers. Integers are big-endian; protobuf fixed32 floats are
  little-endian while raw-body floats are big-endian (`WriteFloat` LE / `WriteFloatBigEndian` are both
  intentional).
- The GameServer connection multiplexes ~4 RPC class-pairs; the dispatch key is **(endpoint class id,
  16-bit opcode)** — the same opcode means different methods per class. Any GameServer opcode >= 0x2014
  is unambiguously GameCharAgent. Tradepost routes class-first (`TradepostProtocol.IsInbound`); add
  class guards surgically, don't rewrite the opcode switch.
- 16-bit RPC opcodes are **method indices** (0x2000 + position) into the recovered client→server tables
  (`docs/aoc-client-rpc-methods.json`); full per-class catalogs are in
  `Faolan.Core/Network/Packets/Opcodes.cs`. Server→client tables DRIFT in the live build — name
  downloads by decoded content, never by table index. 32-bit sub-opcodes inside 0x2000 are BKDR-31
  hashes of the client's IIR type names (`docs/aoc-opcode-ids.md`).
- Session-volatile values (entity handles, shop ids, conversation ids) must never be persistence keys;
  the stable key is the NPC/vendor NAME (conversations, shops) or the character id.

**Capture workflow for protocol work:** decode first, implement second. Each decoded subsystem gets a
Python dissector in `Captures/decode_*.py` that parses the relevant packets out of the capture JSONs and
validates the grammar to the packet end, then a C# builder proven by a byte-exact golden test in
`Faolan.Tests` against the captured bytes. Direction is "construct, don't replay": structured DB rows +
builders replace verbatim blob replay subsystem by subsystem; remaining verbatim blobs are deliberate
interims (marked as such) awaiting decode or in-client validation.

**GM commands** (`Faolan/GameServer/GMCommands.cs`, account type >= GameMaster, typed as `.cmd` in chat)
are the standard test bootstrap for features whose retail trigger isn't wired yet (`.ah`, `.shop`,
`.guild create`, `.money`, `.teleport`, ...).

**MCP server:** the running server hosts an in-process MCP endpoint at `http://127.0.0.1:7099/mcp`
(DB queries + live-session inspection + GM-command routing).

## Conventions

- Commit each verified milestone immediately (conventional style, e.g. `feat(economy): ...`);
  `faolan.db` rides along in the commit.
- Don't "refactor away" magic packet constants or verbatim payloads unless the replacement is proven
  byte-for-byte against a capture. Wrong-looking values are often capture-derived; check the comment
  and the referenced capture first.
- Protocol knowledge lives in `docs/` (per-subsystem .md files + `docs/archive/` for resolved plans);
  update the relevant doc and `TODO.md` when a subsystem's status changes.

Note: `AGENTS.md` predates parts of the current code; where it conflicts (lazy-loading proxies,
test-project layout), this file and the code comments win.
