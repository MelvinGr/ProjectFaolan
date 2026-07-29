#!/usr/bin/env python3
"""Render a playfield's NPC spawn population over the client's own top-down map image.

Draws, for one map id, every `NpcSpawnArea` (the RDB-authoritative ambient spawn GROUP: a roam AABB + a
population count + the exact retail anchor points) plus the `NpcSpawn` set (the exact entities observed in retail
packet captures - named / quest / scripted placements, kept deliberately separate from the ambient groups),
optionally marking one character's stored position. Output is a PNG plus a matching plain-text table - the table
is not decoration, it is the accessible/greppable view of the same rows, and it is what makes the group colours
legible to a reader who cannot rely on hue.

The `NpcSpawn` layer is EVERY row of that table for the map, NOT the subset with no spawn area, and that table is
TRANSITIONAL (#363): `NpcSpawn.NpcSpawnAreaId` is a temporary membership marker on the way out of it. Resolve an
area's creature (`.setnpcgroup`), its captured spawns link to it, then `.graduatenpcgroup` DELETES those rows and
the area's ambient pool becomes the sole source - spawning ~MaxCount on the group's points and wandering, instead
of replaying frozen captured positions.

So this render doubles as a progress view of that migration: an UNRESOLVED group is work remaining; a spawn mark
INSIDE a resolved group is ready to graduate; a spawn mark OUTSIDE every group is a genuine independent placement
(named/quest NPC) that should survive. Nothing has graduated yet - the marker is null on every map today.

Everything it needs already exists in the tree; there is no C# to add:

  # the map image (RDB 1010209, a PNG behind a short preamble) and the playfield extent (RDB 1070003 <tilemap>)
  dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1010209:<mapId> --out <dir> --rdb <rdb-dir>
  dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1070003:<mapId> --out <dir> --rdb <rdb-dir>

  python3 tools/maps/render_spawn_map.py --map <mapId> \
      --map-image <dir>/1010209_<mapId>.bin --playfield <dir>/1070003_<mapId>.bin \
      --db faolan.db --out <mapId>-spawn-groups.png

Only 66 (outdoor) playfields have a 1010209 entry at all; interiors/dungeons simply have none, so this is an
outdoor-map tool. `--extent N` substitutes for `--playfield` when you already know the tilemap width.

⚠️ ORIENTATION. The source PNG's row 0 is SOUTH, so it is flipped vertically for a north-up render - the same
convention `Faolan.Web/wwwroot/js/radar.js` encodes. This is a property of the FORMAT, not of one map, and it
has been confirmed independently: overlay a map's baked collision mesh (`Terrain/<mapId>.terrain`, which on an
outdoor playfield is largely the boundary wall and the river banks) and only under this flip does the wall trace
the image's river channels. Do NOT try to re-derive it from aggregate statistics - brightness correlation,
"find the buildings" blob masks and filled-interior IoU all come back within noise across all four candidate
orientations. The full-resolution visual overlay decides it in one look.

The map image is deliberately contrast-expanded and then dimmed: the shipped PNGs use only a narrow luminance
band (Poitain's spans 33..133 of 255), so a raw draw is muddy while a full stretch leaves the surface too bright
for the overlay to clear its contrast floor. Expanding then dimming gets legible terrain under a dark surface.

Group colours are the first three categorical slots of the house palette, which are the three that validate on
an all-pairs basis (scatter-like marks, not a stacked series). They sit below a 3:1 contrast ratio against the
map surface, which is why every group also carries a direct label and the text table ships alongside.
"""

import argparse
import json
import sqlite3
import re
import sys

import numpy as np
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.patheffects as pe
from matplotlib.lines import Line2D
from matplotlib.patches import Rectangle
from PIL import Image

PNG_SIG = b"\x89PNG\r\n\x1a\n"

# Categorical slots 1-3 (dark steps). Validated all-pairs against a dimmed map surface (~#463d1d).
C_RESOLVED = "#3987e5"
C_UNRESOLVED = "#d95926"
C_COMMUNITY = "#199e70"
INK = "#f2f1ea"
INK_DIM = "#c3c2b7"
PANEL = "#14130d"


def load_map_image(path):
    """The rdb-dump body carries a short [type][id][count] preamble before the PNG; a plain .png also works."""
    try:
        blob = open(path, "rb").read()
    except FileNotFoundError:
        sys.exit(f"{path}: not found. rdb-dump writes nothing when the map has no 1010209 entry - only the 66 "
                 f"outdoor playfields have one, interiors/dungeons have none, so they cannot be rendered.")
    idx = blob.find(PNG_SIG)
    if idx < 0:
        sys.exit(f"{path}: no PNG signature - is this a 1010209 body?")
    import io

    return Image.open(io.BytesIO(blob[idx:]))


def load_extent(path):
    xml = open(path, "rb").read().decode("latin-1")
    m = re.search(r'<tilemap[^>]*Width="(\d+)"', xml)
    if not m:
        sys.exit(f"{path}: no <tilemap Width=...> - is this a 1070003 body?")
    return int(m.group(1))


def load_rows(db, map_id, character_id):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    areas = [dict(r) for r in con.execute(
        "SELECT a.Id, a.GroupOrdinal, a.GroupName, a.Category, a.MaxCount, a.NpcTemplateId, a.ResolvedBy,"
        "       a.BoundsMinX, a.BoundsMaxX, a.BoundsMinZ, a.BoundsMaxZ, t.Name AS TemplateName"
        "  FROM NpcSpawnAreas a LEFT JOIN NpcTemplates t ON t.TemplateId = a.NpcTemplateId"
        " WHERE a.MapId = ? ORDER BY a.GroupOrdinal", (map_id,))]
    for a in areas:
        a["pts"] = [(r["PositionX"], r["PositionZ"]) for r in con.execute(
            "SELECT PositionX, PositionZ FROM NpcSpawnAreaPoints WHERE NpcSpawnAreaId = ? ORDER BY PointIndex",
            (a["Id"],))]
    spawns = [dict(r) for r in con.execute(
        "SELECT s.PositionX, s.PositionZ, s.Level, t.Name"
        "  FROM NpcSpawns s JOIN NpcTemplates t ON t.TemplateId = s.NpcTemplateId"
        " WHERE s.MapId = ?", (map_id,))]
    name = con.execute("SELECT Name FROM Maps WHERE Id = ?", (map_id,)).fetchone()
    player = None
    if character_id:
        player = con.execute(
            "SELECT Name, Level, PositionX, PositionZ, MapId FROM Characters WHERE Id = ?",
            (character_id,)).fetchone()
        if player and player["MapId"] != map_id:
            print(f"note: character {character_id} is on map {player['MapId']}, not {map_id} - not marking it")
            player = None
    con.close()
    return (name["Name"] if name else str(map_id)), areas, spawns, player


# SpawnResolutionSource (Faolan.Core/Enums). 3 = IdPropagation: a creature learned from ONE Name/Containment
# resolution and stamped onto every same-id group, so it is a guess and often a whole family collapsed to one
# species. Rendered dashed, because drawing it like a verified assignment overclaims what the DB knows.
RESOLVED_TAG = {0: "unresolved", 1: "name", 2: "containment", 3: "id-prop", 4: "manual"}
GUESSED = 3


def kind(a):
    if a["Category"] == "Community":
        return "community"
    return "resolved" if a["NpcTemplateId"] else "unresolved"


def place_label(text, x, z_top, placed, char_w, line_h, tries=6):
    """Nudge a label DOWN (into its own box) until it clears already-placed labels and obstacles.

    Group boxes overlap and abut, so top-left labels collide - badly once each carries a count. A greedy
    downward shift fixes almost all of it: boxes are far taller than a line, so the label stays inside its own
    rectangle and next to its own edge. Returns the chosen z and records the rect. Extents are approximated from
    the font size rather than measured - a real measurement needs a draw pass, and this only has to be close
    enough to stop overlaps.
    """
    w = len(text) * char_w
    for _ in range(tries):
        rect = (x, x + w, z_top - line_h, z_top)
        if not any(rect[0] < p[1] and p[0] < rect[1] and rect[2] < p[3] and p[2] < rect[3] for p in placed):
            break
        z_top -= line_h * 1.05
    placed.append((x, x + w, z_top - line_h, z_top))
    return z_top


def main():
    p = argparse.ArgumentParser(description="Render a playfield's NPC spawn groups over the client map image.")
    p.add_argument("--map", type=int, required=True, help="map / playfield id, e.g. 2070")
    p.add_argument("--db", default="faolan.db", help="path to faolan.db (default: ./faolan.db)")
    p.add_argument("--map-image", required=True, help="RDB 1010209 body from rdb-dump (or a plain .png)")
    p.add_argument("--playfield", help="RDB 1070003 body from rdb-dump, for the tilemap extent")
    p.add_argument("--extent", type=int, help="tilemap width in world units, instead of --playfield")
    p.add_argument("--character", type=int, help="optional character id to mark at its stored position")
    p.add_argument("--out", help="output PNG (default: <mapId>-spawn-groups.png)")
    p.add_argument("--dpi", type=int, default=260,
                   help="output resolution (default 260 -> a ~4100px plot, ~4400px image). Raising this sharpens "
                        "the OVERLAY, which is vector - marks, boxes and text are drawn at output resolution.")
    p.add_argument("--bg-scale", type=int, default=4,
                   help="upscale the background by this factor (Lanczos) before drawing (default 4, taking the "
                        "1024px source map to 4096). Pair it with --dpi so the upscaled background roughly "
                        "matches the plot's pixel width - beyond that the extra pixels are just resampled away.")
    args = p.parse_args()

    if not args.playfield and not args.extent:
        p.error("one of --playfield or --extent is required")
    world = float(args.extent or load_extent(args.playfield))
    out_png = args.out or f"{args.map}-spawn-groups.png"
    out_txt = out_png.rsplit(".", 1)[0] + ".txt"

    map_name, areas, spawns, player = load_rows(args.db, args.map, args.character)
    if not areas and not spawns:
        sys.exit(f"map {args.map}: no NpcSpawnAreas and no NpcSpawns in {args.db}")

    # A single label per bucket. The resolved bucket names its creature when the whole map agrees on one.
    resolved_names = {a["TemplateName"] for a in areas if kind(a) == "resolved"}
    resolved_label = ("creature resolved → " + resolved_names.pop()) if len(resolved_names) == 1 \
        else f"creature resolved ({len(resolved_names)} types)"
    style = {
        "resolved": (C_RESOLVED, resolved_label),
        "unresolved": (C_UNRESOLVED, "creature UNRESOLVED"),
        "community": (C_COMMUNITY, "Community group"),
    }

    # Row 0 of the source is SOUTH - flip for north-up (see the module docstring).
    src = load_map_image(args.map_image).convert("RGB").transpose(Image.FLIP_TOP_BOTTOM)
    if args.bg_scale > 1:
        src = src.resize((src.width * args.bg_scale, src.height * args.bg_scale), Image.LANCZOS)
    raw = np.asarray(src).astype(float)
    lo, hi = np.percentile(raw, 2), np.percentile(raw, 99.5)
    img = ((((raw - lo) / max(hi - lo, 1e-6) * 205).clip(0, 255)) * 0.66).astype(np.uint8)

    fig = plt.figure(figsize=(17.0, 18.2), dpi=args.dpi)
    fig.patch.set_facecolor("#0d0c08")
    ax = fig.add_axes([0.035, 0.028, 0.93, 0.885])
    ax.set_facecolor("#0d0c08")
    ax.imshow(img, extent=(0, world, 0, world), origin="lower", interpolation="bilinear")
    ax.set_xlim(0, world)
    ax.set_ylim(0, world)
    ax.set_aspect("equal")
    for g in range(256, int(world), 256):
        ax.axhline(g, color=INK, lw=0.4, alpha=0.10, zorder=2)
        ax.axvline(g, color=INK, lw=0.4, alpha=0.10, zorder=2)
    ax.tick_params(colors=INK_DIM, labelsize=8, length=3)
    for s in ax.spines.values():
        s.set_color("#3a3728")
    ax.set_xlabel("world X", color=INK_DIM, fontsize=9, labelpad=4)
    ax.set_ylabel("world Z  (north up)", color=INK_DIM, fontsize=9, labelpad=4)

    # Label extents in WORLD units, so the declutter holds at any map extent. Sizes are quoted in px at the
    # REFERENCE dpi below and deliberately not read off `fig.dpi`: font and marker sizes are in POINTS, so they
    # keep the same physical fraction of the figure at any dpi - dividing by the real pixel width would shrink
    # these as --dpi rises and let labels collide again. Measured by eye against a render, since exact extents
    # would need a draw pass and only overlap-avoidance depends on them.
    ref_dpi = 110.0
    per_px = world / (fig.get_size_inches()[0] * ref_dpi * 0.93)
    char_w, line_h = 7.0 * per_px, 12.0 * per_px
    # The character marker is an obstacle too, else its star sits on top of whichever group label it lands in.
    # Only the STAR: treating the callout as one too was tried and reverted - a rectangle crude enough to cover
    # two lines of offset text displaces distant labels that never overlapped it, while the label it was meant
    # to move merely abuts the callout and stays readable.
    placed = []
    if player:
        r = 18.0 * per_px  # the s=420 star is ~31px across at the reference dpi
        placed.append((player["PositionX"] - r, player["PositionX"] + r,
                       player["PositionZ"] - r, player["PositionZ"] + r))

    # Biggest boxes first so a small group nested inside a large one stays clickable-looking / on top.
    for a in sorted(areas, key=lambda a: -((a["BoundsMaxX"] - a["BoundsMinX"]) * (a["BoundsMaxZ"] - a["BoundsMinZ"]))):
        col = style[kind(a)][0]
        x0, z0 = a["BoundsMinX"], a["BoundsMinZ"]
        w, h = a["BoundsMaxX"] - x0, a["BoundsMaxZ"] - z0
        ax.add_patch(Rectangle((x0, z0), w, h, facecolor=col, alpha=0.13, edgecolor="none", zorder=3))
        # Secondary encoding, not a fourth hue: a dashed edge marks a creature the resolver GUESSED by
        # id-propagation rather than matched. Slot 4 of the palette would fail the all-pairs floors against
        # slot 2 anyway, and confidence is a different question from category - it belongs on a different channel.
        ax.add_patch(Rectangle((x0, z0), w, h, facecolor="none", edgecolor=col, lw=1.5, alpha=0.95, zorder=5,
                               linestyle=(0, (4, 2)) if a["ResolvedBy"] == GUESSED else "solid"))
        if a["pts"]:
            pts = np.array(a["pts"])
            ax.scatter(pts[:, 0], pts[:, 1], s=26, facecolor=col, edgecolor=PANEL, linewidths=0.9, zorder=7)
        # The direct label is the contrast relief the palette's sub-3:1 slots require - do not drop it. `MaxCount`
        # is the group's population, and equals its anchor count for every area in the DB, so one number says both.
        text = f"{re.sub(r'^Territory', 'T', a['GroupName'])} ×{a['MaxCount']}"
        z_lbl = place_label(text, x0 + 4, z0 + h - 5, placed, char_w, line_h)
        ax.text(x0 + 4, z_lbl, text, color=INK, fontsize=7.6, fontweight="bold", ha="left", va="top", zorder=9,
                path_effects=[pe.withStroke(linewidth=2.6, foreground=PANEL, alpha=0.9)])

    if spawns:
        ax.scatter([s["PositionX"] for s in spawns], [s["PositionZ"] for s in spawns],
                   s=30, marker="D", facecolor=INK_DIM, edgecolor=PANEL, linewidths=0.9, alpha=0.95, zorder=8)

    if player:
        px, pz = player["PositionX"], player["PositionZ"]
        ax.scatter([px], [pz], s=420, marker="*", facecolor="#ffffff", edgecolor=PANEL, linewidths=1.6, zorder=12)
        ax.annotate(f"{player['Name']}  ·  level {player['Level']}\n({px:.0f}, {pz:.0f})", (px, pz),
                    xytext=(px + world * 0.023, pz - world * 0.038), color=INK, fontsize=10.5,
                    fontweight="bold", zorder=12,
                    path_effects=[pe.withStroke(linewidth=3.0, foreground=PANEL, alpha=0.92)],
                    arrowprops=dict(arrowstyle="-", color=INK, lw=1.2, alpha=0.85,
                                    path_effects=[pe.withStroke(linewidth=2.8, foreground=PANEL, alpha=0.9)]))

    counts, anchors = {k: 0 for k in style}, {k: 0 for k in style}
    for a in areas:
        counts[kind(a)] += 1
        anchors[kind(a)] += a["MaxCount"]
    handles = [Line2D([], [], marker="s", linestyle="none", markersize=11, markerfacecolor=col,
                      markeredgecolor=col, alpha=0.9,
                      label=f"{lbl}   ·   {counts[k]} groups, {anchors[k]} anchors")
               for k, (col, lbl) in style.items() if counts[k]]
    guessed = sum(1 for a in areas if a["ResolvedBy"] == GUESSED)
    if guessed:
        handles.append(Line2D([], [], color=INK_DIM, lw=1.5, linestyle=(0, (4, 2)),
                              label=f"dashed = creature GUESSED by id-propagation, unverified   ·   {guessed} of "
                                    f"{sum(counts[k] for k in ('resolved',))} resolved"))
    if spawns:
        handles.append(Line2D([], [], marker="D", linestyle="none", markersize=8, markerfacecolor=INK_DIM,
                              markeredgecolor=PANEL, label=f"captured NpcSpawn   ·   {len(spawns)} rows"))
    if player:
        handles.append(Line2D([], [], marker="*", linestyle="none", markersize=16, markerfacecolor="#ffffff",
                              markeredgecolor=PANEL, label=f"{player['Name']} (stored position)"))
    leg = ax.legend(handles=handles, loc="upper right", bbox_to_anchor=(0.998, 0.998), frameon=True,
                    fontsize=10.0, labelspacing=0.85, handletextpad=0.9, borderpad=1.0)
    leg.get_frame().set_facecolor(PANEL)
    leg.get_frame().set_edgecolor("#4a4636")
    leg.get_frame().set_alpha(0.93)
    for t in leg.get_texts():
        t.set_color(INK)
    leg.set_zorder(20)

    fig.text(0.035, 0.972, f"{map_name} — NPC spawn groups", color=INK, fontsize=27, fontweight="bold",
             ha="left", va="top")
    fig.text(0.035, 0.943,
             f"map {args.map}  ·  {len(areas)} RDB spawn groups (roam AABB + {sum(a['MaxCount'] for a in areas)} "
             f"retail anchor points)  ·  {len(spawns)} captured NpcSpawns",
             color=INK_DIM, fontsize=12.5, ha="left", va="top")
    fig.text(0.965, 0.977,
             "background: client RDB 1010209, contrast-expanded  ·  extent: RDB 1070003 <tilemap>, "
             f"{int(world)}×{int(world)} world units\n"
             "groups: NpcSpawnAreas + NpcSpawnAreaPoints (faolan.db)\n"
             "north-up: source row 0 is south (see radar.js; confirmed against the baked collision mesh)",
             color=INK_DIM, fontsize=8.8, ha="right", va="top", linespacing=1.7)

    fig.savefig(out_png, facecolor=fig.get_facecolor())
    print(f"wrote {out_png}")

    # The table names the creature in its own column, so the kind column carries the short bucket token rather
    # than the legend's prose label.
    token = {"resolved": "resolved", "unresolved": "UNRESOLVED", "community": "community"}
    rows = [f"{map_name} (map {args.map}) - {len(areas)} spawn groups, {len(spawns)} captured NpcSpawns",
            "", "ord  group        kind        creature              via          count  roam AABB (X / Z)"]
    for a in areas:
        rows.append(f"{a['GroupOrdinal']:>3}  {a['GroupName']:<12} {token[kind(a)]:<11} "
                    f"{(a['TemplateName'] or '-'):<21} {RESOLVED_TAG.get(a['ResolvedBy'], '?'):<12} "
                    f"{a['MaxCount']:>5}  "
                    f"{a['BoundsMinX']:.0f}-{a['BoundsMaxX']:.0f} / {a['BoundsMinZ']:.0f}-{a['BoundsMaxZ']:.0f}")
    open(out_txt, "w").write("\n".join(rows) + "\n")
    print(f"wrote {out_txt}")


if __name__ == "__main__":
    main()
