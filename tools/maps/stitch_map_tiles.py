#!/usr/bin/env python3
"""Carve and stitch RDB 1010013 - the client's stylised parchment map - into one PNG.

1010013 is a container of back-to-back JPEGs with no wrapper. It holds ONE overview image followed by a tile
PYRAMID in file order, row-major within each level: level 1 is `a x b` tiles of 256px and every level after it
DOUBLES both axes.

⚠️ **Everything here is SOLVED from the images, never assumed** - three maps and three different shapes:

    4010 Tortage Island   overview 512x512   765 tiles @256  ->  3x3, 6x6, 12x12, 24x24     (766 total)
    1010 Conall's Valley  overview 512x384   1020 tiles @256 ->  4x3, 8x6, 16x12, 32x24    (1021 total)
    2000 Old Tarantia     overview 160x160   765 tiles @256  ->  3x3, 6x6, 12x12, 24x24    (+1 junk slice)

So the documented "1x1 @512, then 3x3/6x6/12x12/24x24, 1+9+36+144+576 = 766" is **Tortage's case only**: the
overview is not always 512, the grid is not always square, and the total is not always 766. What IS invariant is
that the pyramid tiles are 256px and the overview is not - so the tiles are found by SIZE rather than by
position, which also makes the junk slice in map 2000 drop out for free.

⭐ The overview's own aspect is what splits `a*b` into `a` and `b`, so no external extent is needed: 512x512 ->
1:1 -> 3x3, and 512x384 -> 4:3 -> 4x3. That is CORROBORATED from a different source - map 1010's overview is
512x384 and stitches to 8192x6144, and its playfield `1070003 <tilemap>` extent is 2048x1536. Both are exactly
4:3, and neither number went into computing the other.

The output is a plain PNG, which is exactly what `render_spawn_map.py --map-image` accepts, so this is the whole
of the 1010209 -> 1010013 basemap swap (Gitea #492). The world -> pixel transform is unchanged and is NOT ours to
invent - the client's `RegionMapRenderer` divides by the playfield's `1070003 <tilemap>` extent PAIR:

    pixelX = worldX             * (imageWidth  / extentX)
    pixelY = (extentZ - worldZ) * (imageHeight / extentZ)      # Z is FLIPPED, X is not

so a stitched 1010013 drops in against the same `--playfield` body 1010209 already uses.

Usage:
    dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1010013:<mapId> \\
        --out <dir> --captures <data-repo>/Captures
    python3 tools/maps/stitch_map_tiles.py <dir>/1010013_<mapId>.bin -o <mapId>.png [--level N]
"""
import argparse
import io
import sys

try:
    from PIL import Image
except ImportError:
    sys.exit("stitch_map_tiles.py needs Pillow: pip install --user Pillow")

SOI = b"\xff\xd8\xff"
EOI = b"\xff\xd9"

# The pyramid's tiles are always this size; the overview image is whatever is NOT this size.
TILE_PX = 256
MAX_LEVELS = 6


def carve(blob):
    """Every JPEG in the container, in file order.

    ⚠️ Carving on SOI alone OVER-COUNTS. A JPEG may embed an EXIF THUMBNAIL, which carries its own `FF D8 FF`
    inside the outer image - map 2000 came out at 768 tiles that way, two more than its real grid, and 768
    matches no pyramid so it read as a broken container rather than as a carve bug. So each image is taken from
    its SOI to its own EOI (`FF D9`) and the scan resumes AFTER that, which skips any nested marker by
    construction.
    """
    out = []
    at = blob.find(SOI)
    while at >= 0:
        end = blob.find(EOI, at + 3)
        end = len(blob) if end < 0 else end + 2
        out.append(blob[at:end])
        at = blob.find(SOI, end)
    return out


def classify(images):
    """Split the carved images into `(overview, tiles)` by SIZE.

    The overview is the one that is not 256x256. Anything unparseable is dropped - map 2000 carries one such
    slice, and it is junk rather than a tile: its 765 real tiles solve exactly without it.
    """
    overview, tiles = None, []
    for data in images:
        try:
            image = Image.open(io.BytesIO(data))
        except Exception:
            continue
        if image.size == (TILE_PX, TILE_PX):
            tiles.append(data)
        elif overview is None:
            overview = (data, image.size)
    return overview, tiles


def solve_grid(tile_count, aspect):
    """The level-1 grid `(a, b)` and the number of levels, SOLVED from the 256px tile count.

    `n` levels hold `a*b*(1 + 4 + ... + 4^(n-1))` tiles, which gives `a*b` but not `a` and `b` separately - the
    OVERVIEW's aspect ratio splits them, because the map is drawn to the playfield's shape:

        a = sqrt(a*b * aspect),  b = a*b / a

    Returns `(a, b, levels)`, or None when the count solves nothing - which is a refusal, not a fallback:
    stitching an unsolved count would silently place every tile in the wrong grid.
    """
    for levels in range(1, MAX_LEVELS + 1):
        span = sum(4 ** k for k in range(levels))
        if tile_count % span:
            continue
        cells = tile_count // span
        a = round((cells * aspect) ** 0.5)
        if a > 0 and cells % a == 0 and abs(a / (cells // a) - aspect) < 1e-6:
            return a, cells // a, levels
    return None


def stitch(tiles, cols, rows, px):
    """One level's `cols x rows` tiles, row-major, into a single image."""
    out = Image.new("RGB", (cols * px, rows * px))
    for i, data in enumerate(tiles):
        tile = Image.open(io.BytesIO(data)).convert("RGB")
        if tile.size != (px, px):
            tile = tile.resize((px, px), Image.LANCZOS)
        out.paste(tile, ((i % cols) * px, (i // cols) * px))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("body", help="the 1010013 body from --report rdb-dump")
    ap.add_argument("-o", "--out", required=True, help="output PNG")
    ap.add_argument("--level", type=int, default=None,
                    help="pyramid level to stitch, 0 = the single 512px overview (default: the largest present)")
    ap.add_argument("--aspect", help="WxH override for the grid split (default: the overview image's own aspect)")
    args = ap.parse_args()

    overview, tiles = classify(carve(open(args.body, "rb").read()))
    if overview is None and not tiles:
        # ⚠️ Not every 1010013 entry is an image container. Small ones (map 44 is 1,814 B) carry no JPEG at all -
        # they open `[type][id][...]` and are some other record under the same type. Saying "no JPEGs" is the
        # honest report; saying "solves no pyramid" would blame the grid solver for a body that has no tiles.
        sys.exit(f"{args.body}: no JPEG in this body at all - not every 1010013 entry is a tile container.")

    if overview is None and not args.aspect:
        # ⚠️ Some playfields ship the pyramid with NO overview - every image is 256x256 - and then nothing in the
        # container states the aspect. The tile count still constrains a*b, but splitting it needs the shape from
        # somewhere, so ask rather than assume square: guessing 1:1 on a 4:3 map transposes the whole basemap
        # silently, which is the failure this tool exists to make impossible.
        sys.exit(f"{args.body}: {len(tiles)} tile(s) and no overview image, so the container does not state its "
                 f"aspect. Pass --aspect WxH (the playfield's 1070003 <tilemap> Width x Height).")

    if args.aspect:
        w, h = (int(v) for v in args.aspect.lower().split("x"))
    else:
        w, h = overview[1]

    solved = solve_grid(len(tiles), w / h)
    if solved is None:
        sys.exit(f"{args.body}: {len(tiles)} tile(s) at aspect {w}x{h} solves no pyramid - refusing to guess a grid.")

    a, b, levels = solved
    level = levels if args.level is None else args.level
    if not 0 <= level <= levels:
        sys.exit(f"level {level} is not present - this body carries the overview plus levels 1..{levels}.")

    if level == 0:
        if overview is None:
            sys.exit("level 0 is the overview image and this container has none.")
        image = Image.open(io.BytesIO(overview[0])).convert("RGB")
    else:
        step = 2 ** (level - 1)
        cols, rows = a * step, b * step
        first = sum((a * 2 ** k) * (b * 2 ** k) for k in range(level - 1))
        image = stitch(tiles[first:first + cols * rows], cols, rows, TILE_PX)

    image.save(args.out)
    print(f"overview {overview[1][0]}x{overview[1][1]} + {len(tiles)} tile(s) = {levels} level(s), "
          f"level 1 grid {a}x{b}; stitched level {level} -> {args.out} {image.size[0]}x{image.size[1]}")


if __name__ == "__main__":
    main()
