# AGENTS.md

Guidance for coding agents working in this repository.

Project Faolan is an Age of Conan server protocol emulator (C#, .NET 10). **Packet captures are the primary truth
source**: the code mirrors retail traffic byte-for-byte rather than abstracting it, and where local behavior diverges
from a retail capture, local is fixed to match. This file, the nested `AGENTS.md` files and `docs/` are the deep-dive
(architecture, protocol notes, RE methodology); `README.md` is the regular repo readme (overview, build, run). Open work
is tracked as **Gitea issues** in the `projectfaolan` repo on `git.groenendaal.io`, grouped by `area:*` labels, never in
a TODO file. Create and close issues there as work lands (see **Conventions**).

Rules that concern one folder live in a nested `AGENTS.md` there, which loads when you work in that folder:
- `Faolan/AGENTS.md` - the listeners and the container-registered service graph: partials by role, ports instead
  of the listener, construction cycles, how a service takes repository slices; and the world invariants.
- `Faolan.Core.Database/AGENTS.md` - the model conventions (foreign keys, natural and derived keys), how
  `faolan.sql` is generated and loaded, the EF migration workflow, and the EF/SQLite traps.
- `Faolan.Core.Network/Codecs/AGENTS.md` - the codec conventions the convention tests enforce.
- `Faolan.Importer/AGENTS.md` - how an import loses data while reporting success, what a re-harvest may delete, and
  `CapturedPacket.Record` versus `.Body`.
- `Faolan.Tests/AGENTS.md` - the fixture archive: named fixtures and the corpus slice, whether a new check is a test
  or a `--report`, the pseudonyms that replace character names in the bytes; and the test traps.

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

# EF migrations: scaffold the SQLite migration only (the maintainer's standing decision); the workflow and its
# traps are in Faolan.Core.Database/AGENTS.md.
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

**Projects:** one folder per project in `ProjectFaolan.slnx`. What the names do not say:
- The opcode/IIR catalogs are in `Faolan.Core/Enums/Opcodes.cs`; the byte-exact packet codecs are in
  `Faolan.Core.Network/Codecs/`, one folder per domain (`Spawn/`, `World/`, ...).
- `Faolan.Wireshark/` - the Conan-protocol dissector plugin: framing, zlib, RpcHeader endpoints, named opcodes and IIR
  types (generated from `Opcodes.cs`) and handoff auto-follow. It is the ground truth for captures (TCP-reassembled; use
  `-o tcp.analyze_sequence_numbers:FALSE` on lossy captures).
- `tools/` - `capture/` Python dissectors and `tshark_dump.sh`, `ghidra/` RE scripts, `frida/` live-client scripts.
- `Captures/` is not in this repo; it lives in the private ProjectFaolan-Data repo (see **Capture data**).

**Listeners and services:** the listeners route, and domain logic lives in composed, container-registered services
beside them. The rules for that graph are in `Faolan/AGENTS.md`; read them before adding a service or a port.

**Database:** SQLite via EF Core in `Faolan.Core.Database`, all access through `IDatabaseRepository` and its
per-domain slices. Lazy loading is deliberately not used; load navigations explicitly with `Include`.
- `faolan.db` carries the game data (built by the importer, committed to git); the server only migrates and
  reads/writes at save/load points. The sqlite3 CLI has foreign keys off by default: run `PRAGMA foreign_keys=ON`
  before manual deletes or you orphan child rows.

**SQLite is interim.** The goal is a real database with the schema and seed as a `.sql` file, dropping the binary.
- `faolan.sql` is the reviewable mirror of `faolan.db`'s data and the seed that loads into SQLite and Postgres. Never
  hand-edit it. **Whenever `faolan.db` changes, regenerate it** with
  `dotnet run --project Faolan.Importer -- --report sql-seed` and commit the two together: nothing regenerates it
  automatically, and the test suite reads the seed, not the db. How it is generated and loaded:
  `Faolan.Core.Database/AGENTS.md`.
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

**Codecs** (`Faolan.Core.Network/Codecs/`; the conventions are in its `AGENTS.md`, enforced by
`CodecConventionTests` and `CodecSurfaceConventionTests`). Two of them bind code outside that folder:
- **Wire encode/decode lives in `Codecs/`, nowhere else.** A service, listener or importer that needs a message calls
  its codec. An IIR opcode may only be named inside `Codecs/`, plus as a `case` label of the listener's routing switch.
  The frame layer is the exception (`Protocol/Rpc.cs`, `ChatServerMessage`, the framers), because it owns the length
  prefix and CRC, which a codec must not touch.
- **Count-framed lists come from `ListFrame`, never a hand-written `0x3F1`.** `CodecConventionTests` enforces this
  across `Codecs/` and `Faolan/`; the codec `AGENTS.md` explains why a `0x3F1`/`0x7E2` is never a magic constant.

**Capture workflow for protocol work:** decode first, implement second. Each decoded subsystem gets a Python dissector
in `tools/capture/decode_*.py` that parses the relevant packets out of the capture JSONs and validates the grammar to
the packet end, then a C# builder proven by a byte-exact golden test in `Faolan.Tests` against the captured bytes. The
direction is "construct, don't replay": structured DB rows plus builders replace verbatim blob replay subsystem by
subsystem; remaining verbatim blobs are deliberate interims, marked as such, awaiting decode or in-client validation.
Worked example: NPC spawns are built whole from typed columns (the three-tier `NpcTemplate` type / `NpcSpawn`
placement / live `WorldEntity` model, `docs/subsystems/npc-three-tier-architecture.md`), and `TryBuildLive` is the only
spawn-body path. Before a census over the dumps read `docs/re/corpus-analysis-pitfalls.md`; before naming a field
from the client, `docs/re/client-re-techniques.md`.

**GM commands** (`Faolan/GameServer/Gm/GmCommandService.cs`, account type >= GameMaster, typed as `.cmd` in chat) are
the standard test bootstrap for features whose retail trigger is not wired yet (`.additem`, `.teleport`, `.ah`,
`.setnpcgroup`, ...). The registered set is the table `BuildTable` builds in that file (one instance per realm); check it
before assuming a command exists, since MCP tools wrapping a removed command are stale.

**MCP server:** with `Mcp:Enabled` set (off by default) the server hosts an in-process MCP endpoint at
`http://127.0.0.1:7099/mcp` (DB queries,
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

**Measuring against captures:**
- The corpus is retail-only: every dump is the client talking to the live official service. "Retail" always names
  that service, never a client (there is one client). A loopback capture of this server is a debugging aid and never
  joins `Captures/`, so nothing needs to defend against importing our own output.
- Measure against the dumps (`<data-repo>/Captures/*.tshark.json`), never against `fixtures.bin`. The corpus slice
  is IIR-only and sampled, so "not in the corpus" is never evidence that a message was never captured.
  `tools/capture/captures.py` resolves the directory from `FAOLAN_CAPTURES`; stream the files one at a time, and
  prove the parser consumes each body exactly before believing a negative result.
- Read every fact off the wire. A capture's file name is a human label, often wrong about the character, class or
  action it seems to state: never group, filter or derive anything from it. Identify a character by its id on the
  wire (a character missing from `faolan.db` is unknown, not the one the file name suggests).
- Never identify a service tier by port, since retail's ports differ from ours: use the dump's `Protocol` column and
  the frame's endpoint class. Nothing in a dump names a packet's recipient (`CapturedPacket.Port` is the server
  side's port, shared by every client of a multi-client dump), so a per-viewer hypothesis cannot be tested from it.
- On a capture, `handle >= EntityHandles.CharacterInstanceBase` does not separate players from NPCs: that is our
  allocator's boundary, and retail's character ids sit below it. A player's spawn carries `player_stats`.
- The `Method` string in a dump is frozen at the Wireshark plugin's last build. Key on `(ReceiverClass, Opcode)` as
  `ProtocolCatalog` resolves it, and re-dump after renaming a member of `Opcodes.cs`.
- The client install is static (unpatched for years): when data seems to have changed, the change is on our side -
  an extractor, a report, the CDN mirror, or the premise. A number seen in two forms is usually two id spaces, not
  two versions.

**The test suite does not need the data repo.** Every test runs from a plain clone, off one committed file:
`Faolan.Tests/Assets/fixtures.bin` (`FixtureArchive`). How it splits into named fixtures and the corpus slice, how to
add, rename or regenerate them, and whether a new check belongs in a test or a `--report`: `Faolan.Tests/AGENTS.md`.
The archive and its fixture keys carry no capture file names and no character names; do not add a listing of the
private corpus.

## Conventions

- Commit each verified milestone immediately (conventional style, e.g. `feat(economy): ...`); `faolan.db` rides along
  in the commit, with `faolan.sql` regenerated beside it.
- **A task is done when the code, the tests and the record agree.** Update whatever the change made stale:
  - **Docs:** the relevant `docs/` pages. If you changed `Opcodes.cs` or a codec, regenerate the `gen/` pages
    (`FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'`). Repoint any doc
    or code pointer you moved or deleted; add `[Vn: ...]` tags for new protocol claims.
  - **Gitea issue** (the `projectfaolan` repo): comment the outcome and close it when the work lands. Behaviour that
    is built but not yet run in-client stays **open** with the `needs-testing` label and a comment naming the
    concrete check (which NPC, which quest, what should be seen); the label is only for built work. After the
    in-client check, remove the label whatever the outcome: on a pass comment and close, on a failure comment the
    repro and keep the issue open. Create an issue for any follow-up, gap, or `[V0: ...]` unknown you surfaced, so
    it lives on the backlog. A pushed `fix(...)` commit whose subject names `(#NNN)` closes that issue; for a ticket
    that must stay open, put `Refs #NNN` in the body instead.
  - **Rules and gotchas** a contributor needs go into the `AGENTS.md` that covers the folder (or this file), never
    only into one agent's private memory. Do not record what the code or git history already shows.
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
- **Never commit the maintainer's retail account or character names** - in source, docs, `faolan.sql` or fixtures -
  since the repo is meant to go public. Fixtures carry the pseudonyms listed in `Faolan.Tests/AGENTS.md`; nothing
  checks this on commit.
- **Licensing:** the repo is AGPL-3.0-or-later (`LICENSE`). `Faolan.Wireshark/` is GPL-2.0-or-later, because it
  links libwireshark: it has its own `LICENSE`, and every source file there starts with an SPDX line (the generated
  method-name header too). `NOTICE` lists all material the AGPL does not cover (the Funcom-derived game data, the
  CC BY-SA wiki scrape, the archived GPL sources); a new file of third-party code or data gets an entry there. C#
  sources carry no per-file licence header.
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
