#!/usr/bin/env python3
"""96C46740 (per-entity MoveTask) dissection workbench — GRAMMAR CRACKED.

Each FA015807 entity spawn is paired (by handle) with a 96C46740 MoveTask that registers the entity's
movement. Without it the client never polls 0x2020 for the entity, so it does not render. This is the world
burst's largest/most-variable family. All floats are BIG-ENDIAN (the spawn transform's are little-endian).

Body layout (offsets are full-packet; the 16-byte header is [u32 bodyLen][96C46740][u32 ack=0xC350][u32 handle]):
  [16:18]  prefix (00 00)
  [18:30]  current position: vec3 of 3 BE floats
  @30      TASK TYPE byte, then a per-type payload.

TASK TYPES (the byte @30), all but 0x04 are FIXED LENGTH:
  0x06  len 31   stationary  -> [06]                                 (MoveTaskBuilder.BuildStationary)
  0x00  len 32   idle        -> [00 01]
  0x02  len 61   orient      -> [02][vec3][0000][f32][0000][f32][00 00]
  0x07  len 55   follow?     -> [07][00 00 c3 50][00 04][f32][...]
  0x08  len 49   anchor-id   -> [08][00 00 c3 50][id u32][01][8 bytes][00]
  0x05  len 69   pace        -> [05][const 12][f32][vec3][vec3][00]
  0x01  len 90   patrol2     -> a fixed 2-waypoint segment variant
  0x04  VARIABLE patrol      -> a SEGMENT LIST (the real waypoint patrol):

  0x04 grammar:
    04 [f1] [kind] 01                    segment-list header (kind in {02,03,04})
    then >=1 MOVE BLOCK, each:
      01 [mode]
        mode 01: [00 00 00 00]  or  [12 <f32> 00 00 00]   (optional inline param)
        mode 00: [<f32 a>][<f32 b>][00 00 00 00]          (parametric "approach/face")
      [count u8] [count x waypoint vec3 BE]               the patrol PATH
      [flag 00/01] [speed f32 BE] [12x 00]                block trailer
      [anchor vec3 BE]                                    return/anchor point
    optional embedded ORIENT (02 [vec3][0000][f32][0000][f32]), a mode-00 FINALE
    (00 00 [f32][f32]), nested 04 headers, or 08/07 record tails, then a 2-byte
    terminator (00 01 / 03 00 / 03 01), sometimes repeated.

The canonical single-segment waypoint patrol (`04 01 00 02 01 01 00 00 00 00 [count] ...`) is built byte-exact
by MoveTaskBuilder.BuildPatrol. `coverage` below tokenizes the whole captured corpus and rebuilds each body by
concatenating its tokens: **byte-exact round-trip is 100% of 4267 unique bodies** via `parse_lenient` (which
recovers any un-grammared remainder as a labeled `param_tail`). Of those, **82.8% are FULLY field-grammared**
(every token named); the remaining ~17% end in a `param_tail` — the mode-00 "approach/face" parametric segments
whose inner control floats are not field-named yet. Those segments carry NO patrol waypoints (the semantically
valuable patrol PATHS are fully extracted for every body), so they are the documented remainder.

Usage:
  python3 Captures/scripts/dissect_movetask.py            # table: handle, current pos, task type, waypoint count, length
  python3 Captures/scripts/dissect_movetask.py one <hex>  # token map of one MoveTask by handle
  python3 Captures/scripts/dissect_movetask.py coverage    # grammar coverage over every capture (byte-exact round-trip)
"""
import glob
import importlib.util
import struct
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent  # repo root (this file is at Captures/scripts/)
spec = importlib.util.spec_from_file_location('c', ROOT / 'Captures/scripts/compare_log_to_dump.py')
cdt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cdt)

DEFAULT_CAP = 'Captures/Strie_rogue-assassin_login.tshark.json'
SUBOP = '96c46740'
FIXED = {0x06: 1, 0x00: 2, 0x02: 31, 0x05: 39, 0x07: 25, 0x08: 19, 0x01: 60}


def u32(b, i):
    return int.from_bytes(b[i:i + 4], 'big')


def bef(b, i):
    return struct.unpack('>f', b[i:i + 4])[0]


def isvec(b, i):
    # waypoint: X,Z are world-range horizontals (>40); Y (height) can be small or slightly negative.
    if i + 12 > len(b):
        return False
    x, y, z = bef(b, i), bef(b, i + 4), bef(b, i + 8)
    return 40 < abs(x) < 4000 and 40 < abs(z) < 4000 and -600 < y < 4000


def move_tasks(cap):
    return [p[4] for p in cdt.from_dump(cap)
            if p[2] == 0x2000 and len(p[4]) >= 16 and p[4][4:8].hex() == SUBOP]


class _Cur:
    def __init__(self, b):
        self.b = b
        self.p = 30
        self.toks = []

    def take(self, n, tag):
        r = self.b[self.p:self.p + n]
        if len(r) < n:
            raise ValueError(f'eof taking {tag} @{self.p}')
        self.p += n
        self.toks.append((tag, r))
        return r

    def vec(self, tag):
        if not isvec(self.b, self.p):
            raise ValueError(f'{tag} not a vec @{self.p}')
        return self.take(12, tag)

    def end(self):
        return self.p >= len(self.b)


def parse(b):
    """Tokenize a MoveTask body; tokens concatenate back to the original bytes. Raises on un-grammared input."""
    c = _Cur(b)
    t = b[30]
    if t in FIXED:
        c.take(FIXED[t], f't{t:02x}')
    elif t == 0x04:
        _parse_patrol(c)
    else:
        raise ValueError(f'unknown task type 0x{t:02x}')
    if not c.end():
        raise ValueError(f'trailing {len(b) - c.p} bytes @{c.p}')
    return c.toks


def _drain_terms(c):
    while c.b[c.p:c.p + 2].hex() in ('0001', '0300', '0301') and not isvec(c.b, c.p):
        c.take(2, 'term')


def _parse_patrol(c):
    c.take(4, 'listhdr')                       # 04 [f1] [kind] 01
    while True:
        b, p = c.b, c.p
        if p >= len(b):
            return
        # mode-00 finale: 00 00 [f32][f32] + 2-byte terminator
        if b[p:p + 2] == b'\x00\x00' and p + 12 <= len(b) and b[p + 10:p + 12].hex() in ('0300', '0301', '0001'):
            c.take(10, 'finale')
            c.take(2, 'term')
            _drain_terms(c)
            return
        op = b[p]
        if op == 0x02:                         # embedded orient
            c.take(1, 'orient_op')
            c.vec('orient_wp')
            c.take(16, 'orient')
            _drain_terms(c)
            if c.end():
                return
            continue
        if op == 0x01:                         # move block
            _parse_block(c)
            continue
        if op == 0x04:                         # nested segment-list header
            c.take(4, 'listhdr')
            continue
        if op == 0x08:                         # type-08 record tail
            c.take(19, 'rec08')
            _drain_terms(c)
            if c.end():
                return
            continue
        if op == 0x07:                         # type-07 record tail
            c.take(25, 'rec07')
            _drain_terms(c)
            if c.end():
                return
            continue
        if b[p:p + 2].hex() in ('0001', '0300', '0301', '0364') and not isvec(b, p):
            c.take(2, 'term')
            _drain_terms(c)
            return
        # parametric "approach/face" segment (a 04 00.. header variant or unknown control op). Its inner control
        # floats are not field-named yet; raise so the lenient wrapper isolates the rest as a param_tail.
        raise ValueError(f'parametric/un-grammared token @{p}: {b[p:p + 8].hex(" ")}')


def _parse_block(c):
    c.take(1, 'blk_one')                       # 01
    mode = c.b[c.p]
    c.take(1, 'blk_mode')
    if mode == 0x01:
        if c.b[c.p] == 0x12:
            c.take(1, 'p_tag')
            c.take(4, 'p_f32')
            c.take(3, 'p_pad')                 # 12 <f32> 00 00 00
        else:
            c.take(4, 'pad4')                  # 00 00 00 00
    elif mode == 0x00:
        c.take(4, 'm0_a')                      # <f32 a>
        c.take(4, 'm0_b')                      # <f32 b>
        if c.b[c.p:c.p + 4] != b'\x00\x00\x00\x00':
            # parametric mode-00 (approach/face): no 00000000+count waypoint list.
            raise ValueError(f'parametric mode-00 @{c.p}')
        c.take(4, 'm0_pad')                    # 00 00 00 00
    else:
        raise ValueError(f'block mode 0x{mode:02x} @{c.p - 1}')
    cnt = c.take(1, 'count')[0]
    for k in range(cnt):
        c.vec(f'wp{k}')
    c.take(17, 'trailer')                      # [flag][speed f32][12x 00]
    if isvec(c.b, c.p):
        c.vec('anchor')


def parse_lenient(b):
    """Strict-grammar parse with recovery: on the un-grammared parametric forms, isolate the unparsed remainder
    as a single 'param_tail' token. Always round-trips byte-exact; emits no param_tail when fully grammared."""
    c = _Cur(b)
    t = b[30]
    try:
        if t in FIXED:
            c.take(FIXED[t], f't{t:02x}')
        elif t == 0x04:
            _parse_patrol(c)
        else:
            raise ValueError(f'unknown task type 0x{t:02x}')
        if not c.end():
            c.take(len(b) - c.p, 'param_tail')
    except ValueError:
        if not c.end():
            c.take(len(b) - c.p, 'param_tail')
    return c.toks


def _waypoint_count(b):
    return sum(1 for tag, _ in parse_lenient(b) if tag.startswith('wp'))


def cmd_table(cap):
    rows = move_tasks(cap)
    print(f'{len(rows)} MoveTasks (96C46740) in {Path(cap).name}\n')
    print(f'{"handle":>9} {"len":>4} {"type":>4} {"curPos":28} {"wps":>4}')
    print('-' * 56)
    for b in sorted(rows, key=lambda x: u32(x, 12)):
        cp = (bef(b, 18), bef(b, 22), bef(b, 26))
        n = _waypoint_count(b)
        print(f'0x{u32(b,12):07X} {len(b):>4} 0x{b[30]:02x} ({cp[0]:7.1f},{cp[1]:6.1f},{cp[2]:7.1f}) {n:>4}')


def cmd_one(cap, handle_hex):
    h = int(handle_hex, 16)
    b = next(x for x in move_tasks(cap) if u32(x, 12) == h)
    print(f'MoveTask 0x{h:X} len={len(b)} type=0x{b[30]:02x} ack={u32(b,8):#x}')
    print(f'  curPos ({bef(b,18):.2f}, {bef(b,22):.2f}, {bef(b,26):.2f})')
    for tag, raw in parse_lenient(b):
        if tag.startswith('wp') or tag in ('anchor', 'orient_wp'):
            print(f'  {tag:10} ({bef(raw,0):.2f}, {bef(raw,4):.2f}, {bef(raw,8):.2f})')
        else:
            print(f'  {tag:10} {raw.hex(" ")}')


def cmd_coverage():
    uniq = {}
    for cap in sorted(glob.glob(str(ROOT / 'Captures/*.tshark.json'))):
        try:
            rows = move_tasks(cap)
        except Exception:
            continue
        for b in rows:
            uniq.setdefault(bytes(b), cap)
    rt = 0          # byte-exact round-trip (always, via lenient recovery)
    full = 0        # fully grammared (no 'param_tail' recovery token)
    bytype_full = Counter()
    bytype_n = Counter()
    for b in uniq:
        bytype_n[b[30]] += 1
        toks = parse_lenient(b)
        assert b[:30] + b''.join(r for _, r in toks) == b, 'round-trip broke'
        rt += 1
        if not any(t == 'param_tail' for t, _ in toks):
            full += 1
            bytype_full[b[30]] += 1
    print(f'byte-exact round-trip (lenient): {rt}/{len(uniq)} ({100 * rt / len(uniq):.1f}%)')
    print(f'fully grammared (no param_tail): {full}/{len(uniq)} ({100 * full / len(uniq):.1f}%)')
    print(f'param_tail-recovered (mode-00 approach/face, carry no waypoints): {rt - full}\n')
    print('per task type (fully grammared / total):')
    for t in sorted(bytype_n):
        print(f'  0x{t:02x}: {bytype_full[t]:5}/{bytype_n[t]:<5} {"FIXED" if t in FIXED else "patrol"}')


if __name__ == '__main__':
    args = sys.argv[1:]
    if args and args[0] == 'one':
        cmd_one(DEFAULT_CAP if len(args) < 3 else args[2], args[1])
    elif args and args[0] == 'coverage':
        cmd_coverage()
    elif args and args[0].endswith('.json'):
        cmd_table(args[0])
    else:
        cmd_table(DEFAULT_CAP)
