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

# Rebuild DB content from captures. The importer is MODULE-BASED (docs/tooling/importer.md): each subsystem
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
tools/capture/tshark_dump.sh Captures/<capture>.pcapng   # the canonical *.tshark.json the importer reads:
# TCP-reassembled, one record per packet, includes the compressed DL packets.
# Lossy captures in the Wireshark GUI: set TCP pref analyze_sequence_numbers off (the script does).

# EF migrations (context lives in Faolan.Core.Database; the migrations themselves are PER PROVIDER, in
# Faolan.Core.Database.Migrations.{Sqlite,Postgres}/Migrations - scaffold into the one matching the provider).
# GOTCHA: the EF tools run design time with CWD = the PROJECT dir, so the design-time factory can't see the
# repo-root faolan.json - point FAOLAN_CONFIG at it (every exe reads Global.ConfigPath: "faolan.json" beside
# the exe / in the CWD, or whatever FAOLAN_CONFIG names):
FAOLAN_CONFIG=$PWD/faolan.json dotnet ef migrations add <Name> \
  --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
# Same CWD gotcha for `database update`: a relative `Data Source=faolan.db` would create a stray DB beside the
# project. Use the absolute connection override (or just start the server - it migrates + seeds at startup):
FAOLAN_CONFIG=$PWD/faolan.json dotnet ef database update \
  --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite \
  --connection "Data Source=$PWD/faolan.db"
# For pure DATA changes (no schema change) just UPDATE faolan.db directly and commit it - no migration.
```

## Architecture

**Server flow:** `UniverseAgent (7010, the only fixed port) -> PlayerAgent -> GameServer`. Each service
hands the client the next address in its payload. `Faolan/Program.cs` runs the protocol services as hosted
services: `UniverseAgentListener`, `PlayerAgentListener` (the login/router tier), `CsPlayerAgentListener`
(guilds/teams/groupfinder), `ChatServerListener` (chat), `GameServerListener` (the world; one listener
serves both the create-editor and world connections). **Process role (multi-realm, #324 —
`docs/subsystems/multi-realm.md`):** by default `dotnet run --project Faolan` is *all-in-one* (login +
a game host for the first realm — the historical single-process dev run); `--role login` runs ONLY the
router tier; `--role realm=<id>` runs ONE realm's game host (GameServer + realm-local chat + guilds, bound to
that `Realm` row's ports). `--role` is **repeatable** — `--role login --role realm=<id>` co-hosts both in one
process — but **at most one realm role per process** (co-hosting realms would leak chat/session state across
them). So one login + N realm processes compose a multi-realm deployment against the shared DB;
guild/AH/mail/character-name are realm-scoped (`RealmId`), routed by the character's realm.

**Projects:** `Faolan/` server
host · `Faolan.Core/` BASE shared types + crypto + the `Protobuf/` schema (the layer everything builds on) ·
`Faolan.Core.Database/` EF Core DB — `DatabaseContext`, `Tables/`, `DatabaseRepository` · `Faolan.Core.Network/`
networking + the byte-exact packet CODECS (`Codecs/` by domain: `Spawn/`, `World/`, …) + `Constants/Opcodes.cs`
· `Faolan.Core.Rdb/` client RDB extraction · `Faolan.Importer/` tshark-dump+RDB -> DB CLI ·
`Faolan.ClientLauncher/` unattended retail client · `Faolan.Mcp/` in-process MCP server (DB + live-session +
GM-command tools) · `Faolan.Web/` + `Faolan.Web.Content/` Blazor armory/3D-viewer + GM tools + CDN proxy ·
`Faolan.Tests/` the test suite · `Faolan.Wireshark/` full Conan-protocol dissector plugin - framing + zlib +
RpcHeader endpoints + named opcodes/IIR types (generated from `Opcodes.cs`) + handoff auto-follow (ground truth, TCP-reassembled; use
`-o tcp.analyze_sequence_numbers:FALSE` on lossy captures) · `Captures/` reference traces - NOT in this repo (they live in the private ProjectFaolan-Data repo; the suite reads `Faolan.Tests/Assets/corpus-extract.bin` instead, see **Capture data** below) ·
`tools/` the tooling that reads them: `capture/` Python dissectors + `tshark_dump.sh`, `ghidra/` RE scripts,
`frida/` live-client scripts.
(The old C# `Faolan.PacketAnalyzer` GUI/CLI was retired in #82 - it had no TCP reassembly and dropped ~2/3 of
the compressed GameServer downloads; the Wireshark plugin + `tools/capture/tshark_dump.sh` fully replace it.)

**Listeners** are partial classes split across many files — inspect all partials before changing
behavior. `GameServerListener`'s partials are flat `GameServerListener.*.cs` files named by role:
`.Inbound` (opcode handlers, `Handle*`), `.Outbound` (packet builders/senders, **behavior-named** e.g.
`SendVitals`/`SendAddItem` — never opcode-named), plus
`.WorldEntry`/`.PostReadyBurst`/`.World`/`.Combat`/`.Buffs`/`.Tradepost`/`.ItemUse`; GM commands are the
`.GmCommands` partial. Domain logic proper lives in composed service folders beside it (`Combat/`,
`CreateFlow/`, `Dialogue/`, `Economy/`, `Inventory/`, `Quest/`, `Scripting/`, `Session/`, `World/`). NB a partial-class file keeps
`namespace Faolan.GameServer` regardless of its folder (the `// CheckNamespace` suppression), so its folder
is organizational only.

**Prefer composed collaborators over growing the partial.** Domain logic is pulled OUT of the listener into
folder-namespaced service classes that take their deps by ctor and the client per call:
`CombatService` (Combat/), `WorldBroadcaster` + `WorldSimulation` + `PlayerPersistenceService` (World/),
`EquipmentService` (Inventory/), `ConversationService` (Dialogue/), and
`ShopService`/`BankService` (Economy/) — tradepost/AH + mail ops are folded onto the listener itself
(`GameServerListener.Tradepost.cs`) but still reach the wire through the same `IEconomyOutbound` port. The
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
`INetworkClient.Session`. Cross-listener actions go through dispatcher seams (`IChatServerDispatcher` for
chat/system messages, `IGameServerDispatcher` for world actions), never direct references.

**Database:** SQLite via EF Core (`Faolan.Core.Database`: `DatabaseContext` + `Tables/`), pooled
`DbContextFactory`, short-lived per-unit-of-work contexts — lazy loading is deliberately NOT used; load
navigations explicitly with `Include`. All access goes through `IDatabaseRepository`. `faolan.db` carries the game data (built by the importer, committed to
git) and the server only migrates + reads/writes at save/load points. The sqlite3 CLI has foreign keys
OFF by default — `PRAGMA foreign_keys=ON` before manual deletes or you orphan child rows.

**SQLite is INTERIM (#379).** The goal is a real DB with the schema+seed as a `.sql` file, dropping the binary.
Two things already run ahead of that move:
- **`faolan.sql` — the reviewable mirror of `faolan.db`'s DATA, and a seed that loads into BOTH SQLite and
  Postgres** (#411). Regenerated by the `.githooks/pre-commit` hook whenever `faolan.db` is committed (dumping the
  STAGED blob, so it matches the commit exactly), together with the `faolan.seq.{sqlite,postgres}.sql` sidecars.
  ⚠️ **`git config core.hooksPath .githooks` once per clone** — hooks are not shared by git, and without it
  `faolan.sql` silently goes stale. Never hand-edit it. To regenerate by hand:
  `dotnet run --project Faolan.Importer -- --report sql-seed`
  **Restore:** create the schema (`dotnet ef database update`), run `faolan.sql`, then the sidecar for your
  dialect — verified at 0 errors with full row parity into a fresh SQLite *and* a fresh Postgres.
  It is GENERATED from the EF model (`SqlSeedWriter`), not by `sqlite3 .dump`, because a dump is SQLite-only:
  identifiers are quoted (Postgres folds unquoted `Items` to `items`), every row carries an explicit column list
  (so column ORDER is irrelevant), literals come from each property's **CLR type** rather than the value the
  reader hands back (SQLite has no boolean storage class and returns 0/1, which Postgres rejects for a `boolean`),
  and tables are emitted in FK-dependency order so it loads with constraints enforced. No `CREATE TABLE` — the
  schema's one owner is the EF migrations — and no `__EFMigrationsHistory`, which is per-database bookkeeping.
  Characters are excluded (per-developer play state); the excluded set is derived from the model, not listed.
- **`journal_mode=WAL`** — a persistent FILE-HEADER flag (no code, nothing to configure). Without it a reader
  BLOCKS a writer, so any concurrent read (a `sqlite3` query, the MCP server, Faolan.Web, another realm process)
  makes a live world write FAIL and be lost. ⚠️ A busy_timeout does NOT help and is already 30s by default
  (`Microsoft.Data.Sqlite` maps `Default Timeout` → `sqlite3_busy_timeout`): a timeout only makes the writer WAIT,
  and the reader still holds the lock.

**Wire protocol essentials:**

- `PacketStream.WriteHeader(...)` + `SendAsync(...)` computes length + CRC; build packets with
  `PacketStream`/`FaolanStream` helpers. Integers are big-endian; protobuf fixed32 floats are
  little-endian while raw-body floats are big-endian (`WriteFloatLittleEndian` / `WriteFloat` BE are both
  intentional).
- 16-bit RPC opcodes are **method indices**: `wire = 0x2000 + registration index`, per interface, base
  0x2000 for every interface except PlayerAgent/PlayerInterface (32-bit hash dispatch — login, still open).
  Both directions are recovered from the client (`docs/re/aoc-rpc-method-opcodes.md`, scripts in
  `tools/ghidra/`): c2s order == `docs/re/aoc-client-rpc-methods.json`; the s2c "Dispatch" tables are the
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
- **Count-framed lists come from `ListFrame`, never a hand-rolled `0x3F1`.** A list is prefixed by a u32
  `(count + 1) * 0x3F1` (guilds, AH, quests, spell book, shortcut bar, stat lists, spawn scaffold, resurrection
  spots). ⚠️ A ONE-element list frames as `0x7E2`, and single-element lists are the common case in captures — so
  `0x7E2` reads as magic and has been written down as a constant repeatedly (resurrection spots #372, the AH search
  `head`, `SendMail` #378, GameActionList). **There is no such thing as a `0x3F1`/`0x7E2` constant**: of the 136
  functions in `AgeOfConan.exe` that touch the word, **77 divide by `0x3F1`** to read a count and **59 multiply** to
  write one — **zero** compare it to a constant (`tools/ghidra/DumpListFraming.java`). So when a "constant" equals
  `n * 0x3F1` it IS a list count; distrust any grammar pinned only by single-element captures. Enforced across
  `Codecs/` **and** `Faolan/` by `CodecConventionTests` — a wire rule must cover everywhere the wire is written.
- Files are one-type-per-file named after that type (a tightly cohesive group may share one file, e.g.
  `FullCharSnapshot/FullCharSnapshotSubClass.cs`), filed under their domain folder.

**Capture workflow for protocol work:** decode first, implement second. Each decoded subsystem gets a
Python dissector in `tools/capture/decode_*.py` that parses the relevant packets out of the capture JSONs and
validates the grammar to the packet end, then a C# builder proven by a byte-exact golden test in
`Faolan.Tests` against the captured bytes. Direction is "construct, don't replay": structured DB rows +
builders replace verbatim blob replay subsystem by subsystem; remaining verbatim blobs are deliberate
interims (marked as such) awaiting decode or in-client validation. Worked example: NPC spawns build
WHOLE from typed columns (the three-tier `Npc` type / `NpcSpawn` placement / live `WorldEntity` model —
`docs/subsystems/npc-three-tier-architecture.md`); the captured `NpcSpawn.Data` blob is now **retired** — `TryBuildLive`
is the sole spawn-body path.

**GM commands** (`Faolan/GameServer/GameServerListener.GmCommands.cs`, account type >= GameMaster, typed as `.cmd` in chat)
are the standard test bootstrap for features whose retail trigger isn't wired yet (`.additem`, `.teleport`, `.ah`,
`.setnpcgroup`, ...). The registered set is the `_gmCommandsDict` in that file (`InitGmCommands`) — check it before
assuming a command exists; several older ones were removed, and any MCP tools wrapping them are stale.

**MCP server:** the running server hosts an in-process MCP endpoint at `http://127.0.0.1:7099/mcp`
(DB queries + live-session inspection + GM-command routing).

## Capture data

The 349 MB of retail traces, the Ghidra project and the baked terrain live in a separate PRIVATE repo,
[ProjectFaolan-Data](https://git.groenendaal.io/MelvinGr/ProjectFaolan-Data), so this repo stays small
(and can go public). Clone it near this repo (it holds `Captures/`, `Ghidra/`, `Terrain/`) and pass its
`Captures/` dir to importer commands as `--captures <data-repo>/Captures` (and `--rdb` likewise).

**The test suite does NOT need it.** All 1046 tests read `Faolan.Tests/Assets/corpus-extract.bin` (12 MB,
committed): the eight IIR types the gates need, pulled out of the corpus and deflated, with each
packet's source file, index and tshark frame preserved so a byte-exact failure still names one packet in one
capture. Regenerate it with the data repo present:

```zsh
dotnet run --project Faolan.Importer -- --report corpus-extract --captures <data-repo>/Captures
```

`--captures` is how every importer command takes it (`--rdb` likewise). The importer's DB comes from faolan.json
(the `Database` section: `Database:Provider` + `Database:ConnectionString`), the same as the server; override the connection with `--connectionstring "<conn string>"`

⚠️ The extract is DERIVED. A regeneration against a partial corpus would quietly WEAKEN every whole-corpus
gate while they all still pass - the exact failure mode those gates exist to catch. Its manifest pins the
per-IIR counts and `CorpusExtract` throws on a short extract rather than letting coverage narrow. It also
records all 213 capture FILE NAMES so the doc linters (`DocReferenceTests`, `ProtocolCitationTests`) can
verify `[Vn:]` citations without the data.

You only need the real corpus to regenerate the extract, or to run `Faolan.Importer` / its `--report` tools.
`Terrain/` needs no symlink - it is the `TerrainDir` key in `faolan.json`; if it is missing the server logs
that ground-snap and line-of-sight are DISABLED and carries on, so check that line rather than assuming.

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
- **Never commit a machine-local absolute path** (`/home/<user>/…`, `/mnt/…`, a `/tmp/claude-…` scratchpad
  dir, a `C:\Users\…` path) into ANY tracked file — code, tests, docs, scripts, config. The repo is meant to
  go public and must stay machine-agnostic; a local path is wrong on every other clone and leaks the layout.
  Instead:
  - **Runtime code / config:** take the location from a CLI flag or env var, falling back to a clear error —
    the client RDB dir is `--rdb` → `AOC_RDB` (`ReportCli.RdbDir`); `Faolan.Web`'s `RdbDir` is blank in the
    committed `appsettings.json` and set in the gitignored `appsettings.Development.json` or the env.
  - **RE / tool scripts:** write outputs under a repo-relative dir (Ghidra → `Captures/ghidra/output/`) or the
    system temp dir (`tempfile.gettempdir()`, `Path.GetTempPath()`) — never a hardcoded scratchpad path.
  - **Docs / prose:** use a placeholder — `<data-repo>/Captures`, `<rdb-dir>`, `$PWD/faolan.db`. Concrete local
    paths belong in agent memory only (under `~/.claude`), not the repo. See the `no_local_paths_in_repo` memory.
  - `run.sh` is the one deliberate exception (personal dev launcher, kept local); its Windows twin `run.bat`
    takes `FAOLAN_CDN_STORE` / `AOC_EXE2` instead.
- **Docs carry CONFIRMED info only — progress/TODOs live in Gitea, not `docs/`.** No "Remaining / Next steps /
  Open items / IN PROGRESS / status-at-a-glance / handoff" sections and no `*-handoff.md` "how to continue"
  docs; keep the verified facts (decoded grammar, shipped behaviour, code maps) and replace any open-work list
  with a one-line pointer to the Gitea issue (`open work tracked in Gitea #NNN`). The backlog has one home.
- **`docs/` is organized by kind** (see [`docs/protocol/README.md`](docs/protocol/README.md) for the index):
  - `docs/protocol/` — the **verified wire reference**: every fact carries a `[Vn: …]` verification tag a test
    resolves, and `docs/protocol/gen/` is GENERATED from the code (opcodes, IIR types, protobuf, coverage,
    per-message index) — never hand-edit those; regenerate with
    `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.
  - `docs/subsystems/` — how OUR server implements a domain (combat, chat, guilds, inventory, economy, …).
  - `docs/rdb/` — client asset formats · `docs/tooling/` — launcher/capture/importer · `docs/re/` — Ghidra
    methodology + raw decode artifacts.
  Three tests keep this honest: a `[Vn: …]` claim must cite a real artifact (`ProtocolCitationTests`); a
  change to `Opcodes.cs` must regenerate the `gen/` pages in the same commit (`ProtocolDocsTests`); and across
  ALL of `docs/`, every backtick type-name (`FooService`, `BarTests`, …) and repo-relative file path must
  resolve to something that exists (`DocReferenceTests` — the prose-citation analogue of the `[Vn:]` linter;
  add a retired reference to its allowlist with a reason rather than leaving it dangling).
