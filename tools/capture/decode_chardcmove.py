#!/usr/bin/env python3
"""Decode CharDCMoveIIR_t (0x4517AA81) - the per-tick dead-reckoning movement update.

This is the single highest-volume GameServer world record (13289 instances across 11
captures): every moving entity streams its position + facing + velocity each tick. Decode
recovered from AgeOfConan.exe via the vtable-xref method (tools/ghidra/DumpVtableReaders.java,
slot 8 -> CharDCMoveIIR_t::ReadSubClass = FUN_007adae0 -> inner reader FUN_007ad6b0).

Carrier framing (the 0x2000 IIR envelope):

    [u32 len][u32 0x4517AA81][u32 0xC350][u32 handle][u8 env][ inner reader ]

The inner reader (FUN_007ad6b0) is a FLAG-GATED dead-reckoning record. A leading bitfield
byte (flag1) selects which optional 3-float vectors are present; a second mode byte (flag2,
only meaningful when < 8) gates the velocity/acceleration/heading block:

    [u8 flag1]                      bitfield: see below
    [float x3]  position            ALWAYS (world x, y=height, z)
    bit4 set  -> [float facingX][..]   collapsed: 1 float (the other axis is param6 below)
    bit4 clear-> [float x3]            full move vector
    [float]     param6              facing/secondary scalar (with the bit4 float = normalized 2D facing)
    [u8 flag2]                      move mode; the block below is read only when flag2 < 8:
        bit2 clear-> [float x3]     velocity      (param10..12)
        bit3 clear-> [float x3]     acceleration  (param13..15)
        [float]     param16         speed/scalar
        bit1 clear-> [float x3]     heading vector (param7..9)
        [float]     param19         scalar
        [u8][u8]    two bool flags

flag1 bit map (from the decompile; uVar5 = flag1):
    bit1 (0x02)  local_d : gates the param7..9 heading vector
    bit2 (0x04)  local_f : gates the param10..12 velocity vector
    bit3 (0x08)  local_e : gates the param13..15 acceleration vector
    bit4 (0x10)          : collapses the post-position move vector to a single float
    bit5 (0x20)          : stored as bool param[0x12]
    bit6 (0x40)          : stored as bool

VALIDATED: the grammar consumes EXACTLY to the declared packet length for all 13289 samples.
Position floats track real movement (a walking char's x/z drift smoothly tick-to-tick) and the
bit4 collapsed pair is a unit-length facing direction (facingX^2 + param6^2 == 1.0).

Run: python3 tools/capture/decode_chardcmove.py
"""
import json, struct, glob
from collections import Counter

SUB = 0x4517AA81


def parse(b):
    """Walk CharDCMoveIIR_t per FUN_007ad6b0. Returns (end_offset, flag1, flag2)."""
    o = 16
    _env = b[o]; o += 1                  # envelope byte (0x00/0x01) between handle and reader
    flag1 = b[o]; o += 1
    bit1 = (flag1 >> 1) & 1
    bit2 = (flag1 >> 2) & 1
    bit3 = (flag1 >> 3) & 1
    bit4 = (flag1 >> 4) & 1
    o += 12                               # position x,y,z (always)
    o += 4 if bit4 else 12                # collapsed facing float, or full 3-float vector
    o += 4                                # param6 scalar
    flag2 = b[o]; o += 1
    if flag2 < 8:
        o += 0 if bit2 else 12            # velocity     (param10..12)
        o += 0 if bit3 else 12            # acceleration (param13..15)
        o += 4                            # param16 scalar
        o += 0 if bit1 else 12            # heading      (param7..9)
        o += 4                            # param19 scalar
        o += 2                            # two bool bytes
    return o, flag1, flag2


def f(b, o):
    return struct.unpack('>f', b[o:o + 4])[0]


def main():
    samples = []
    for path in sorted(glob.glob('Captures/*.tshark.json')):
        try:
            d = json.load(open(path))
        except Exception:
            continue
        for p in d.get('Packets', []):
            b = bytes.fromhex(p.get('BodyHex', '') or '')
            if len(b) < 8 or struct.unpack('>I', b[4:8])[0] != SUB:
                continue
            samples.append(b)

    ok = bad = 0
    flagcombos = Counter()
    facing_unit = 0
    for b in samples:
        end_expected = 4 + struct.unpack('>I', b[:4])[0]
        try:
            end, flag1, flag2 = parse(b)
        except Exception:
            bad += 1
            continue
        if end == end_expected == len(b):
            ok += 1
        else:
            bad += 1
        flagcombos[(flag1 & ~1, flag2 if flag2 < 8 else 'F2>=8')] += 1
        if (flag1 >> 4) & 1:              # collapsed facing -> should be unit length
            fx, fz = f(b, 30), f(b, 34)
            if abs(fx * fx + fz * fz - 1.0) < 0.02:
                facing_unit += 1

    print(f"CharDCMoveIIR_t (0x{SUB:08X}): {len(samples)} samples")
    print(f"  grammar consumes to packet end: {ok}/{len(samples)}  (bad={bad})")
    print(f"  collapsed-facing records that are unit-length: {facing_unit}")
    print("\n  flag1(base)/flag2 distribution:")
    for k, c in flagcombos.most_common(10):
        print(f"    flag1&~1=0x{k[0]:02x} flag2={k[1]}: {c}")
    assert bad == 0, "grammar did not consume to packet end for every sample"
    print("\nOK: byte-exact for every sample.")


if __name__ == '__main__':
    main()
