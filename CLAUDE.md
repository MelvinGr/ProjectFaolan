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
dotnet run --project Faolan.Importer -- --only rdb-maps --RdbDir <dir> # rdb-* + mounts are OPT-IN (not in the default run)
dotnet run --project Faolan.Importer -- --report burst-coverage       # read-only diagnostics (was --burst-coverage / --dump-*)
dotnet run --project Faolan.Importer -- --report sql-seed             # (re)write faolan.sql + the counter sidecars from the DB
dotnet run --project Faolan.Importer -- --report apply-seed --dry-run # upgrade an EXISTING db's content FROM faolan.sql (#412)
dotnet run --project Faolan.Importer -- --report timeline --report-arg <capture>.tshark.json --RdbDir <dir>
# ^ ONE capture as a flat chronological story, ids resolved + LDB text decrypted inline (`./run.sh --timeline <dump>`;
#   see the `capture-timeline` skill). Decode lives in the CODECS - add one there, never inline in the report.
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
# Faolan.Core.Database.Migrations.{Sqlite,Postgres}/Migrations).
# ⚠️ SQLITE ONLY - scaffold the Sqlite one and STOP. Melvin's standing call (2026-07-26): do not scaffold Postgres
# migrations. It is not a new divergence - Postgres was already 16 migrations behind (8 vs 24) when the rule was set,
# so it has not been migratable for a long time. The Postgres PROVIDER code stays (Faolan.Core.Database is
# provider-agnostic and faolan.sql loads into both), but its migration chain is dead: bringing Postgres back means
# deleting its Migrations/ and scaffolding ONE fresh initial migration from the current model, not replaying 16.
# The EF tools run design time with CWD = the PROJECT dir. That used to need FAOLAN_CONFIG + an absolute
# --connection override (#456): Global.ConfigPath now searches UP from the CWD (then up from the exe dir) for
# faolan.json, and a relative SQLite `Data Source` resolves against the DIRECTORY that file was found in - so
# both commands work from anywhere in the tree with no overrides. FAOLAN_CONFIG still wins if you set it.
dotnet ef migrations add <Name> \
  --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
# `database update` likewise (or just start the server - it migrates, loads faolan.sql into a database with no
# content, and seeds the default realm/account at startup):
dotnet ef database update \
  --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
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
networking + the byte-exact packet CODECS (`Codecs/` by domain: `Spawn/`, `World/`, …); the opcode/IIR catalogs
themselves live one layer down in `Faolan.Core/Enums/Opcodes.cs`
· `Faolan.Core.Rdb/` client RDB extraction · `Faolan.Importer/` tshark-dump+RDB -> DB CLI ·
`Faolan.ClientLauncher/` unattended retail client · `Faolan.Mcp/` in-process MCP server (DB + live-session +
GM-command tools) · `Faolan.Web/` + `Faolan.Web.Content/` Blazor armory/3D-viewer + GM tools + CDN proxy ·
`Faolan.Tests/` the test suite · `Faolan.Wireshark/` full Conan-protocol dissector plugin - framing + zlib +
RpcHeader endpoints + named opcodes/IIR types (generated from `Opcodes.cs`) + handoff auto-follow (ground truth, TCP-reassembled; use
`-o tcp.analyze_sequence_numbers:FALSE` on lossy captures) · `Captures/` reference traces - NOT in this repo (they live in the private ProjectFaolan-Data repo; the suite reads committed fixtures under `Faolan.Tests/Assets` instead, see **Capture data** below) ·
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

**Model conventions** (enforced by `Faolan.Tests/DatabaseConventionTests.cs`):
- **A foreign key is declared with `[ForeignKey]` on the navigation property — never a fluent
  `.HasForeignKey(...)`.** Reading `Tables/Foo.cs` must tell you what `Foo` points at; a fluent FK moves half of
  that into `OnModelCreating`. A **composite** key is no exception and is not a reason to reach for the fluent
  API — `[ForeignKey]` takes a comma-separated column list, kept rename-safe with `nameof`:
  `[ForeignKey($"{nameof(ConversationNodeId)},{nameof(PacketSequence)}")]`. What legitimately stays fluent is
  everything that is *not* the FK column mapping: `.OnDelete(...)` (no attribute exists),
  `.ValueGeneratedNever()`, and the `HasMany`/`WithOne` pairing that names both ends.
- **A table the importer delete+reinserts must have a natural or derived key, never an AUTOINCREMENT
  surrogate** — a surrogate is reassigned on every re-harvest, so a reimport churns thousands of `faolan.sql`
  lines for zero semantic change, and cascades into every child FK. Two shapes, in order of preference:
  1. **Natural composite** — `[PrimaryKey(...)]` on a plain class (not `BaseEntity`), e.g. `ShopItem`
     `(ShopId, Pos)`, `NpcSpawnEquipment` `(NpcSpawnId, Pos)`. For a tree, each child is keyed by its parent's
     key plus its own `Sequence` (`ConversationPacket` → `ConversationScreen` → `ConversationScreenParameter`).
  2. **Derived id** (`DerivedKey` / `IDerivedKey`) when the identity is too wide to carry down a tree, contains
     floats, or is pointed at by a *nullable* FK (a composite cannot express `SetNull` when one of its columns
     is non-null). The entity exposes `ComputeId()`; `DatabaseContext` stamps it on insert, and
     `DatabaseConventionTests` gates the shipped rows on `Id == ComputeId()`. Used by `ConversationNode`,
     `NpcSpawn`, `NpcSpawnArea`, `WorldObject`.
  Empirical check for a table that still needs this: `sqlite_sequence.seq / COUNT(*)` is how many times the
  table has been fully renumbered.

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
  ⚠️ **The sidecars are currently EMPTY, and that is the expected state.** They exist to restore AUTOINCREMENT /
  identity-sequence high-water marks, and no game-content table has a store-generated key any more — every one is
  keyed by its identity (see the model conventions above). They are still generated and still part of the restore
  because they repopulate automatically the moment a generated key reappears; an empty sidecar is the signal that
  the seed is fully identity-keyed, not that something failed.
  It is GENERATED from the EF model (`SqlSeedWriter`), not by `sqlite3 .dump`, because a dump is SQLite-only:
  identifiers are quoted (Postgres folds unquoted `Items` to `items`), every row carries an explicit column list
  (so column ORDER is irrelevant), literals come from each property's **CLR type** rather than the value the
  reader hands back (SQLite has no boolean storage class and returns 0/1, which Postgres rejects for a `boolean`),
  and tables are emitted in FK-dependency order so it loads with constraints enforced. No `CREATE TABLE` — the
  schema's one owner is the EF migrations — and no `__EFMigrationsHistory`, which is per-database bookkeeping.
  Characters are excluded (per-developer play state); the excluded set is derived from the model, not listed.
  Rows are batched into multi-row `INSERT … VALUES (…),(…)` so the column list is paid once per 1000 rows rather
  than per row (24 MB → 7.4 MB), while each row keeps its own LINE so the diff stays reviewable.
  **A database with NO game content gets this loaded automatically** (`DatabaseInitializer.EnsureGameDataAsync`,
  #412) — delete `faolan.db` and the server rebuilds it from the migrations + `faolan.sql`, on SQLite *or*
  Postgres. Probed on `Items` being empty, so a populated database is never touched and nothing is re-seeded on a
  second boot; if the seed is missing when it IS needed, startup fails with a clear error instead of coming up
  with an empty world. `FAOLAN_SEED` overrides the path (`Global.SeedPath`), like `FAOLAN_CONFIG`. The importer
  opts OUT (`InitializeAsync(db, seedGameData: false)`) — its job is to build that content from captures.
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
- **Offset-addressed reads/writes over a buffer go through `SpanExtensions`** (`Faolan.Core/Extensions`):
  `body.ReadU32Be(4)` / `template.WriteF32Le(PosXOffset, x)`, NOT `BinaryPrimitives.<Op>(body.AsSpan(4, 4))`
  and never a local `U32`/`ReadLE`/`WriteLe` helper (a dozen files had re-declared their own). `byte[]`,
  `Span<byte>` and `ReadOnlySpan<byte>` all bind directly — no `.AsSpan()` at the call site. The set covers
  both endiannesses of every width, so there is never a reason to reach past it. What legitimately stays on
  raw `BinaryPrimitives` is the case where the span IS the value, not a field at an offset: a fixed-width
  scratch buffer flushed to a stream (`FaolanStream`, `GlbWriter`, `FctxEncoder`) or a 4-byte length prefix
  accumulated across recv boundaries (`ConanMessageFramer._lengthBuffer`).
- 16-bit RPC opcodes are **method indices**: `wire = 0x2000 + registration index`, per interface, base
  0x2000 for every interface except PlayerAgent/PlayerInterface (32-bit hash dispatch — login, still open).
  Both directions are recovered from the client (`docs/re/aoc-rpc-method-opcodes.md`, scripts in
  `tools/ghidra/`): c2s order == `docs/re/aoc-client-rpc-methods.json`; the s2c "Dispatch" tables are the
  per-class dispatcher switches (the old "drift" was just extra no-arg dispatch methods the string catalog
  missed). Full per-class catalogs are in `Faolan.Core/Enums/Opcodes.cs`. 32-bit sub-opcodes
  inside 0x2000 are BKDR-31 hashes of the client's IIR type names (`docs/re/aoc-opcode-ids.md`).
- Session-volatile values (entity handles, shop ids, conversation ids) must never be persistence keys;
  the stable key is the NPC/vendor NAME (conversations, shops) or the character id.

**Codec conventions** (`Faolan.Core.Network/Codecs/`, enforced by `Faolan.Tests/CodecConventionTests.cs`):

⚠️ **These rules are BINDING — the sweep is done (Gitea #530, waves 0–9) and all 16 gates in
`Faolan.Tests/CodecSurfaceConventionTests.cs` are live over every folder under `Codecs/`, with zero skips.** Every
codec is on the uniform surface, `Faolan/`'s own wire builders moved in (wave 7), `Faolan.Importer/` no longer
names an IIR or hand-decodes a body a codec owns (wave 8), and `ICodec<T>` is deleted (wave 9) — do not
reintroduce it. Outside `Codecs/`, a `GameServer0X2000Opcodes` member may only appear as a `case` label of the
listener's routing switch. **Every allowlist is now EMPTY** — `UncomposedSubRecords` came off with #543 and
`UnreadableInTheCorpus` with #545, so every codec with a standalone body Parses and re-Builds every captured body
byte-exact, with nothing excused. An allowlist is not a place to silence a gate: re-adding an entry means naming
the DECODE issue that removes it again. One follow-up remains: #533 (row↔value mapping).

- **One codec shape: two public method NAMES, nothing else.** A codec is a `static class` exposing exactly
  `byte[] Build(...)` and `T Parse(in ReadOnlySpan<byte> data)`, plus its `Opcode` / `HandledTypes`
  declaration. No `Encode`/`Decode`/`TryParse`/`BuildBody`/`ToArray`/public `Read`/`Write`.
  - **`Build(T value)` is canonical and is the ONLY method that writes bytes.** `Build` may be overloaded in
    any shape (loose scalars, a DB row — whatever the caller holds), but every other overload *delegates* to
    `Build(T)`. `Parse` is never overloaded. One encoding path per codec is what makes the round-trip gate
    binding on all of them.
  - **`Parse` is strict — it throws `CodecParseException`** on a short/overlong body, a wrong type word, or a
    field it cannot account for. Never a null, never a `false`, never a default. Callers scanning a mixed
    stream dispatch on the type word at offset 0 first.
  - **The value `T` is a plain record** named for the client IIR *verbatim* (`MagicBuffCodec.Parse` →
    `MagicBuffIIR`). It does NOT serialize itself — no `ToArray()`, no `ICodec<T>`.
  - **Composition is `internal`:** `internal static T Read(ref SpanReader)` /
    `internal static void Write(FaolanStream, T)`. `InternalsVisibleTo Faolan.Tests` is global, so these stay
    testable.
- **String codecs: the same rules, `string` in place of `byte[]`.** A handful of codecs carry TEXT rather than
  bytes — the `remoteformat` XML layer that sits *inside* a byte codec, plus the row↔XML mappers around it
  (`InnerElementCodec`, `RemoteFormatScreenCodec`, `NpcChatterCodec`, `ConversationScreenCodec`,
  `AttributeChangedCodec`, …). They expose `string Build(T)` / `T Parse(string)`; everything above applies
  unchanged — two public method names, `Build(T)` canonical and the only thing that composes text, `Parse`
  strict and total (a throw, never a null), the value a plain record, one message one codec. The `byte[]` form
  stays as a **delegating** overload wherever the text IS the whole wire payload
  (`=> Encoding.Latin1.GetBytes(Build(value))` — Latin1 because that is the wire's string encoding). A codec is
  one shape or the other, never a mix of both `Parse` forms.
- **Read sequentially through `SpanReader` (`Faolan.Core/Extensions`); no const offsets; `Parse` ends with
  `EnsureAtEnd()`.** A `private const int FooOffset = 17` means the bytes before 17 were never decoded, so the
  codec cannot tell a correct body from one that merely has the right word at 17. `SpanReader` also owns the
  bounds checks — a codec never writes `if (body.Length < 26) return false;`. The rule is about READING, so the
  gate scopes to files that declare a `Parse`: a build/patch-only neighbour (`SpawnTransformPatch` stamping the
  live position/token into an already-built record, `SpawnPacketBuilder` splicing a captured template) legitimately
  addresses a fixed-width head by offset, and those offsets go away with the thing they patch.
- **`Build` returns the body WITHOUT the length prefix.** The u32 length belongs to the framing sender.
- **Dispose the stream:** `using var s = new FaolanStream(); … return s.ToArray();` — never
  `return new FaolanStream().Write(…).ToArray();` (`FaolanStream : MemoryStream` is `IDisposable`).
- **Every codec with a standalone body declares its opcode**, in one uniform place: `public const uint Opcode =
  (uint)GameServer0X2000Opcodes.<Type>;` (single type) or `public static readonly GameServer0X2000Opcodes[]
  HandledTypes` (a genuine family). An IIR opcode must **never** appear as a bare hex literal in `Codecs/` —
  the test fails on it. This is what makes `grep GameServer0X2000Opcodes.<Type>` find every emitter, and it
  is what `docs/protocol/gen/coverage.md` reads to attribute each type to its builder.
  **One IIR, one codec** — two codecs declaring the same opcode are the same codec and must be merged.
  **RPC-message codecs** (whole method bodies: `GuildSearchResultCodec`, `MailMessageCodec`, …) do the same against
  their own interface enum (`(ushort)CSPlayerInterfaceOpcodes.<Method>`) and are named for the client's RPC
  method. When several METHODS genuinely share one body — `DispatchTeamJoin`/`DispatchTeamLeave`/
  `DispatchSetNewTeamLeader` are the same layout with the verb in the opcode — declare
  `public static readonly <Interface>Opcodes[] HandledOpcodes`, the RPC twin of `HandledTypes`; picking one of the
  three as *the* `Opcode` would be a lie about the other two. **Sub-record codecs** (`CharacterStatBlock`,
  `StatList`, `MoveTaskNode`, …) are composed inside a larger body, never sent alone, and so declare nothing —
  that's expected, not an omission.
- **Wire encode/decode lives in `Codecs/`, nowhere else.** A service, listener or importer that needs a message
  calls its codec; it does not hand-roll the bytes. Concretely: an IIR opcode may only be *named* inside
  `Codecs/` (the listener's dispatch switch aside). The **frame** layer is the exception and stays put —
  `Protocol/Rpc.cs`, `ChatServerMessage`, the framers — because it owns the length prefix and CRC, which a
  codec must not touch.
- **Count-framed lists come from `ListFrame`, never a hand-rolled `0x3F1`.** A list is prefixed by a u32
  `(count + 1) * 0x3F1` (guilds, AH, quests, spell book, shortcut bar, stat lists, spawn scaffold, resurrection
  spots). ⚠️ A ONE-element list frames as `0x7E2`, and single-element lists are the common case in captures — so
  `0x7E2` reads as magic and has been written down as a constant repeatedly (resurrection spots #372, the AH search
  `head`, `SendMail` #378, GameActionList). ⚠️ **`0x3F1` itself is the same trap wearing the empty-list face** —
  `ForCount(0)` — and it is the more dangerous one, because a list that is empty in *most* captures reads as a
  closing/terminator constant. The player spawn's post-IIR carried FIVE of these at once (#251): the ID-list close,
  a 13-byte-record list after it, the "final marker", and the header pair; all five looked constant across ~200
  captured spawns and broke on the ~30 where the list was non-empty. **There is no such thing as a `0x3F1`/`0x7E2` constant**: of the 136
  functions in `AgeOfConan.exe` that touch the word, **77 divide by `0x3F1`** to read a count and **59 multiply** to
  write one — **zero** compare it to a constant (`tools/ghidra/DumpListFraming.java`). So when a "constant" equals
  `n * 0x3F1` it IS a list count; distrust any grammar pinned only by single-element captures. Enforced across
  `Codecs/` **and** `Faolan/` by `CodecConventionTests` — a wire rule must cover everywhere the wire is written.
- **A `Build` overload is not the only way to offer a convenience form.** Where several semantic forms of one
  message share a parameter list (`MoveTaskCodec`'s path and patrol are both "a waypoint list plus a float"),
  the forms become VALUE factories beside the codec (`MoveTaskNodes.Path` / `.Patrol`), not overloads on it —
  choosing between them by signature is exactly the ambiguity rule 1 removes. The same applies to everything
  next to a codec that is not encode/decode: row↔column mappers (`WorldObjectColumns`,
  `ConversationScreenColumns`), live-value policy (`RemoteFormatSession`, `GameClock`) and route/geometry math
  (`PatrolRoute`) are named for what they are and stay off the codec's surface. Row↔value mapping proper is
  Gitea #533.
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
`Captures/` dir to importer commands as `--captures <data-repo>/Captures` (and `--RdbDir` likewise).

**The test suite does NOT need it.** Every test runs from a plain clone, off committed fixtures in
`Faolan.Tests/Assets`. There are two kinds, and the difference decides which one a new gate belongs in:

- **Named fixtures** (`recipe-book-reply.bin`, `mount-spawns.bin`, `casilda-quest-*.bin`,
  `snapshot-login-*.bin`, …) - a specific packet or one capture's worth, read via `CapturedFixture` (whole
  Conan packets concatenated; a packet self-frames, so no container format) or `TestPaths.AssetBytes`. This is
  what a gate that only ever wanted ONE packet should use.
- **`corpus-extract.bin`** (14 MB) - the whole-corpus slice, read via `CorpusExtract`. Reserved for gates whose
  assertion IS breadth: the snapshot round-trips (#384/#390/#391), the protobuf re-encode oracle (#385), the
  world-object column lift. These would go vacuous on a fixture, which is exactly why they read the corpus.
  Regenerate it with the data repo present:

```zsh
dotnet run --project Faolan.Importer -- --report corpus-extract --captures <data-repo>/Captures
```

`--captures` is how every importer command takes it (`--RdbDir` likewise). The importer's DB comes from faolan.json
(the `Database` section: `Database:Provider` + `Database:ConnectionString`), the same as the server; override the connection with `--Database:ConnectionString "<conn string>"`

⚠️ The extract is DERIVED. A regeneration against a partial corpus would quietly WEAKEN every whole-corpus
gate while they all still pass - the exact failure mode those gates exist to catch. Its manifest pins the
per-IIR counts and `CorpusExtract` throws on a short extract rather than letting coverage narrow.

The extract carries **no capture file-name index**. It used to, so `ProtocolCitationTests` and
`DocReferenceTests` could resolve `[V2:]` citations and `Captures/…` paths without the data - which meant every
checkout shipped a listing of the private corpus. `V2Citations_AreWellFormed` now checks the citation's FORM
instead, and `Captures/…` paths in docs are not linted at all. Don't reintroduce the list.

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
    the client RDB dir is the `RdbDir` key (`--RdbDir` / `RdbDir` env / faolan.json), blank in `Faolan.Web`'s
    committed `appsettings.json` and set in the gitignored `appsettings.Development.json` or the env.
  - **RE / tool scripts:** write outputs to an env-var dir falling back to the system temp dir, never a
    hardcoded scratchpad path and never a repo dir. Every `tools/ghidra/*.java` script resolves
    `static final String OUT = getenv("FAOLAN_GHIDRA_OUT", java.io.tmpdir) + "/"` and writes `OUT + "<name>.txt"`
    — copy that line into a new script. (The old `Captures/ghidra/output/` is gone: `Captures/` belongs to the
    private data repo, so it does not exist in a fresh clone.) Python/C# equivalents:
    `tempfile.gettempdir()`, `Path.GetTempPath()`.
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
