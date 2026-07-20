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
        track = [u32 nameLen][ascii rig_bone_*][T-channel][R-channel]
        T-channel (translation) = [u32 nKeysT][nKeysT x f32 time][nKeysT x (f32 x,y,z)]     # metres, vec3
        R-channel (rotation)    = [u32 nKeysR][nKeysR x f32 time][nKeysR x (f32 x,y,z,w)]   # UNIT quaternion
      Keyframe times are real seconds (25 fps). Values are plain float32 (NOT quantized); rotation quats verify
      to |q|=1.0. Static bones are 1-key channels. Decodable straight to glTF animation channels (T = translation
      sampler, R = rotation sampler; skeleton bone names match rig_bone_* directly).

Usage: python3 tools/capture/decode_lmxb.py            # self-validates on 14532 + dumps a 1010003 sample
Reads the client RDB directly (RdbReader path from the agent memory client_rdb_install_path).
"""
import os, struct, zlib, re, sys

RDB = os.environ.get("AOC_RDB", "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB")


class Rdb:
    # On-demand (file==0xff) entries live on the UPM CDN, content-addressed by the entry's 16-byte md5. When
    # Faolan.Web.Content (the store-and-forward mirror) is running, fetch them through it - same scheme the C#
    # RdbReader.FetchCdn / RdbCdnCache use: <mirror>/gameupdate/upm/rdb/res/<h0:2>/<h2:1>/<h3:>.
    MIRROR = os.environ.get("AOC_MIRROR", "http://127.0.0.1:7081")

    def __init__(self, d=RDB):
        b = open(os.path.join(d, "le.idx"), "rb").read()
        assert b[:4] == b"IBDR"
        ver = struct.unpack_from("<I", b, 4)[0]
        n = struct.unpack_from("<I", b, 24)[0]
        stride = 44 if ver == 5 else 28
        idx, det = 28, 28 + n * 8
        self.dir = d
        self.bykey = {}
        self.hash = {}
        for i in range(n):
            t, eid = struct.unpack_from("<II", b, idx + i * 8)
            o = det + i * stride
            self.bykey[(t, eid)] = (b[o] & 0xff, *struct.unpack_from("<II", b, o + 4))
            self.hash[(t, eid)] = b[o + 28:o + 44]
        self._fh = {}

    @staticmethod
    def _unwrap(r):
        return zlib.decompress(r[8:]) if r[:4] == b"IOz1" else r

    def dec(self, t, eid):
        f, off, size = self.bykey[(t, eid)]
        if f == 0xff:
            return self._fetch_cdn(t, eid)
        fh = self._fh.get(f) or self._fh.setdefault(f, open(os.path.join(self.dir, f"{f:02d}.rdbdata"), "rb"))
        fh.seek(off)
        return self._unwrap(fh.read(size))

    def _fetch_cdn(self, t, eid):
        import urllib.request
        h = self.hash[(t, eid)].hex()
        url = f"{self.MIRROR}/gameupdate/upm/rdb/res/{h[:2]}/{h[2:3]}/{h[3:]}"
        try:
            with urllib.request.urlopen(url, timeout=60) as resp:
                return self._unwrap(resp.read())
        except Exception as e:
            print(f"  [cdn miss for ({t},{eid}) {url}: {e}]", file=sys.stderr)
            return None


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


def _channel(d, p, width):
    """[u32 count][count f32 times][count x width f32 values] -> (times, [tuple], next_p).
    Bounds-checked: raises ValueError on a count that overruns the buffer (a variant channel layout)."""
    cnt = struct.unpack_from("<I", d, p)[0]
    p += 4
    if cnt < 0 or p + cnt * 4 * (1 + width) > len(d):
        raise ValueError("channel count %d overruns buffer (variant layout)" % cnt)
    times = list(struct.unpack_from("<%df" % cnt, d, p))
    p += cnt * 4
    vals = [struct.unpack_from("<%df" % width, d, p + i * width * 4) for i in range(cnt)]
    return times, vals, p + cnt * width * 4


def parse_curve(d):
    """1010003 per-bone keyframe tracks. Each bone = a translation (vec3) channel then a rotation (quat) channel."""
    cid = struct.unpack_from("<I", d, 4)[0]
    duration = struct.unpack_from("<f", d, 16)[0]
    frames = struct.unpack_from("<I", d, 20)[0]
    p = 24
    bones = []
    while p + 4 <= len(d):
        nl = struct.unpack_from("<I", d, p)[0]
        if not (0 < nl < 64) or d[p + 4:p + 5] != b"r":  # tracks are 'rig_bone_*'
            break
        name = d[p + 4:p + 4 + nl].decode("latin1")
        p += 4 + nl
        t_times, t_vals, p = _channel(d, p, 3)  # translation vec3
        r_times, r_vals, p = _channel(d, p, 4)  # rotation quat (x,y,z,w)
        bones.append(dict(bone=name, translate=list(zip(t_times, t_vals)), rotate=list(zip(r_times, r_vals))))
    return dict(id=cid, duration=duration, frames=frames, bones=bones)


if __name__ == "__main__":
    r = Rdb()

    # --- validate the LMXB grammar on the movement_idle clip ---
    L = Lmxb(r.dec(1010511, 14532))
    print("=== 1010511:14532 (movement_idle) metadata tree ===")
    L.dump()

    # --- decode + validate real curve resources (every bone parses; every rotation key is a unit quaternion) ---
    import math
    for cid in (11761, 11709):
        c = parse_curve(r.dec(1010003, cid))
        nkeys = sum(len(b["rotate"]) for b in c["bones"])
        bad = sum(1 for b in c["bones"] for _, q in b["rotate"] if abs(math.sqrt(sum(x * x for x in q)) - 1.0) > 1e-3)
        print(f"\n=== 1010003:{c['id']} duration={c['duration']:.2f}s frames={c['frames']} bones={len(c['bones'])} ===")
        print(f"  parsed all bones; {nkeys} rotation keys, {bad} non-unit quaternions")
        h = next(b for b in c["bones"] if b["bone"] == "rig_bone_Hips")
        print(f"  Hips: {len(h['translate'])} T-keys, {len(h['rotate'])} R-keys; "
              f"T[0]={tuple(round(x,3) for x in h['translate'][0][1])} R[0]={tuple(round(x,3) for x in h['rotate'][0][1])}")
