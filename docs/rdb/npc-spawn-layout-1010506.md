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
  name / gated containment / guarded id-propagation), which resolves ~500 of the 2,664 areas; the rest await
  captures or a manual pass. See #363.
- **`NpcSpawnArea` is kept separate from the capture-derived `NpcSpawn` set** — the former is the RDB-authoritative
  ambient population (roaming groups), the latter is exact hand-placed / named / scripted entities. The long-term
  plan is for a captured NPC confirmed to belong to an area to graduate out of `NpcSpawn` into its area.

Worked example — Tarantia Noble District (map 2010): `Brawlers`/Community `295`/3 points, `RoamingCitizens1`/
Community `257`/41 points (with 5 roam paths of 51/46/39/51/39 points), `RoamingCitizens2`/Community `257`/20
points.
