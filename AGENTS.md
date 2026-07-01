# AGENTS.md

## Big picture

- `Project Faolan` is an `Age of Conan` protocol emulator. Treat packet captures as the primary truth source; the code
  intentionally mirrors retail traffic rather than abstracting everything cleanly.
- Main projects: `Faolan/` (server host), `Faolan.Core/` (shared networking, DB, crypto, enums),
  `Faolan.PacketAnalyzer/` (pcap/json analysis tool), `Faolan.DatabaseEditor/` (Avalonia DB editor),
  `Faolan.PacketAnalyzer.Tests/` (xUnit tests for analyzer only).
- Canonical server progression is `UniverseAgent -> PlayerAgent -> GameServer` (see `README.md`). When debugging
  login/handoff issues, trace that order first.
- `Faolan/Program.cs` wires the server as hosted services: `UniverseAgentListener`, `PlayerAgentListener`,
  `CsPlayerAgentListener`, `AgentServerListener`, plus one `GameServerListener` bound to `realm.Port` loaded from the
  database.

## Runtime/data boundaries

- Ports and paths come from `faolan.json`; access them through `Faolan.Core/Extensions/IConfigurationExtensions.cs`, not
  ad hoc string reads.
- DB access is centralized through `IDatabaseRepository` / `DatabaseRepository` (
  `Faolan.Core/Database/DatabaseRepository.cs`) over SQLite + EF Core. `DatabaseMigrator` runs automatically on startup.
- Current working directory matters, it should be the solution root
- `GameServerListener` depends on `IAgentServerDispatcher` (`Faolan.Core/Network/AgentServer/AgentServerDispatcher.cs`)
  to send chat/system messages through the separate AgentServer channel.

## Packet/code patterns to follow

- Most listeners are split across partial classes (organized under `Inbound/`, `Outbound/`, `Gm/`, retail burst files,
  etc.). Before changing behavior, inspect all partials for that listener. Domain logic increasingly lives in composed
  collaborator services (`CombatService`, `ShopService`, `ConversationService`, …) rather than the partial — see
  CLAUDE.md.
- Network services inherit `Server<TPacket,...>` (`Faolan.Core/Network/Server.cs`); connection/session state lives on
  `INetworkClient.Session` (`Faolan.Core/Network/NetworkClientSession.cs`).
- Build packets with `PacketStream` / `FaolanStream`, not raw byte math where helpers exist. Important quirks: integers
  are big-endian, but `WriteFloat` is intentionally little-endian (`Faolan.Core/Data/FaolanStream.cs`).
- `PacketStream.WriteHeader(...)` plus `SendAsync(...)` computes length + CRC automatically. If you bypass
  `PacketStream`, you must already have a full wire packet.
- The project still contains intentional hardcoded/verbatim packet payloads. Example: the retail create-flow
  editor/world-entry bursts are stored as verbatim wire bytes in `Faolan/GameServer/CreateFlow/` (
  `RetailCreateEditor*Burst.cs`, `RetailCreateWorldEntryBurst.cs`). Their IIR grammar is now decoded (
  `docs/create-editor-bursts.md`, dissector `Captures/scripts/decode_create_editor.py`); the verbatim replay stays as
  the interim until each record is constructed + proven byte-exact.
- Preserve capture-specific conversation IDs and temporary IDs unless a capture proves otherwise. Example:
  `PlayerAgentListener` forces retail create temporary client instance `0x02165EFF` during character creation.

## Project-specific behavior that surprises newcomers

- Database contents can explain repeated retail IDs. `DatabaseRepository.CreateCharacter(...)` reuses an existing
  `Character` row where `Name == null` before inserting a new row.
- Lazy-loading proxies are enabled in `DatabaseContext.OnConfiguring`; navigation properties may trigger DB access
  implicitly.
- `GameServerListener` contains create-flow state machines keyed by session flags such as `HasPendingPost205FFollowUp`,
  `HasFlushedCreateWorldEntryBurst`, and request counters; keep those in sync when changing the create/login handshake.
- GM chat commands live in `Faolan/GameServer/GMCommands.cs` and are only available when the account type is at least
  `GameMaster`.

## Workflows that matter here

- Build the solution from repo root:
  ```zsh
  cd '/home/melvin/Development/C#/Project Faolan'
  dotnet build '/home/melvin/Development/C#/Project Faolan/ProjectFaolan.slnx' -v q
  ```
- Run analyzer tests:
  ```zsh
  dotnet test '/home/melvin/Development/C#/Project Faolan/Faolan.PacketAnalyzer.Tests/Faolan.PacketAnalyzer.Tests.csproj' -v q
  ```
- Dump a capture to JSON before changing packet logic:
  ```zsh
  dotnet run --project '/home/melvin/Development/C#/Project Faolan/Faolan.PacketAnalyzer/Faolan.PacketAnalyzer.csproj' -- --dump '/home/melvin/Development/C#/Project Faolan/Captures/<capture>.pcapng'
  ```
- `Faolan.PacketAnalyzer/PacketCapturePortDetector.cs` currently hardcodes known port mappings instead of inferring them
  dynamically; adjust that if new capture layouts are introduced.
- Use `deleteBinObj.sh` when stale `bin/`/`obj/` output causes confusion. `build.sh` / `Dockerfile` build the server
  container with .NET 10 SDK images.

## When making changes

- Prefer pcap-backed edits over speculative protocol cleanup; compare against `Captures/*.pcapng` or existing dumped
  `.json` files.
- For handshake or world-entry bugs, inspect both packet builders and DB state (`faolan.db`), because this codebase
  intentionally mixes persisted state with verbatim retail packet replay.
- Keep edits narrow and style-consistent; avoid “refactoring away” magic packet constants unless you can prove the
  replacement matches the retail capture byte-for-byte.
