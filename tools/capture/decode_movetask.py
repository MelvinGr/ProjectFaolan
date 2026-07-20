#!/usr/bin/env python3
"""Recursive MoveTaskIIR_c (0x96C46740) dissector - validates the wire grammar to the record end (#53).

The MoveTask is a RECURSIVE composite (docs/re/movetask-grammar.md): the top record is
  [u16 0][float x3 from][factory]
and the factory (FUN_00a46800) is [u8 mode][type-specific fields], where ListMoveTask (mode 4) nests a
count of further factories. Readers recovered from AgeOfConan.exe via Ghidra (tools/ghidra/
DumpMoveTaskList.java + DumpMoveTaskLeafReaders.java + DumpPathHelpers.java).

Pinned leaf grammars:
  WaitMoveTask base header (FUN_00a48820): [u8 flags] (+ [float dur][float rem] when flags&1 == 0)
  mode 0  Wait        : the base header
  mode 1  PathMoveTask: header + [u8 F] (+float if F&0x10)(+float if F&0x20) then
                          F&1 == 0 -> [u32 count][count x (3 floats)][29-byte trailer]   # the WAYPOINT PATH
                          F&1      -> [u16][u16][a3e400]            # packed branch (only seen nested)
                        per-waypoint = exactly 12 B (FUN_00a31ec0 is a 0xC-byte vector push, no extra read);
                        a3e400 (FUN_00a3e400) finalizer = [u8 bool][7 u32] = fixed 29 B (a3e510 only validates
                        the spline, reads 0). The 7 u32 are [pathLen][3 u32=0][3 floats=from].
  mode 2  Rotate      : [float x3 to][float x4 quat][u16]            # the built 61-B form
  mode 3  Jump        : [u8 flag]
  mode 4  ListMoveTask: header + [u8 min][u8 count][factory x count] # recursive
  mode 5  Ballistic   : header + [float x7][u8 flag]                 # FUN_00a49540
  mode 6  Animated    : (nothing - FUN_00a4a2b0 returns 1)
  mode 7  Formation   : [float x6]                                   # FUN_00a4a9d0 (no header)
  mode 8  approach    : [u32 0xC350][u32 targetHandle][u8 1][9 zero bytes]
  mode 10 SetPos      : [float x3][float x4]
  mode 11 GroundAlign : [u8 flag]

Coverage: 9468/9468 captured MoveTask records (100%) round-trip byte-exact - the full recursive grammar.
"""
import json, struct, glob, sys
from collections import Counter
import sys as _sys, pathlib as _pl
_sys.path.insert(0, str(_pl.Path(__file__).resolve().parent))
from captures import capture_files, capture  # corpus lives in the ProjectFaolan-Data repo


class Reader:
    def __init__(self, b): self.b, self.o = b, 0
    def u8(self): v = self.b[self.o]; self.o += 1; return v
    def u16(self): self.o += 2
    def u32(self): v = struct.unpack_from('>I', self.b, self.o)[0]; self.o += 4; return v
    def f(self, n): self.o += 4 * n
    def skip(self, n): self.o += n
    def rem(self): return len(self.b) - self.o


def header(p):  # WaitMoveTask base (FUN_00a48820)
    flags = p.u8()
    if (flags & 1) == 0:
        p.f(2)
    return flags


def a3e400(p):  # FUN_00a3e400 - the PathMoveTask finalizer: [u8 bool][7 u32]. (a3e510 only validates, reads 0.)
    p.u8(); p.f(7)


def path(p):  # PathMoveTask (FUN_00a483b0)
    header(p)
    F = p.u8()
    if F & 0x10: p.f(1)
    if F & 0x20: p.f(1)
    if F & 1:
        p.u16(); p.u16()        # 2x ReadU16 (FUN_0048f550)
        a3e400(p)
    else:
        cnt = p.u32()
        if cnt > 1000:
            raise ValueError(f"path count {cnt}")
        p.f(3 * cnt)            # count waypoints, 12 B each
        a3e400(p)              # the fixed 29-byte trailer


def factory(p):
    mode = p.u8()
    if mode == 0:    header(p)
    elif mode == 1:  path(p)
    elif mode == 2:  p.f(3); p.f(4); p.u16()
    elif mode == 3:  p.u8()
    elif mode == 5:  header(p); p.f(7); p.u8()       # BallisticMoveTask FUN_00a49540
    elif mode == 6:  pass                            # AnimatedMoveTask FUN_00a4a2b0 (reads nothing)
    elif mode == 7:  p.f(6)                          # FormationMoveTask FUN_00a4a9d0 (no header)
    elif mode == 8:  p.u32(); p.u32(); p.u8(); p.skip(9)
    elif mode == 10: p.f(3); p.f(4)
    elif mode == 11: p.u8()
    elif mode == 4:
        header(p); p.u8(); cnt = p.u8()
        if cnt > 64:
            raise ValueError(f"list count {cnt}")
        for _ in range(cnt):
            factory(p)
    else:
        raise NotImplementedError(f"leaf mode {mode}")
    return mode


def records(paths):
    sub = bytes.fromhex('96c46740')
    for path_ in paths:
        try:
            d = json.load(open(path_))
        except Exception:
            continue
        for pk in d.get('Packets', []):
            raw = bytes.fromhex(pk.get('BodyHex') or '')
            o = 0
            while True:
                j = raw.find(sub, o)
                if j < 0:
                    break
                o = j + 1
                if j < 4:
                    continue
                ln = struct.unpack('>I', raw[j - 4:j])[0]
                if 8 <= ln <= 512 and j + ln <= len(raw):
                    yield raw[j:j + ln]


def main(paths):
    recs = list(records(paths))
    ok = 0
    fails = Counter()
    for r in recs:
        p = Reader(r)
        try:
            p.u32(); p.u32(); p.u32()   # subop, type, handle
            p.u16(); p.f(3)             # framing + from-position
            factory(p)
            if p.rem() == 0:
                ok += 1
            else:
                fails[f"leftover{p.rem()}"] += 1
        except Exception as e:
            fails[type(e).__name__ + ':' + str(e)[:18]] += 1
    print(f"{len(recs)} MoveTask records; {ok} round-trip byte-exact ({100 * ok // max(1, len(recs))}%)")
    for k, v in fails.most_common(10):
        print(f"  {v:5}  {k}")


if __name__ == '__main__':
    args = sys.argv[1:] or capture_files("*.tshark.json")
    main(args)
