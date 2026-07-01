# Handoff — 2026-07-01: appearance editor, appearance-in-DB, Faolan.Core split (#290)

Branch: `development`. All work committed **except `faolan.db`** (left dirty by the running game server;
its only intentional content change — the `AppearanceChoices` table — was already committed in `d12a9bd3`).

Servers were left **up**: game server on `:7010`, `Faolan.Web` on `:7080` (`dotnet run --project Faolan.Web`).

---

## What landed (commits, newest first)

| commit | what |
|---|---|
| `d7b6f5f7` | **#290** extract `Faolan.Core.Rdb` |
| `4fd70aeb` | **#290** drop the login-time scaffold-refresh hook (save-time covers it) |
| `006e8162` | **#290** propagate appearance edits to the spawn scaffold at save-time (web) |
| `9dc7ea6d` | **#290** extract `Faolan.Core.Spawn` (spawn/appearance domain slice) |
| `d12a9bd3` | import creator appearance choices to DB with names (`AppearanceChoice` + `rdb-appearance`) |
| `6d8e8788` | propagate appearance edits into the spawn scaffold on login *(superseded by save-time; hook later removed)* |
| `9f09deca` | Faolan.Web appearance **editor** (race/sex-gated, live preview + save) |
| `687d1cc6` | move pose sliders into a draggable floating window |
| `7077af77` | fix web-viewer horizontal stretch (camera aspect synced to canvas) |
| `351053a3` | randomise female starter clothing colour |

---

## 1. Appearance editor (Faolan.Web `/characters`)

A second draggable floating window edits a character's looks: **body morph sliders, hair style, hair/skin/eye
colour swatches, head, body/face tattoos**. Live preview + Save + Revert. `viewer.js` `show(canvasId, url,
keepCamera)` — the editor reloads with `keepCamera:true` so the orbit isn't reset each tweak; returns the
server error text on failure (strict preview, no fallback).

- Preview endpoint: `GET /api/character/custom/model.glb` (strict — 422 with a reason if an asset is missing).
- Options endpoint: `GET /api/appearance/options?sex=&race=` → reads the `AppearanceChoices` table.
- Save: direct server-side `CharacterModelService.SaveAppearance` (no open write endpoint) → writes
  `CharacterBody` + `CharacterBodyResources`, then `PropagateToScaffold` rebuilds the spawn scaffold's
  appearance entries so the edit shows **in-game on next relog**.

**Category → render role** (verified vs the TexScript mesh-target): cat3 hair-style(mesh), 6 hair-colour(tint,
MeshTarget −4), 7 eyes(−5), 8 skin-colour(−3), 9 body-tattoo(−2), 10 face-tattoo (**not in the render switch →
doesn't render yet, #275**). See memory `charactercreator_1010700_xml` / `face_tattoo_240_client_side`.

## 2. Options + names in the DB (importer)

`Faolan.Core/Database/Tables/AppearanceChoice.cs` + `Faolan.Importer/Rdb/RdbAppearanceImporter.cs`
(module `rdb-appearance`). Parses the **CHARACTERCREATOR (RDB 1010700, plaintext XML)** into per-sex/race option
rows (hair / colours / body+face tattoos / heads + height + per-sex morph min/max) and resolves each option's
**Name** from the referenced RDB object's own `name=` (texscript 1010035 → "The Bear"; monsterdata 1010201 →
"Thick 1"); colours store the XML hex swatch. Re-run: stop servers, `git checkout -- faolan.db`,
`dotnet ef database update --project Faolan --connection "Data Source=$PWD/faolan.db"`,
`dotnet run --project Faolan.Importer -- --only rdb-appearance --rdb <RDB>`, restart.

## 3. Faolan.Core split (#290) — pilot done, then a real wall

Two clean leaves extracted into their own projects (technique: **keep the `Faolan.Core.*` namespace**, move
files + add project references → zero `using` edits; works only for a clean output-leaf):

- **`Faolan.Core.Spawn`** — `CharacterMorphs`, `PlayerSpawnScaffoldRows` (+ `ScaffoldEntry`), the
  `PlayerSpawnScaffold` data model. Moved OUT of the `Faolan` host (`Faolan/GameServer/Spawn/`, namespace
  `Faolan.GameServer.Spawn` → `Faolan.Core.Spawn`). The wire codec `PlayerSpawnScaffoldCodec` (Parse/Serialize)
  **stayed in the host** (it needs the host-side `PlayerSpawnReader`, which pulls in `SpawnPacketBuilder`).
  This let the web call `RefreshAppearance` at save-time.
- **`Faolan.Core.Rdb`** — `RdbReader` + `Assets/`. Clean output-leaf (only depends on `Faolan.Core.Crypt`);
  consumers Web/Importer/Tests just added a project ref.

Verified: solution builds, **657/657 tests pass**, `model.glb` renders at runtime.

---

## Open / next steps

### #290 — the remaining core is a CYCLIC cluster (the real work)
`Network` depends on `Crypt`/`Data`/`Database`/`Extensions`, and each depends **back** on `Network` — via four
foundational types in the wrong layer:
- `INetworkClient` (used by `FaolanStream`, `DatabaseRepository`)
- `FaolanProtobufMessage` (base of `ProtoVector3`/`ProtoQuaternion`, `ProtobufExtensions`)
- `PacketStream` (used by `Crc32`, `PacketExtensions`)
- `ConanPacket`/`Packet`

None are dead usings. **Split is gated on relocating those base types down into a `Faolan.Core.Abstractions`
(or `.Primitives`) project** that `Network` references. Suggested order: (1) move `INetworkClient` +
`PacketStream`/`Packet`/`FaolanProtobufMessage` to the base project; (2) `Crypt`/`Data`/`Database` then fall out
cleanly; (3) `Network` last. This is deliberate, higher-churn (namespace decisions) — do it as its own focused
pass, one base type at a time, building+testing between each. `Enums` is the one remaining trivial clean leaf.
Full analysis is in **Gitea #290** comments.

### #291 — LDB names (hair/head polish)
Tattoo/head names come from the resource `name=`. **Hair falls back to an ordinal** and a few texscripts show
an internal name. The polished localized names live in the **LDB = RDB type 1030002** (1103 `TDC2` string-table
pages; grep misses them — zlib-compressed). The pages are FLAT ordered strings with **no per-id key**, so the
id→name index is an unsolved sub-decode. Once cracked it's a one-place swap of the importer's name source.

### Client-side (not server) — #240, #288
- **#240** face tattoo, **#288** floating hair (Testietwo) are client-render issues (#275 face-paint/head-morph
  mapping). Our wire matches retail; see memory `face_tattoo_240_client_side`. Both `prio:low`.

### Not verified in-client this session
The save-time scaffold propagation (`006e8162`/`4fd70aeb`) built + tests green, but the full create→edit→relog
loop wasn't re-run in-client after the refactor. Worth a quick confirm: create a char, edit + Save in the web,
relog in-game, check the look propagates.

---

## Gotchas hit this session
- `git checkout -- faolan.db` under a running game server makes its DB connection readonly — **stop servers
  first** (`pkill -9 -f bin/Debug/Faolan.dll`).
- Saving a scaffold graph loaded via `GetCharacterScaffold` cascade-inserts the parent `Character` (UNIQUE
  constraint) because the loaded skills/frame carry a `.Character` back-nav — persist **entry-table rows only**
  (fresh, nav-free). That's why `PropagateToScaffold` does its own `RemoveRange`/`AddRange`.
- `RefreshAppearance` must NOT reset progression — it rebuilds only weighted+morph+closer entries, keeping
  skills/frame/pairs. Only runs for chars with editor resources (captured chars have none).

Related memories: `charactercreator_1010700_xml`, `faolan_core_split_290`, `face_tattoo_240_client_side`,
`no_fallbacks_surface_errors`, `terminology_retail_means_live_service`.
