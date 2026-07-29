# Open-world NPC spawn layout grammar (RDB type 1010506) — #332

The per-playfield **authoritative, capture-independent** open-world NPC spawn source. Where our normal NPC
placements are lifted from network captures (`NpcSpawnExtractor` → the burst-truth `NpcSpawn` set), this RDB
type ships the spawn-point layout for every open-world playfield the client renders. Decoded decode-first (probe
in `Faolan.Core.Rdb/Assets/NpcSpawnLayout.cs`) and **validated byte-exact**: all **238 local** `1010506`
records parse to EOF (0 failures), **119 carry spawn groups** (2,664 groups / 23,782 primary spawn points) and
**119 are empty** (`groupCount == 0` — scripted/instanced playfields like the castle and the Tortage interiors,
whose NPCs are server-scripted and never appear here). The RDB record id **== the playfield id** (== `Map.Id`).

Ported to C# as `NpcSpawnLayout` and imported by `RdbNpcSpawnImporter` (the opt-in `rdb-npc-spawns` module) into
the `NpcSpawnArea` table — one row per group carrying the roam AABB + population count + (once resolved) the
creature `NpcTemplateId`, keyed by the stable `(MapId, GroupOrdinal)` pair, with the individual anchors as child
`NpcSpawnAreaPoint` rows. The creature is inferred by `SpawnAreaResolver` (name / gated containment / guarded
id-propagation, #363) since 1010506 carries no template — see the resolution note below.

## What the CLIENT does with it

The client's name for this resource is **`GameData::PlayfieldTerritory_c`**
(`CoreGameFramework/Playfield/Data/Territory.h`). It registers an RDB constructor for the type through
`RDBObject::RegisterConstructor` (`RDBObject.cpp`), and the reader accepts both this binary form and a legacy XML
one — *"Non binary territory format for %u:%u, please reexport playfield"*. [V1: `AgeOfConan.exe`, the 1010506
immediate inside `PlayfieldTerritory_c::ReadData`]

Its job is **NPC roam paths**. `PathMoveTask::InitRoamPath` resolves
`territories[territoryIndex].paths[pathIndex]` and walks it, bounds-checking both — the failure string prints
`size=<territory count>, index=<territoryIndex>`, and a path index of `-1` means "no roam path". So a MoveTask
can send a wander route **by reference** instead of by value: `PathFlags & 1` swaps the explicit waypoint list
for those two u16, and the client supplies the geometry from its own copy of this resource. Everything else
about the plan — anchor, speed, duration, remaining — is sent either way, so the two encodings are
interchangeable and every client reproduces the same route. The link to a particular NPC is the entity handle in
the MoveTask body; this resource carries no identity at all. See #490 and
[`docs/subsystems/npc-three-tier-architecture.md`](../subsystems/npc-three-tier-architecture.md).

Those two indices are `PatrolNode.TerritoryIndex` / `TerritoryPathIndex`, gated by
`Faolan.Tests/TerritoryRoamPathTests.cs`: every captured value joins an existing `(MapId, GroupOrdinal)` and
falls inside that territory's `RoamPathCount` — 56/56 captured nodes, zero misses.
[V1: Faolan.Tests/TerritoryRoamPathTests.cs]

The client also ships a **playfield debug GUI** that draws territories (`Views/PlayfieldDebugGUI/territories.xml`,
`PDTerritoriesView_c`, `TerritoriesDrawButton`), and a Lua predicate `isinterritory`.

⚠️ Do not conflate this with the OTHER "territory" in the client: `Territory Manager`, `TerritoryAddr`,
`X-CONAN-TERRITORYID` and `Authenticate(..., territoryVersion)` are login/realm infrastructure, unrelated to
spawn groups.

## Container

Decompressed via the normal `IOz1`+zlib unwrap (`RdbReader.ReadDecompressed`). Unlike most RDB types there is
**no 12-byte RDB-Id header** — the payload begins at the `version` word. Everything is little-endian; strings are
`[u16 len][latin1 bytes]`, floats are 32-bit LE.

```
[4]   version         u32 = 1
[4]   groupCount      u32
      groupCount × group
```

## group

```
[str] name            e.g. "Brawlers", "RoamingCitizens1", "Territory0", "Pict Camp"
[str] category        one of: Community | Race | Superfamily | Name | "no category"
[str] id              meaning depends on `category` — see below
[4]   pointCount      u32
      pointCount × vec3     the PRIMARY spawn points (a placement sits on one of these)
[12]  boundsMin       vec3   the roam AREA min corner (see below)
[12]  boundsMax       vec3   the roam AREA max corner (max.Y is padded to min.Y + 10)
[4]   pathCount       u32
      pathCount × ( [4] n u32, n × vec3 )   wander-SAMPLES that fill the roam area (NOT distinct routes)
```

`vec3` = `[f32 x][f32 y][f32 z]`.

## Roam area, not patrol routes

The `boundsMin`/`boundsMax` pair is the group's **roam AREA** (an AABB), and the `pathCount` sub-lists are
**space-filling wander samples of that same area, not distinct patrol routes** (measured on 4010, 2026-07-14):

- Every sub-list has `pathLength / bboxSpan ≈ 5` — the polyline folds back on itself ~5× within its extent
  (crocs 5.2, apes 5.6, poachers 5.3, **Picts 5.9** — uniform across creature type), i.e. a dense random walk,
  never a straight run.
- All of a group's sub-lists share **one** bounding box, and that box **equals the decoded `boundsMin`/`boundsMax`
  AABB** to within 1–4 world units. So the AABB is the roam area and the wander stays inside it.
- Groups differ only in the *size* of that area, not its structure: a Pict camp roams an ~80u box (spread out
  enough to read as a route network), while crocs/poachers/slavers pack the identical wander into an ~20u box
  (reads as a solid blob). An ambient spawner therefore wants the AABB (the roam bounds) — the individual
  sub-lists are just samples of it.

## What `id` means

The `category` selects how to read the `id` string:

| category | id string | count | notes |
|---|---|---|---|
| `Community` | numeric faction id | 8,912 pts | e.g. Brawlers → `295`, RoamingCitizens → `257` |
| `Race` | numeric race id | 7,133 pts | |
| `Superfamily` | numeric superfamily id | 3,677 pts | |
| `no category` | `"no category value"` | 2,735 pts | ungrouped points |
| `Name` | **creature display-name + 4-char tag** | 1,325 pts | e.g. `Shark (H8AV)`, `Chicken (424X)` |

So the group `id` is a **spawn-group / community key, NOT the creature template** — except for `Name`-category
groups, where it carries the creature identity directly. For every other category the creature type still has to
be correlated from captures. `NpcSpawnLayout.SpawnGroup` keeps the raw `Id` string plus a parsed `CommunityId`
(the numeric value, or null for `Name` / `no category`).

## Limits (why this doesn't fix every missing NPC)

- **Empty for scripted/instanced playfields.** The Castle of King Conan (map 2040) and the Tortage interiors are
  `version=1, groupCount=0` — their NPCs are server-scripted, not in the RDB, so they remain capture-only.
- **1010506 carries no creature template** (except `Name` groups): the creature `TemplateId` is
  server-authoritative (sent on the wire at spawn), and is **not** in the client RDB — confirmed via the empty
  spawn-export types (`1000077/78/79`), MonsterData being a many-to-one render model, and the 4-char code being
  absent from every other resource. So the creature can only be inferred from captures (`SpawnAreaResolver`:
  name / gated containment / guarded id-propagation), which resolves **186 of the 2,664 areas**; the rest await
  captures or a manual pass. See #363.

  Id-propagation is deliberately narrow: a numeric id names a *family*, not a species, so a creature learned for
  a `(Category, CommunityId)` key is propagated only to areas on a map where that key was itself resolved by
  name or containment (#489). Without that gate it reached 329 areas on just 6 creatures — Poitain's 29 groups
  all read `Panther` off a key whose only captured panthers are in Tortage, while Poitain's own captures say
  Wild Pig and Wild Cow. With it the tier covers 10 areas. `SpawnAreaResolver` also resets its own machine
  assignments before re-deriving, so it is idempotent and a rules change actually reaches existing rows; a
  `Manual` assignment (`.setnpcgroup`) is never touched. Re-run it over the DB alone with
  `dotnet run --project Faolan.Importer -- --report resolve-spawn-areas`.
- **`NpcSpawnArea` is kept separate from the capture-derived `NpcSpawn` set** — the former is the RDB-authoritative
  ambient population (roaming groups), the latter is exact hand-placed / named / scripted entities. The long-term
  plan is for a captured NPC confirmed to belong to an area to graduate out of `NpcSpawn` into its area.

Worked example — Tarantia Noble District (map 2010): `Brawlers`/Community `295`/3 points, `RoamingCitizens1`/
Community `257`/41 points (with 5 roam paths of 51/46/39/51/39 points), `RoamingCitizens2`/Community `257`/20
points.
