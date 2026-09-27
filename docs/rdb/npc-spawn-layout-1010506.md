# Open-world NPC spawn layout grammar (RDB type 1010506)

The per-playfield **authoritative, capture-independent** open-world NPC spawn source. Where our normal NPC
placements are lifted from network captures (`NpcSpawnExtractor` → the burst-truth `NpcSpawn` set), this RDB
type ships the spawn-point layout for every open-world playfield the client renders. Decoded decode-first (probe
in `Faolan.Core.Rdb/Assets/NpcSpawnLayout.cs`) and **validated byte-exact**: every `1010506` record parses to
EOF (0 failures). Over the 425 records whose playfield we hold, **188 carry spawn groups** (3,970 groups /
34,302 primary spawn points) and **237 are empty** (`groupCount == 0` — scripted/instanced playfields like the
castle and the Tortage interiors, whose NPCs are server-scripted and never appear here). The RDB record id
**== the playfield id** (== `Map.Id`).

**Most of this type is CDN-only** — 577 of the 817 `1010506` records have `file == 0xFF`, so they are not in
the local `NN.rdbdata` at all. `RdbReader` pulls those through the mirror proxy transparently, but only when it
is CONSTRUCTED with one; a bare reader gated on `Entry.IsLocal` silently makes the local 240 the whole world (120
playfields, 2,669 groups). Read a CDN-backed type with `rdb.CanRead(e)` and a reader that has the proxy base,
never with `IsLocal`.

Ported to C# as `NpcSpawnLayout` and imported by `RdbNpcSpawnImporter` (the opt-in `rdb-npc-spawns` module) into
the `NpcSpawnArea` table — one row per group carrying the roam AABB + population count + (once resolved) the
creature `NpcTemplateId`, keyed by the stable `(MapId, GroupOrdinal)` pair, with the individual anchors as child
`NpcSpawnAreaPoint` rows. The creature is inferred by `SpawnAreaResolver` (name / gated containment / guarded
id-propagation / declared race) since 1010506 carries no template — see the resolution note below.

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
the MoveTask body; this resource carries no identity at all. See
[`docs/subsystems/npc-three-tier-architecture.md`](../subsystems/npc-three-tier-architecture.md).

Those two indices are `PatrolNode.TerritoryIndex` / `TerritoryPathIndex`, gated by
`Faolan.Tests/TerritoryRoamPathTests.cs`: every captured value joins an existing `(MapId, GroupOrdinal)` and
falls inside that territory's `RoamPathCount` — 56/56 captured nodes, zero misses.
[V1: Faolan.Tests/TerritoryRoamPathTests.cs]

The client also ships a **playfield debug GUI** that draws territories (`Views/PlayfieldDebugGUI/territories.xml`,
`PDTerritoriesView_c`, `TerritoriesDrawButton`), and a Lua predicate `isinterritory`.

Do not conflate this with the OTHER "territory" in the client: `Territory Manager`, `TerritoryAddr`,
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
**space-filling wander samples of that same area, not distinct patrol routes** (measured on 4010):

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

## The record ID SPACE splits

`--only rdb-npc-spawns` imports a layout only when its `id` is a playfield already in `Maps` (the areas carry an FK
to it), and it counts every skip:

```
188 playfields imported, 237 empty, 0 unreadable,
  8 for a playfield not in Maps,
384 in the >= 0x40000000 id space
```

* **384** of the skipped entries carry an id with **bit 30 set** (`>= 0x4000_0000`). That is not a playfield id
  anywhere else in the client - it is a second id space inside this same RDB type, so those records are not
  playfields we are missing.
* **8** are ordinary playfield ids we do not hold: **741, 966, 6101, 6105, 6303, 6315, 6405, 50873** - and their
  neighbours are in `Maps` (6100 and 6103 are, 6101 and 6105 are not), so these are gaps in the map import rather
  than in this type.

So the coverage gap is eight playfields wide, and the 384 are a decode question about what the high id space means
rather than an import gap. Open work tracked in Gitea #886.

## How a CAPTURED spawn relates to an area

A capture-derived `NpcSpawn` is LINKED to an area — `NpcSpawn.NpcSpawnAreaId`, a nullable FK — when it is on the
same map, resolves to the same template, and stands inside the area's padded bounds. 200 spawns across 12 maps
link that way today. A linked spawn is then a member of that area's POOL: it renders with its captured
appearance, the pool owns its respawn, and it counts toward `MaxCount` so the pool tops up only the deficit. That
is what stops a captured NPC and an ambient one of the same creature standing in the same spot.

**Two things it deliberately does NOT do**:

* **It does not SNAP.** Nothing rewrites a captured position onto the RDB point. The captured coordinate is
  burst truth; the RDB point is where retail's ambient spawner would have put *an* instance of that group. Moving
  the one onto the other trades evidence for tidiness, and the pool behaviour above needs no such move.
* **It does not copy a `(playfield, group, index)` id onto the spawn.** It does not need to: `(MapId,
  GroupOrdinal)` is `NpcSpawnArea`'s own unique key, so the FK already *is* the stable id, reachable by one join
  and unable to drift from it. A per-POINT id has no reader either — `AmbientPoolService.FreePoint` scores points
  against where live members actually stand, and a linked captured spawn is one of those live members, so its
  occupancy is already counted by position.

The link is a downstream STAMP: `--only npcs` delete+reinserts `NpcSpawns` and the FK comes back NULL with the
row COUNTS unchanged, so a `--dry-run` delta shows nothing. After a spawn-harvest change run the full import, or
`--only rdb-npc-spawns --mode overwrite`.

## Limits (why this doesn't fix every missing NPC)

- **Empty for scripted/instanced playfields.** The Castle of King Conan (map 2040) and the Tortage interiors are
  `version=1, groupCount=0` — their NPCs are server-scripted, not in the RDB, so they remain capture-only.
- **1010506 carries no creature template** (except `Name` groups): the creature `TemplateId` is
  server-authoritative (sent on the wire at spawn), and is **not** in the client RDB — confirmed via the empty
  spawn-export types (`1000077/78/79`), MonsterData being a many-to-one render model, and the 4-char code being
  absent from every other resource. So the creature is inferred (`SpawnAreaResolver`: name / gated containment /
  guarded id-propagation / the group's declared race); the rest await captures or a manual pass. The shipped
  `NpcSpawnArea.ResolvedBy` split over the 3,970 areas:

  | `SpawnResolutionSource` | areas |
  |---|---:|
  | `Name` | 431 |
  | `Containment` | 37 |
  | `IdPropagation` | 55 |
  | `RaceUnique` | 353 |
  | `Manual` | 1 |
  | `None` (unresolved) | 3,093 |

  **For the `Name` groups the TEMPLATE ID is in the client too** — one lookup away, not capture-bound. `LDB[51000]` (`npc_template.name`) is **keyed by the NPC template id**, so the creature name a
  `Name` group carries resolves straight back to its id. Measured over the 23 real creatures behind the 153
  unresolved `Name`-category areas (six `XX …` dev entries excluded), **18 resolve**:

  | creature | template id | | creature | template id |
  |---|---:|---|---|---:|
  | Horned Viper | 8072 | | Jal Kor Bloodbane | 15116 |
  | Idar | 8078 | | Warlord Repadis | 15100 |
  | Shark | 10027 | | Attilius | 15585 |
  | Tia Shar | 15113 | | I-po, Master of Bats | 16129 |
  | The Red One | 15115 | | Jacinth Kappa | 16492 |
  | Bibaculus | 15321 | | Hyrkanian Ghost | 16888 |
  | Water Buffalo | 17387 | | Shar Kihn | 19189 |
  | Xiao-mei | 18182 | | Tian-bai | 19936 |
  | Refuge Corpse-Waker | 19961 | | Aratus | 20149 |

  The five that do not are `Bamboo Forest Komodo`, `Coast Shark`, `Groll`, `The Artificer` and `Vayaspara` —
  names the shipped English pages do not carry at all.

  **What that buys is IDENTITY, not a spawn.** None of the 18 ids is in `NpcTemplates`, so a seed would add
  identity-only rows — and `AmbientPoolService.LoadAsync` already skips any resolved area
  whose template has no buildable `StatCore.IirHeader`, so nothing would try to spawn a creature it cannot build.
  The stats and the render model still need a capture; the LINK does not.

  **The `Race`-category groups are the exception to "inferred from captures".** A group's `id` under that category
  IS the RDB 1000508 creature-race id — the client STATING what lives there — so where we hold exactly one
  spawnable template of that race there is nothing left to infer. That tier (`RaceUnique`) resolves 353 areas /
  2,382 ambient creatures across 9 creatures, and the identity behind it is independently checked: of the areas
  resolved by name/containment/propagation, 369 of 415 (measured) carry a template whose `NpcStatCore.Race` equals
  the race their group declares — two sources that share nothing agreeing at 89%.

  **Unambiguous or nothing.** A race with several candidate templates says which FAMILY lives here, not which
  creature, so those areas stay unresolved rather than taking an arbitrary one; only a race with exactly one
  candidate template is taken. The tier also runs LAST (it never displaces a name/containment answer) and is blocked when
  the group's own name names a *different* race — `CatTerritory` declares Rat and is left alone. The guard is on the
  RACE name, not the creature's: `GrasslandsRabbit42` shares no word with `Old Buck`, which is exactly why the
  creature-name guard tier 3 uses would have rejected 200 correct areas.

  The containment tier votes on captured spawns standing **on the group's wander path**, not merely inside its
  roam AABB. The box also holds the hand-placed NPCs standing in it — a village overlapping a territory,
  or a named boss beside its trash — and their votes broke unanimity or won it outright. Members roam along the
  polyline: 59–81% of captured spawns inside a territory sit within 1 world unit of a path segment, and the tail
  beyond is overwhelmingly hand-placed. The whole box remains a fallback for members caught mid-stride between
  samples, gated by the same name guard tier 3 uses. A box-only vote mislabels a group by its neighbours (a
  `Rabbits` group reads as `Warg`), and id-propagation then copies the mistake onward.

  Id-propagation is deliberately narrow: a numeric id names a *family*, not a species, so a creature learned for
  a `(Category, CommunityId)` key is propagated only to areas on a map where that key was itself resolved by
  name or containment. Without that gate it reaches hundreds of areas on a handful of creatures — Poitain's 29
  groups all read `Panther` off a key whose only captured panthers are in Tortage, while Poitain's own captures say
  Wild Pig and Wild Cow. `SpawnAreaResolver` also resets its own machine
  assignments before re-deriving, so it is idempotent and a rules change actually reaches existing rows; a
  `Manual` assignment (`.setnpcgroup`) is never touched. Re-run it over the DB alone with
  `dotnet run --project Faolan.Importer -- --report resolve-spawn-areas`.
- **`NpcSpawnArea` is kept separate from the capture-derived `NpcSpawn` set** — the former is the RDB-authoritative
  ambient population (roaming groups), the latter is exact hand-placed / named / scripted entities. A captured NPC
  confirmed to belong to an area graduates out of `NpcSpawn` into its area (`.graduatenpcgroup`; see
  [map-render.md](../tooling/map-render.md)).

Worked example — Tarantia Noble District (map 2010): `Brawlers`/Community `295`/3 points, `RoamingCitizens1`/
Community `257`/41 points (with 5 roam paths of 51/46/39/51/39 points), `RoamingCitizens2`/Community `257`/20
points.
