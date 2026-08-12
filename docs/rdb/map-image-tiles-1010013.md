# The in-game world map (RDB type 1010013)

The **stylised map the client draws in the map window** — parchment tone, gilt frame, glowing lava, painted
roads — as opposed to [`1010209`](rdb-type-catalog.md), which is the plain top-down *terrain* render the
web radar and `tools/maps/render_spawn_map.py` use. Keyed by **playfield id**, same as 1010209 (`1010013:4010`
is Tortage Island). 653 entries, 73 local.

## Container: concatenated JPEGs, no wrapper

The decompressed resource is **back-to-back JPEG files with no container, index or padding** — carve on the
`FF D8 FF` SOI marker and each run to the next marker is a complete JPEG. Tortage Island is 11.4 MB / **766
tiles**.

They form a 5-level tile pyramid, in file order, each level **row-major**:

| level | grid | tile px | tiles | stitched |
|------:|------|--------:|------:|---------|
| 0 | 1×1 | 512 | 1 | 512×512 (standalone overview, includes the frame) |
| 1 | 3×3 | 256 | 9 | 768×768 |
| 2 | 6×6 | 256 | 36 | 1536×1536 |
| 3 | 12×12 | 256 | 144 | 3072×3072 |
| 4 | 24×24 | 256 | 576 | **6144×6144** |

`1 + 9 + 36 + 144 + 576 = 766`, which accounts for every tile exactly — so the pyramid arithmetic alone is
enough to extract and stitch, with no need to parse the index below. Verified by stitching level 4 and matching
it against an in-client screenshot of the same map.

## Companion index: 1010012 (`MTI1`)

Some — **not all** — of these map images are accompanied by a small index at the same id, magic `MTI1`
(6,216 B for Tortage). There are only 74 `1010012` entries against 653 `1010013`, so most maps have no index at
all; that is the practical reason to carve on the SOI marker and derive the layout from the pyramid arithmetic
rather than depending on it. Little-endian u32:

```
[4]  magic 'MTI1'
[4]  levels = 5
...  header fields: 1, 1, 512, 512, 0, 87882, 3, 3, 32, 32
[48] tile table: 766 × ( [u32 offset][u32 size] ) into the 1010013 blob
     40-byte trailer (undecoded)
```

The tile table at byte 48 reproduces the carved SOI offsets exactly. The ten header words before it are only
partly understood: `1,1,512,512` is level 0's grid and tile size and `0,87882` is its `(offset,size)`, but the
trailing `3,3,32,32` does not continue that pattern (level 1 is 3×3 with **256**-px tiles, not 32).

## ⚠️ Why the lava fit failed (superseded — see the transform below)

1010209 is georeferenced to the `1070003` `<tilemap>` extent — ⚠️ **the PAIR, `Width` *and* `Height`**, not a
single number. 15 of the 520 shipped playfields are not square (2048×1536 on eight, 1024×512 on five,
512×1024 on two), and map 1010's image is 1024×768 against a 2048×1536 extent — the same 4:3, which is what
confirms the pair rather than the width alone is the georeference. `tools/maps/render_spawn_map.py` read only
`Width` and used it for both axes, drawing every Z coordinate on that map at 3/4 of its true height; the marks
stayed inside the image, so it did not look wrong. Fixed. World → pixel is a
scale. **1010013 is not.** It carries a decorative frame, and the drawing extends past the playfield to show
surrounding sea and neighbouring landmasses.

Fitting it by the lava, whose world bounds are known from 1010209 (Tortage: world X 188–902, Z 150–1064), does
**not** converge: a loose colour mask gives 5.749 px/world-unit in X against 4.860 in Z, a strict one 5.417
against 4.509 — the same ~20 % axis mismatch both times, implying a full-extent of ~8300 × 6900 px inside a
6144² image. That is not mask noise; the lava is drawn with artistic licence rather than to scale, so it cannot
anchor the fit.

The bounds are not in the neighbouring per-playfield resources either: `1010300` is `RoomAndPortalSystem`
export metadata and `1060666` is the playfield environment (sky dome, fog). The remaining candidates are the
undecoded `MTI1` header words and trailer, and `1010220`.

## ⭐ The transform, recovered from the CLIENT (#492)

The ticket's own last suggestion was the one that worked: *"whatever draws the map window has to convert player
position to a pin, so the transform is in code."* It is, in `MapGUI\RegionMapRenderer.cpp`
(`FUN_01652460`, the waypoint placer), and it is a **pure per-axis scale with Z inverted** — no offset table, no
per-map georeference:

```
pixelX = worldX              * (imageWidth  / extentX)
pixelY = (extentZ - worldZ)  * (imageHeight / extentZ)      // ⚠️ Z is FLIPPED, X is not
```

(plus the view rect's own centring term, `((right - left) + 1) * 0.5`, which is a pan and not part of the
georeference.)

**`extentX` / `extentZ` are the PLAYFIELD's world extent**, handed to the renderer by its caller
(`FUN_015818a0` → `FUN_01650270`) as a two-float struct read from the playfield record's `+0xFC` / `+0x100` as
**unsigned** ints. That is the same quantity `1010209` is georeferenced to — so the two basemaps share one
georeference after all, and the decorative frame is drawn INSIDE that extent rather than extending it.

The renderer addresses its image as the texture **`RegionMaps/<playfieldId>`**, falling back to
`RegionMaps/default`, and works in **metres per pixel** (`RegionMapRenderer.xml` declares
`min_meter_per_pixel = 4`, `max_meter_per_pixel = 100`).

⭐ So the ~20 % axis mismatch above is explained rather than mysterious: the lava really is drawn with artistic
licence, and it was never going to anchor a fit that has no free parameters to absorb it. The remaining step is a
data lookup — read the playfield's extent pair — not a decode.

So the image is usable as a picture today; substituting it for 1010209 in a spawn render needs that extent pair
read for the map being rendered — which `tools/maps/render_spawn_map.py` already does, via `--playfield`.

## Carving and stitching: `tools/maps/stitch_map_tiles.py`

```zsh
dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1010013:<mapId> \
    --out <dir> --captures <data-repo>/Captures
python3 tools/maps/stitch_map_tiles.py <dir>/1010013_<mapId>.bin -o <mapId>.png
```

It emits a plain PNG, which is what `render_spawn_map.py --map-image` accepts — so with the transform above
already implemented there, this is the whole of the swap.

⚠️ **Three things the "1 + 9 + 36 + 144 + 576 = 766" reading above gets wrong, all of them Tortage-specific.**
The tool solves each from the images instead:

| map | overview | 256px tiles | level-1 grid | level 4 |
|---|---|---:|---|---|
| 4010 Tortage Island | 512×512 | 765 | 3×3 | 6144×6144 |
| 2000 Old Tarantia | **160×160** | 765 | 3×3 | 6144×6144 |
| 1010 Conall's Valley | **512×384** | 1020 | **4×3** | **8192×6144** |

* the overview is **not always 512** — map 2000's is 160×160;
* the grid is **not always square** — map 1010 is 4×3, and it is one of the 15 playfields whose extent is not
  square either;
* so the total is not always 766.

What IS invariant is that pyramid tiles are 256px and the overview is not, so the tool finds the tiles **by
size** rather than by position. That also drops a junk slice map 2000 carries for free.

⭐ **The overview's own aspect splits `a*b` into `a` and `b`, and it cross-checks against a different source.**
Map 1010's overview is 512×384 and its level-4 stitch is 8192×6144; its playfield `1070003 <tilemap>` extent is
2048×1536. Both are exactly 4:3 and neither number went into computing the other.

⚠️ Two refusals, both deliberate. **Not every 1010013 entry is a tile container** — map 44's body is 1,814 B and
holds no JPEG at all. And a container with **no overview** does not state its aspect, so the tool asks for
`--aspect WxH` rather than assuming square: guessing 1:1 on a 4:3 map transposes the whole basemap silently,
which is exactly the failure the extent-pair note above warns about from the other axis.

⚠️ Carving on the `FF D8 FF` SOI alone **over-counts**: a JPEG may embed an EXIF thumbnail carrying its own SOI,
which is what made map 2000 read as 768 tiles. Each image is taken from its SOI to its own `FF D9` EOI and the
scan resumes after that.

Open work tracked in Gitea #492.
