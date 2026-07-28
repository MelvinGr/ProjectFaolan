#!/usr/bin/env python3
"""Decode the client RDB spell-data records (RDB type 1040005) into their [fieldId -> value] tables.

The base spell record is a 12-byte RDB-Id header ([type LE][id LE][subtype LE]) followed by IIR sub-blocks
of [u32 fieldId][u32 value] LE pairs. RdbSpellImporter already lifts a few fields by the SAME parse used
here (the LONGEST run of strictly-INCREASING fieldId in (0,2000]); this script exposes the whole table so
new stats can be located (#78 level/cooldown work).

KNOWN field ids (validated):
  0x21  (33)  name string (separate framing; not in the numeric table - see RdbItemImporter.ExtractName)
  0x126 (294) cast time, centiseconds (value*10 = ms): instant=0, Ice Strike 200 -> 2.0s
  0x11F (287) target range * 100 (2500 -> 25 m)
  0x15C (348) spell damage rating * 100

STILL UNRESOLVED (this script is the tool for finding them):
  - LEVEL requirement: NOT in this record. The canonically-named/learnable spells (ids ~0x30xxxxx / 0x1xxxxx,
    matched by name) have no field equal to the real level (proven vs ellatha ground truth: Smite r1=L1,
    Hellfire Stream r1=L2, Divine Vigor r1=L3, Emanation of Life r1=L5, Rebuke r1=L7 - no field matches all).
    A separate id-range (~0x23xxx, e.g. Smite 144385) DOES carry a 0x36 rank-level series (1/20/35/50/65/80)
    but those records don't match the learnable spell names. Level is class-progression/trainer data, not the
    spell record -> source it from ellatha (levels only; its mana/cast are wrong, e.g. Chill r5 mana 107 vs
    in-game 417) or retail screenshots.
  - COOLDOWN/RECAST: exists as a spell stat (retail screenshot: Chill r5 = Recast 4s, instant cast) but the
    field isn't pinned yet - needs >=2-3 spells with DIFFERENT readable recasts to triangulate (one point is
    ambiguous; the record has multiple sub-blocks the longest-run parse doesn't fully cover).

Usage:
  decode_spell_fields.py <name|id> [more...]   # dump the field table for spells (name from faolan.db, or RDB id)
  decode_spell_fields.py --grep <substr>       # dump every faolan.db spell whose name contains <substr>
  decode_spell_fields.py --check               # cross-check a field vs ellatha-known levels (edit GT below)
Options: --rdb <dir> (default: $AOC_RDB), --db <faolan.db>
"""
import struct, sqlite3, sys, os

DEFAULT_RDB = os.environ.get("AOC_RDB", "")
SPELL_TYPE = 1040005


def load_index(rdb_dir):
    b = open(os.path.join(rdb_dir, "le.idx"), "rb").read()
    if b[:4] != b"IBDR":
        raise SystemExit("le.idx magic mismatch")
    n = struct.unpack_from("<I", b, 24)[0]
    stride = 44 if struct.unpack_from("<I", b, 4)[0] == 5 else 28
    idx_off, det_off = 28, 28 + n * 8
    ent = {}
    for i in range(n):
        t, rid = struct.unpack_from("<II", b, idx_off + i * 8)
        if t == SPELL_TYPE:
            d = det_off + i * stride
            ent[rid] = (b[d] & 0xFF, struct.unpack_from("<I", b, d + 4)[0], struct.unpack_from("<I", b, d + 8)[0])
    return ent


class Rdb:
    """Random access into the RDB data files.

    ⚠️ Reads the (offset, size) range and NOTHING else. This used to cache each touched `.rdbdata` file WHOLE -
    `self._files[f] = open(...).read()` - which is a 1 GB allocation per file, never evicted, to serve lookups
    that want a few hundred bytes. There are 28 of them, 28 GB in total, so a scan spanning the catalog OOMs a
    32 GB box and no amount of RAM fixes it. Keep the file HANDLE, not the contents.
    """

    def __init__(self, rdb_dir):
        self.dir = rdb_dir
        self.ent = load_index(rdb_dir)
        self._handles = {}

    def _handle(self, f):
        h = self._handles.get(f)
        if h is None:
            h = self._handles[f] = open(os.path.join(self.dir, f"{f:02d}.rdbdata"), "rb")
        return h

    def body(self, rid):
        if rid not in self.ent:
            return None
        f, off, size = self.ent[rid]
        if f == 0xFF:  # CDN-hosted, not local
            return None
        h = self._handle(f)
        h.seek(off)
        return h.read(size)

    def close(self):
        for h in self._handles.values():
            h.close()
        self._handles.clear()

    def fields(self, rid):
        """The longest strictly-increasing [fieldId][value] run (fieldId in (0,2000]) - mirrors RdbSpellImporter."""
        d = self.body(rid)
        if not d:
            return {}
        body = d[12:]
        words = min(len(body) // 4, 250)
        best = {}
        for start in range(words - 1):
            last, have, cur, j = 0, False, {}, start
            while j + 1 < words:
                fid = struct.unpack_from("<I", body, j * 4)[0]
                if fid == 0 or fid > 2000 or (have and fid <= last):
                    break
                cur[fid] = struct.unpack_from("<I", body, (j + 1) * 4)[0]
                last, have, j = fid, True, j + 2
            if len(cur) > len(best):
                best = cur
        return best


def resolve_ids(arg, con):
    """A bare int -> that RDB id; otherwise a faolan.db spell-name lookup (exact, then LIKE)."""
    if arg.isdigit():
        return [(int(arg), f"id {arg}")]
    rows = con.execute("SELECT Id, Name FROM Spells WHERE Name = ?", (arg,)).fetchall()
    if not rows:
        rows = con.execute("SELECT Id, Name FROM Spells WHERE Name LIKE ?", (f"%{arg}%",)).fetchall()
    return [(r[0], r[1]) for r in rows]


def dump(rdb, con, ids):
    recs = [(rid, label, rdb.fields(rid)) for rid, label in ids]
    keys = sorted(set().union(*[set(f) for _, _, f in recs])) if recs else []
    print("spell".ljust(32), "  ".join(f"0x{k:X}" for k in keys))
    for rid, label, f in recs:
        cast = f.get(0x126, 0) * 10
        print(f"{label[:30]:32s}", "  ".join(f"{f.get(k, '-'):>6}" for k in keys), f"   (cast {cast}ms)")


def main():
    args = sys.argv[1:]
    rdb_dir = DEFAULT_RDB
    if "--rdb" in args:
        i = args.index("--rdb"); rdb_dir = args[i + 1]; del args[i:i + 2]
    db = "faolan.db"
    if "--db" in args:
        i = args.index("--db"); db = args[i + 1]; del args[i:i + 2]

    rdb = Rdb(rdb_dir)
    con = sqlite3.connect(db)
    print(f"RDB type {SPELL_TYPE}: {len(rdb.ent)} records in {rdb_dir}")

    if args and args[0] == "--grep":
        ids = [(r[0], r[1]) for r in con.execute("SELECT Id, Name FROM Spells WHERE Name LIKE ? ORDER BY Name", (f"%{args[1]}%",))]
        dump(rdb, con, ids)
        return

    if not args:
        print(__doc__)
        return

    ids = []
    for a in args:
        ids += resolve_ids(a, con)
    if not ids:
        print("no matching spells")
        return
    dump(rdb, con, ids)


if __name__ == "__main__":
    main()
