# Open-world NPC spawn layout grammar (RDB type 1010506) — #332

The per-playfield **authoritative, capture-independent** open-world NPC spawn source. Where our normal NPC
placements are lifted from network captures (`NpcSpawnExtractor` → the burst-truth `NpcSpawn` set), this RDB
type ships the spawn-point layout for every open-world playfield the client renders. Decoded decode-first (probe
in `Faolan.Core.Rdb/Assets/NpcSpawnLayout.cs`) and **validated byte-exact**: all **238 local** `1010506`
records parse to EOF (0 failures), **119 carry spawn groups** (2,664 groups / 23,782 primary spawn points) and
**119 are empty** (`groupCount == 0` — scripted/instanced playfields like the castle and the Tortage interiors,
whose NPCs are server-scripted and never appear here). The RDB record id **== the playfield id** (== `Map.Id`).

Ported to C# as `NpcSpawnLayout` and imported by `RdbNpcSpawnImporter` (the opt-in `rdb-npc-spawns` module) into
the `RdbSpawnPoint` table, keyed by the stable `(MapId, GroupOrdinal, PointIndex)` triple.

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
[12]  boundsMin       vec3   AABB min corner over the group's points
[12]  boundsMax       vec3   AABB max corner (max.Y is padded to min.Y + 10)
[4]   pathCount       u32
      pathCount × ( [4] n u32, n × vec3 )   the roam/patrol paths the group's NPCs walk
```

`vec3` = `[f32 x][f32 y][f32 z]`.

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
- **1010506 carries no creature template** (except `Name` groups), so `RdbSpawnPoint` is placement geometry +
  grouping only. Matching a captured NPC to its nearest authoritative point (to assign the stable id / snap the
  position / infer per-group density) is the #332 follow-up and is **not** wired into the capture-derived
  `NpcSpawn` set yet — the two sets are kept separate for now.

Worked example — Tarantia Noble District (map 2010): `Brawlers`/Community `295`/3 points, `RoamingCitizens1`/
Community `257`/41 points (with 5 roam paths of 51/46/39/51/39 points), `RoamingCitizens2`/Community `257`/20
points.
