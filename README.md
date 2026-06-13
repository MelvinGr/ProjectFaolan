# Project Faolan

Project Faolan is a server-protocol emulator for the MMORPG *Age of Conan*, written in C# (.NET 10). It
reimplements the game's network protocol — login, character creation, world entry, inventory, combat, chat,
NPCs and the auction house — by mirroring real packet captures byte-for-byte, so a retail client can connect
to it and play.

It is a **protocol-research and preservation project**, not a finished game server. Packet captures are the
source of truth: where the emulator and a retail capture disagree, the emulator is wrong. Many subsystems are
decoded and working; others are partially implemented or gated on further reverse-engineering (see `TODO.md`).

> **Disclaimer.** This is an independent, non-commercial research project for interoperability and game
> preservation. *Age of Conan* is a trademark of Funcom; this project is not affiliated with or endorsed by
> Funcom. No game client, assets, or copyrighted content are distributed here.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) (the whole solution targets `net10.0`)
- A retail *Age of Conan* client, if you want to connect end-to-end (not included)

## Build

```sh
dotnet build ProjectFaolan.slnx
```

## Run the server

The server reads `faolan.json` via a relative path, so it must run from `bin/Debug`:

```sh
cd Faolan/bin/Debug && dotnet Faolan.dll
```

It starts every protocol service (UniverseAgent on the fixed port **7010**, plus the per-session PlayerAgent,
CsPlayerAgent, AgentServer and GameServer), migrates the database schema, and hosts an in-process MCP endpoint
on `http://127.0.0.1:7099/mcp`.

## Test

The test suite uses byte-exact "golden" round-trips against captured packets as its acceptance gates:

```sh
dotnet test Faolan.Tests
dotnet test Faolan.Tests --filter GuildPacketTests        # a single class
```

## Connect a retail client

`Faolan.ClientLauncher` launches the retail client unattended (it stands in for the patcher: computes the
launch token, auto-logs-in, and can drive straight to the world):

```sh
dotnet run --project Faolan.ClientLauncher -- --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c <char>
```

See `docs/client-launcher.md` for the full flag set and the loopback-capture testing workflow.

## Rebuild the game database

`faolan.db` (SQLite) is committed to the repo and is the single source of truth for game data; the server only
migrates and reads it. To rebuild its content from the packet captures and client RDB:

```sh
dotnet run --project Faolan.Importer -- --skip-characters    # keep --skip-characters on a populated DB
```

## Project layout

| Project | Purpose |
|---------|---------|
| `Faolan/` | The server host — runs every protocol listener as a hosted service. |
| `Faolan.Core/` | Shared networking, database, protobuf, crypto, and enums. |
| `Faolan.Importer/` | CLI that (re)builds `faolan.db` from packet captures + the client RDB. |
| `Faolan.Mcp/` | In-process MCP server exposing DB queries + live-session inspection to Claude. |
| `Faolan.ClientLauncher/` | Launches the retail client unattended (patcher stand-in). |
| `Faolan.ContentMirror/` | Store-and-forward proxy for the client's content CDN (fixes the loading hang). |
| `Faolan.Wireshark/` | A Wireshark/tshark C plugin that fully dissects the Conan protocol. |
| `Faolan.PacketAnalyzer/` | Older pcap→JSON dump tool (superseded by the Wireshark plugin). |
| `Faolan.DatabaseEditor/` | Desktop editor for `faolan.db`. |
| `Faolan.Tests/` | The unit/golden test suite. |
| `Captures/` | Reference packet traces + Python dissectors used for protocol comparison. |

## Documentation

- **`CLAUDE.md`** — architecture, build/run commands, the wire-protocol essentials, and working conventions
  (the orientation guide for anyone, human or AI, working in the codebase).
- **`TODO.md`** — the single tracker of open work, grouped by subsystem.
- **`docs/`** — per-subsystem deep-dives (protocol decoding, reverse-engineering methodology, capture workflow);
  `docs/archive/` holds resolved plans and handoffs.
- **`Faolan.Wireshark/README.md`** — building and using the dissector plugin.

## How it works (in one paragraph)

The client is handed each service's address in turn (`UniverseAgent → PlayerAgent → GameServer`); only port
7010 is fixed. Packets are constructed explicitly from `PacketStream` builders rather than replayed as blobs,
and each subsystem is proven byte-exact against a capture before it ships. Game content (items, NPCs, maps,
world bursts) lives in the committed SQLite database, built from captures by the importer. The numeric protocol
(opcodes, message names, conversation ids) was recovered by reverse-engineering the client. The deep-dive for
all of this is in `CLAUDE.md` and `docs/`.
