#!/usr/bin/env python3
"""Dissector for the Age of Conan RDB RenderMesh resource (type 1010001) - the geometry blob a MESHINDEXFILE
(1010030) points at via <RenderMesh file="ID"/>. Gitea #275 Phase 1: prove the geometry is decodable.

Layout recovered from the simplest decor meshes (flat strips where min_y==max_y==0 give a clean bbox oracle):

  [12]  RDB-Id header     : type(u32 LE)=1010001, id(u32), marker(u32)=1000003
  [4]   version/flags     : u32 (0x9003 seen)
  [4]   texRefCount        : u32 N
  [8*N] texRefs            : N x (type u32, id u32)   -> the FCTX textures, matches the manifest <Material>
  [8]   0xFFFFFFFF x2       : null/terminator
  ...   material params    : ASCII "g_Name=...g_ExporterVersion=NNN" block (same text as the manifest)
  ...   vertex declaration : small element table (offset/format/usage); ends with stride(u32) + vertexCount(u32)
  V*S   vertex buffer      : vertexCount vertices, stride bytes each; bytes [0:12] = position float x,y,z (LE)
  [4]   triCount            : u32 T
  T*3   index buffer        : T*3 x u16 (triangle list)
  ...   trailing chunks      : e.g. a "DPPM" tag block (not needed for display)

This is the decode-first dissector; the C# reader follows once the grammar is firm. We locate (stride,vertexCount)
by scanning after the params text and validating: every position inside the manifest bbox AND every index < vertexCount.
"""
import struct, zlib, sys, os, re

RDB = os.environ.get("AOC_RDB", "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB")
MESHINDEX, RENDERMESH = 1010030, 1010001


def load_index(rdb):
    b = open(os.path.join(rdb, "le.idx"), "rb").read()
    n = struct.unpack_from("<I", b, 24)[0]
    idxoff, detoff, stride = 28, 28 + n * 8, 44
    loc = {}
    for i in range(n):
        t, iid = struct.unpack_from("<II", b, idxoff + i * 8)
        d = detoff + i * stride
        file = b[d] & 0xFF
        offset, size = struct.unpack_from("<II", b, d + 4)
        loc[(t, iid)] = (file, offset, size)
    return loc


def read(rdb, loc, t, iid):
    file, offset, size = loc[(t, iid)]
    if file == 0xFF or size == 0:
        return None
    fs = open(os.path.join(rdb, f"{file:02d}.rdbdata"), "rb")
    fs.seek(offset)
    raw = fs.read(size)
    return zlib.decompress(raw[8:]) if raw[:4] == b"IOz1" else raw


def parse_bbox(manifest_xml):
    m = re.search(r'<RenderMesh file="(\d+)"((?:\s+\w+="[^"]*")*)', manifest_xml)
    if not m:
        return None, None
    rmid = int(m.group(1))
    attrs = dict(re.findall(r'(\w+)="([^"]*)"', m.group(2)))
    keys = ["min_x", "min_y", "min_z", "max_x", "max_y", "max_z"]
    if not all(k in attrs for k in keys):
        return rmid, None
    return rmid, tuple(float(attrs[k]) for k in keys)


# The vertex declaration ends with the D3D-style end sentinel (0xFF) + a 0x11 section marker; immediately after
# come [stride u32][vertexCount u32][vertex buffer]. This is a hard anchor - no heuristic, and independent of the
# (optional) material-params text. A RenderMesh may contain several such sections (sub-meshes / LODs).
ANCHOR = b"\xff\x00\x00\x00\x11\x00\x00\x00"


def decode_geometry(rm, bbox):
    """Parse every vertex/index section anchored by the declaration-end marker. Returns a list of sub-meshes."""
    minx, miny, minz, maxx, maxy, maxz = bbox if bbox else (None,) * 6
    eps = (0.02 * max(1.0, maxx - minx, maxy - miny, maxz - minz) + 0.05) if bbox else None

    def inside(x, y, z):
        if not bbox:
            return True
        return (minx - eps <= x <= maxx + eps and miny - eps <= y <= maxy + eps and minz - eps <= z <= maxz + eps)

    subs = []
    pos = 0
    while True:
        a = rm.find(ANCHOR, pos)
        if a < 0:
            break
        pos = a + len(ANCHOR)
        base = a + len(ANCHOR)
        if base + 8 > len(rm):
            break
        stride, count = struct.unpack_from("<II", rm, base)
        if stride < 12 or stride > 256 or stride % 4 or count < 1 or count > 500000:
            continue
        vbase = base + 8
        if vbase + count * stride + 4 > len(rm):
            continue
        if not all(inside(*struct.unpack_from("<fff", rm, vbase + vi * stride)) for vi in range(count)):
            continue
        ibase = vbase + count * stride
        tri = struct.unpack_from("<I", rm, ibase)[0]
        if tri < 1 or tri > 500000 or ibase + 4 + tri * 6 > len(rm):
            continue
        idx = list(struct.unpack_from(f"<{tri*3}H", rm, ibase + 4))
        if any(i >= count for i in idx):
            continue
        subs.append((vbase, stride, count, tri, idx))
        pos = ibase + 4 + tri * 6
    return subs


def export_obj(rm, subs, path):
    with open(path, "w") as f:
        vofs = 0
        for vbase, stride, count, tri, idx in subs:
            for vi in range(count):
                x, y, z = struct.unpack_from("<fff", rm, vbase + vi * stride)
                f.write(f"v {x:.6f} {y:.6f} {z:.6f}\n")
            for i in range(0, len(idx), 3):
                f.write(f"f {idx[i]+1+vofs} {idx[i+1]+1+vofs} {idx[i+2]+1+vofs}\n")
            vofs += count


def main():
    ids = [int(a) for a in sys.argv[1:]] or [4832165]
    loc = load_index(RDB)
    outdir = "/tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/b0778add-a126-477b-8758-2620da06bf1b/scratchpad/mesh"
    os.makedirs(outdir, exist_ok=True)
    for mesh_idx_id in ids:
        mi = read(RDB, loc, MESHINDEX, mesh_idx_id)
        if not mi:
            print(f"{mesh_idx_id}: no manifest")
            continue
        rmid, bbox = parse_bbox(mi[12:].decode("latin1"))
        if (RENDERMESH, rmid) not in loc:
            print(f"{mesh_idx_id}: rmid {rmid} not local")
            continue
        rm = read(RDB, loc, RENDERMESH, rmid)
        subs = decode_geometry(rm, bbox)
        if not subs:
            print(f"{mesh_idx_id} rm={rmid} len={len(rm)} bbox={bbox}: DECODE FAILED")
            continue
        obj = os.path.join(outdir, f"mesh_{rmid}.obj")
        export_obj(rm, subs, obj)
        verts = sum(s[2] for s in subs)
        tris = sum(s[3] for s in subs)
        strides = sorted({s[1] for s in subs})
        print(f"{mesh_idx_id} rm={rmid}: OK subs={len(subs)} verts={verts} tris={tris} strides={strides} -> {os.path.basename(obj)}")


if __name__ == "__main__":
    main()
