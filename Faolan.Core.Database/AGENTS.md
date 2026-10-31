# Faolan.Core.Database: the model, the seed and migrations

**Database:** SQLite via EF Core (`Faolan.Core.Database`: `DatabaseContext` + `Tables/`), a pooled `DbContextFactory`,
short-lived per-unit-of-work contexts. Lazy loading is deliberately not used; load navigations explicitly with
`Include`. All access goes through `IDatabaseRepository`, which is the composite of 13 per-domain slices, each declared
beside its implementation (`Repositories/DatabaseRepository.<Domain>.cs`). The character's five slices are split by the
question a member answers: `ICharacterRepository` (the row and its 1:1 satellites), `ICharacterInventoryRepository`
(what it carries), `ICharacterQuestRepository` (what it is on), `ICharacterProgressionRepository` (what it has learned)
and `ICharacterStatRepository` (what it adds up to).

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

## EF migrations

```zsh
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
