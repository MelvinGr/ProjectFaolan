#!/usr/bin/env python3
"""Probe the List B region of the 0x737A6DF9 FullChar snapshot TAILS (#258).

Per the client reader (docs/re/fullplayer-snapshot-reader.md top-level grammar), after List A (Quest_c journal)
comes List B (driver FUN_00798c70 -> per-record FUN_00922c90). The static order says List B is next after
List A (@609 in the beach template) but the doc flags that @609 "fits none" of the statically-next readers, so
this script tests the List B grammar directly against the bytes and reports where it lands.

List B framing (FUN_00798c70): 0x3F1-framed -> count c, records = c/0x3F1 - 1.
List B per-record (FUN_00922c90):
    u32  field0
    string s1 ; string s2 ; string s3          # [u16 len][bytes]
    u32  hash            (ACGHash, FUN_005d7190 = 1x ReadU32)
    u32  field_0x6c
    u32  innerCount
    inner x innerCount:
        u32 a ; u32 b
        nested 0x3F1 string-list (FUN_004d55b0): u32 c2 (c2%0x3F1==0), then c2/0x3F1 - 1 ReadStrings

Run:  python3 tools/capture/decode_listb.py [tails.bin] [start_offset]
      (default: the beach template @609)
"""
import struct, sys, re

from fixtures import named

# The beach tails template lives in the fixture archive (Faolan.Tests/Assets/fixtures.bin), not as a loose file.
TEMPLATE = "assets/BeachTailsTemplate.bin"
FRAME = 0x3F1


class R:
    def __init__(self, b, o=0): self.b, self.o = b, o
    def u16(self): v = struct.unpack_from(">H", self.b, self.o)[0]; self.o += 2; return v
    def u32(self): v = struct.unpack_from(">I", self.b, self.o)[0]; self.o += 4; return v
    def peek(self): return struct.unpack_from(">I", self.b, self.o)[0]
    def s(self):
        n = self.u16()
        if self.o + n > len(self.b):
            raise ValueError(f"string len {n} overruns @{self.o}")
        v = self.b[self.o:self.o + n]; self.o += n
        return v


def looks_str(b):
    """Heuristic: does this look like a [u16 len][printable bytes] string?"""
    if len(b) == 0:
        return False
    try:
        return all(9 <= c < 127 for c in b)
    except Exception:
        return False


def nested_strlist(r):
    c2 = r.u32()
    if c2 % FRAME != 0 or c2 == 0:
        return None, c2  # not a frame -> the reader sets the fail flag, reads nothing
    n = c2 // FRAME - 1
    out = [r.s() for _ in range(n)]
    return out, c2


def read_record(r, idx):
    start = r.o
    field0 = r.u32()
    strs = []
    for k in range(3):
        so = r.o
        try:
            v = r.s()
        except ValueError as e:
            print(f"    rec[{idx}] s{k}: {e}")
            raise
        strs.append(v)
        if not looks_str(v):
            print(f"    rec[{idx}] s{k} @{so} NOT-printable ({len(v)}B): {v[:16].hex()}  <-- grammar likely wrong here")
    hash_ = r.u32()
    f6c = r.u32()
    inner = r.u32()
    print(f"    rec[{idx}] @{start}: field0={field0:#x} hash={hash_:#x} f6c={f6c:#x} innerCount={inner}")
    for s in strs:
        t = s.decode("latin1", "replace")
        cat = re.search(r'category="(\d+)"', t); idm = re.search(r'id="(\d+)"', t)
        tag = f"id={idm.group(1)} cat={cat.group(1)}" if (idm or cat) else repr(t[:40])
        print(f"        str[{len(s):3}] {tag}")
    if inner > 1000:
        raise ValueError(f"innerCount {inner} implausible @{r.o-4}")
    for j in range(inner):
        a = r.u32(); b = r.u32()
        lst, c2 = nested_strlist(r)
        print(f"        inner[{j}] a={a:#x} b={b:#x} nested_count={c2:#x} -> {None if lst is None else len(lst)} strings")
    return r.o


def decode(path, start):
    # A path argument reads that file; the default is the archived template, by key.
    b = open(path, "rb").read() if path != TEMPLATE else named(TEMPLATE)
    print(f"\n=== {path}  ({len(b)} bytes)  List B probe @ {start} ===")
    r = R(b, start)
    c = r.u32()
    print(f"  List B framing count @ {start} = {c:#x}", end="")
    if c % FRAME != 0 or c == 0:
        print(f"  -> NOT 0x3F1-framed (records would be 0). List B is EMPTY/absent here.")
        print(f"  next word stays @{start+4} = {r.peek():#x}")
        return
    nrec = c // FRAME - 1
    print(f"  -> {nrec} record(s)")
    for i in range(nrec):
        try:
            end = read_record(r, i)
        except (ValueError, struct.error) as e:
            print(f"  !! record {i} failed: {e}")
            print(f"  -> List B grammar does NOT fit at @{start}")
            return
    print(f"  >> List B consumed to @{r.o}; next word = {r.peek():#x}")


if __name__ == "__main__":
    path = sys.argv[1] if len(sys.argv) > 1 else TEMPLATE
    start = int(sys.argv[2]) if len(sys.argv) > 2 else 609
    decode(path, start)
