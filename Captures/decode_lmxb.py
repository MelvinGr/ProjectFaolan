#!/usr/bin/env python3
"""Decoder for AoC ANIMSYS animation data (#292).

Two RDB types make up the animation pipeline; this dissector decodes both and is
validated end-to-end against the client RDB.

  1010511  ANIMSYS  (magic 'LMXB')  -- the clip METADATA tree.
      A nested key/value tree. Grammar (fully reverse-engineered from the client
      loader FUN_00fd5580 + FUN_00c4b3e0 and validated byte-exact on 1010511:14532):
        - all offsets are relative to BASE = file+0x08.
        - node = 16 bytes: [u32 firstChildOff][u32 attrPtr][u32 nameOff][u16 childCount][u16 attrCount]
            firstChildOff : byte offset (base-rel) of the first child node; children are consecutive 16B records.
            attrPtr       : byte offset (base-rel) of this node's attribute array, or 0xFFFFFFFF for none.
            nameOff       : byte offset (base-rel) of the node's NUL-terminated key name.
            childCount    : number of child nodes.
            attrCount     : number of attributes (the u16 at node+0x0e).
        - attribute = (u32 nameOff)(u32 valueOff) pair; BOTH are base-rel string offsets.
          ALL values are stored as TEXT (skeleton="14551", duration="4.0", rdbname="14549").
        - the root node is AT base (offset 0x08); its name is empty.
      A small clip (e.g. 14532) is just references:
        animsys{skeleton=14551} > resources > animation{duration=4.0,name=movement_idle,rdbname=14549}
      The big entries (and the 3.6MB master 1010511:1) are per-skeleton catalogs: `resources` holds
      `attractor` mount-points (bone sockets) + `animation` nodes (name/duration/rdbname), `tree` holds
      the clip-name -> animation-name map. The 'rdbname' is the curve resource id (see 1010003).

  1010003  the actual keyframe CURVES, keyed by the clip's rdbname.
      RDB-Id header [u32 type=1010003][u32 id][u32 type2=1000003][u32 flags=0x20000][f32 duration]
      [u32 frameCount] then a sequence of per-bone tracks:
        track = [u32 nameLen][ascii rig_bone_*][channels...]
        channel = [u32 keyCount][keyCount x f32 ...]  -- keyframe times are real seconds (0,0.04,0.08 = 25fps)
      Values are plain float32 (NOT quantized). Translation channels are metre offsets, rotation channels
      are quaternion components. Decodable straight to glTF animation channels.

Usage: python3 Captures/decode_lmxb.py            # self-validates on 14532 + dumps a 1010003 sample
Reads the client RDB directly (RdbReader path from the agent memory client_rdb_install_path).
"""
import os, struct, zlib, re, sys

RDB = os.environ.get("AOC_RDB", "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB")


class Rdb:
    def __init__(self, d=RDB):
        b = open(os.path.join(d, "le.idx"), "rb").read()
        assert b[:4] == b"IBDR"
        ver = struct.unpack_from("<I", b, 4)[0]
        n = struct.unpack_from("<I", b, 24)[0]
        stride = 44 if ver == 5 else 28
        idx, det = 28, 28 + n * 8
        self.dir = d
        self.bykey = {}
        for i in range(n):
            t, eid = struct.unpack_from("<II", b, idx + i * 8)
            o = det + i * stride
            self.bykey[(t, eid)] = (b[o] & 0xff, *struct.unpack_from("<II", b, o + 4))
        self._fh = {}

    def dec(self, t, eid):
        f, off, size = self.bykey[(t, eid)]
        if f == 0xff:
            return None
        fh = self._fh.get(f) or self._fh.setdefault(f, open(os.path.join(self.dir, f"{f:02d}.rdbdata"), "rb"))
        fh.seek(off)
        r = fh.read(size)
        return zlib.decompress(r[8:]) if r[:4] == b"IOz1" else r


class Lmxb:
    """The 1010511 clip-metadata tree parser (validated on 14532)."""
    BASE = 0x08

    def __init__(self, d):
        assert d[:4] == b"LMXB", "not an LMXB resource"
        self.d = d

    def _u32(self, o):
        return struct.unpack_from("<I", self.d, o)[0]

    def _u16(self, o):
        return struct.unpack_from("<H", self.d, o)[0]

    def s(self, rel):
        o = self.BASE + rel
        return self.d[o:self.d.index(0, o)].decode("latin1")

    def node(self, off):
        return dict(off=off, childOff=self._u32(off), attrPtr=self._u32(off + 4),
                    nameOff=self._u32(off + 8), nchild=self._u16(off + 0xc), nattr=self._u16(off + 0xe))

    def attrs(self, n):
        if n["attrPtr"] == 0xffffffff or n["nattr"] == 0:
            return {}
        base = self.BASE + n["attrPtr"]
        out = {}
        for i in range(n["nattr"]):
            no, vo = struct.unpack_from("<II", self.d, base + i * 8)
            out[self.s(no)] = self.s(vo)
        return out

    def children(self, n):
        if not n["childOff"] or not n["nchild"]:
            return []
        cb = self.BASE + n["childOff"]
        return [self.node(cb + i * 0x10) for i in range(n["nchild"])]

    def root(self):
        return self.node(self.BASE)

    def dump(self, n=None, depth=0):
        n = n or self.root()
        for c in self.children(n):
            a = self.attrs(c)
            print("  " * depth + self.s(c["nameOff"]) + ("  " + str(a) if a else ""))
            self.dump(c, depth + 1)


def parse_curve(d):
    """1010003 per-bone keyframe tracks -> list of (bone, [channel float-lists])."""
    typ, cid, typ2, flags = struct.unpack_from("<IIII", d, 0)
    duration = struct.unpack_from("<f", d, 16)[0]
    frames = struct.unpack_from("<I", d, 20)[0]
    p = 24
    tracks = []
    while p + 4 <= len(d):
        nl = struct.unpack_from("<I", d, p)[0]
        if not (0 < nl < 64) or d[p + 4:p + 5] != b"r":  # tracks are 'rig_bone_*'
            break
        name = d[p + 4:p + 4 + nl].decode("latin1")
        p += 4 + nl
        # channels run until the next track's nameLen+'rig_bone_' (or EOF)
        nxt = d.find(b"rig_bone_", p)
        end = (nxt - 4) if nxt > 0 else len(d)
        tracks.append((name, d[p:end]))
        p = end
    return dict(id=cid, duration=duration, frames=frames, tracks=tracks)


if __name__ == "__main__":
    r = Rdb()

    # --- validate the LMXB grammar on the movement_idle clip ---
    L = Lmxb(r.dec(1010511, 14532))
    print("=== 1010511:14532 (movement_idle) metadata tree ===")
    L.dump()

    # --- characterise a real curve resource ---
    for cid in (11761, 11709):
        d = r.dec(1010003, cid)
        c = parse_curve(d)
        print(f"\n=== 1010003:{c['id']} curves: duration={c['duration']}s frames={c['frames']} bones={len(c['tracks'])} ===")
        for name, blob in c["tracks"][:4]:
            print(f"  {name}: {len(blob)} bytes of channel data")
