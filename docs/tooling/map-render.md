# Rendering a playfield's spawn population

`tools/maps/render_spawn_map.py` draws one map's NPC spawn population over the client's own top-down map
image: every `NpcSpawnArea` as its roam AABB plus its retail anchor points, the `NpcSpawn` set as separate
marks, and optionally one character at its stored position. It emits a PNG and a matching `.txt` table of the
same rows.

It needs no C# beyond the importer's existing `rdb-dump` report:

```zsh
# the map image (RDB 1010209) and the playfield extent (RDB 1070003 <tilemap>)
dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1010209:2070 \
    --out <dir> --RdbDir <rdb-dir> --captures <data-repo>/Captures
dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1070003:2070 \
    --out <dir> --RdbDir <rdb-dir> --captures <data-repo>/Captures

python3 tools/maps/render_spawn_map.py --map 2070 --db faolan.db \
    --map-image <dir>/1010209_2070.bin --playfield <dir>/1070003_2070.bin \
    --character <id> --out poitain.png
```

`--extent N` substitutes for `--playfield` when the tilemap width is already known. `--captures` is required
by the importer CLI even for an RDB-only report.

**Only outdoor playfields can be rendered.** 66 maps have a `1010209` entry; the other ~176
(interiors/dungeons/instances) have none at all — not CDN-gated, simply absent — so `rdb-dump` writes no file
and the script exits saying so. See [`docs/rdb/rdb-type-catalog.md`](../rdb/rdb-type-catalog.md).

## Orientation

The source PNG's **row 0 is SOUTH**, so it is flipped vertically for a north-up render — the convention
`Faolan.Web/wwwroot/js/radar.js` already encodes for the live radar. This is a property of the format, not of
one map.

Confirmed independently on Poitain (2070) against the baked collision mesh: plot the vertices of
`Terrain/2070.terrain` over the image in each of the four candidate orientations. On an outdoor playfield that
mesh is largely the playfield boundary wall and the river banks, and only under this flip does the wall trace
the image's river channels; unflipped it sits in open terrain. [V1: `Terrain/2070.terrain` collision mesh
overlaid on RDB `1010209:2070`, full resolution]

Aggregate statistics do **not** settle this and should not be reached for: correlating image brightness against
the collision occupancy grid, "find the buildings" bright-blob masks, mean-luminance-at-collision-cells, and
filled-interior IoU all land within noise across all four orientations (|r| < 0.04, IoU 0.14–0.18). Only the
unrotated case is ruled out. The full-resolution visual overlay decides it in one look.

## Rendering choices

- The shipped map PNGs use a narrow luminance band (Poitain's spans 33–133 of 255), so the background is
  contrast-expanded and then dimmed. A raw draw is muddy; a full stretch leaves the surface too bright for the
  overlay marks to clear their contrast floor.
- Output defaults to `--dpi 260` (~4100px plot, 4420×4732 image, ~22 MB, ~14 s) with `--bg-scale 4`, which takes
  the 1024px source map to 4096. The two are meant to be set together: the **overlay is vector**, so it is drawn
  at output resolution and stays crisp, while the background is a fixed-resolution image that simply gets softer
  the further it is scaled. Raising `--bg-scale` past the plot's pixel width buys nothing — the surplus is
  resampled straight back out. Drop both (`--dpi 110 --bg-scale 2`) for a quick ~1870px preview.
- Groups are coloured by creature-resolution provenance — resolved / unresolved / `Community` — using the first
  three categorical slots, the three that validate on an all-pairs basis. Those slots sit below a 3:1 contrast
  ratio against the map surface, which is why every group also carries a direct label and the `.txt` table
  ships beside the PNG. Do not drop either.
- A **dashed** group edge means its creature was `IdPropagation` — learned from one `Name`/`Containment`
  resolution and stamped onto every same-id group, so it is a guess, not a match. This is a deliberate secondary
  encoding rather than a fourth hue: confidence is a different question from category, and palette slot 4 would
  fail the all-pairs floors against slot 2 anyway. It matters — across the DB `IdPropagation` covers 329 areas
  but only **6 distinct creatures**, versus 170 areas / 14 creatures for the trustworthy `Name` tier. On Poitain
  all 29 "resolved" groups are id-propagated to `Panther` from a `(Superfamily, 4)` key whose only captured
  Panthers are 14 spawns in Tortage; a superfamily is a whole family, so one species standing for all of it is
  very likely wrong. The `.txt` table carries the same provenance in a `via` column.
- Each group's label is `<name> ×<MaxCount>` — its population. `MaxCount` equals the group's anchor-point count
  for all 2664 areas in the DB, so the one number says both. Because group boxes overlap and abut, labels are
  decluttered by nudging each one down (into its own box) until it clears the already-placed ones and the
  character marker; extents are approximated from the font size, since measuring them would need a draw pass.
- `NpcSpawnArea` and `NpcSpawn` are drawn as distinct layers because they are distinct populations: the former
  is the RDB-authoritative ambient roam group, the latter an exact entity observed in a retail capture.

## Reading it as #363 migration progress

The `NpcSpawn` layer is EVERY row of that table for the map, **not** the subset lacking an area — and that table
is transitional. `NpcSpawn.NpcSpawnAreaId` is a temporary MEMBERSHIP marker on the way out of it (#363):

| stage | mechanism |
|---|---|
| resolve the area's creature | `.setnpcgroup` → `DatabaseRepository.SetNpcSpawnAreaCreature` (`ResolvedBy = Manual`) |
| link its captured spawns | `SpawnAreaResolver.LinkCapturedSpawns` (creature match + bounds ±5u) or `.setnpcgroup`; reversible via `UnlinkNpcSpawnsFromArea` |
| graduate | `.graduatenpcgroup` → `GraduateSpawnArea` **deletes** the linked `NpcSpawn` rows |
| spawn from then on | a resolved area becomes an `AmbientPool` (`WorldBroadcaster.LoadAmbientSpawns`) holding ~`MaxCount` on the group's points, wandering (#266), fading on death, refilled by `MaintainAmbientPools` |

Linking is already non-duplicating **at runtime** — `WorldBroadcaster` treats a linked captured spawn as a member
of that area's pool rather than a separate entity, so there is no captured/ambient overlap in the live world.
Graduation makes that permanent in the data and drops the frozen captured position in favour of the pool's wander.

So the render doubles as a progress view: an **unresolved** (orange) group is work remaining; a diamond **inside**
a resolved group is linked-or-linkable, i.e. ready to graduate; a diamond **outside** every group is a genuine
independent placement (named/quest NPC) that should survive. As graduation proceeds the diamonds drain away and
`NpcSpawns` shrinks toward only that last category. It is null for all 1228 rows on every map today, so nothing
has graduated yet and the two layers are currently disjoint.
