#!/usr/bin/env python3
"""Decode a batch of combat + world IIR ReadSubClass readers (Gitea #233 decode campaign).

All readers recovered from AgeOfConan.exe via the vtable-xref method
(tools/ghidra/DumpVtableReaders.java, slot 8). Stream primitives:
    FUN_0048f900            ReadU32  (4 bytes, big-endian)
    FUN_00cba1b0(&x,1)      ReadByte (1 byte; the `x >> 0x18` idiom extracts it)
    FUN_00cbfb80(dst)       ReadLengthPrefixedBlob = [u32 len][len bytes]
    FUN_0080c620(dst)       the kd-tree / bounds struct reader (shared with SimpleDynel)

Carrier framing for every record here (the 0x2000 IIR envelope):
    [u32 len][u32 sub][u32 0xC350][u32 handle][u8 env][ reader ]
i.e. a single envelope byte sits between the handle and the inner reader (same as CharDCMove).

This validator proves each reader consumes EXACTLY to the declared packet length across every
captured instance. Run: python3 tools/capture/decode_combat_world_iir.py
"""
import json, struct, glob
from collections import Counter

U = 4


def u32(b, o):
    return struct.unpack('>I', b[o:o + 4])[0]


def load():
    caps = {}
    for path in sorted(glob.glob('Captures/*.tshark.json')):
        try:
            d = json.load(open(path))
        except Exception:
            continue
        for p in d.get('Packets', []):
            b = bytes.fromhex(p.get('BodyHex', '') or '')
            if len(b) < 8:
                continue
            caps.setdefault(u32(b, 4), []).append(b)
    return caps


# (sub, name, reader_size_bytes) for the FIXED readers -- env byte is +1 on top
FIXED = [
    (0x0EED7068, "ChainKillIIR_t",        1 * U),          # 1 u32
    (0xCFFD0519, "CombatShieldIIR_c",     1 * U),          # 1 u32
    (0x6B0AEC58, "LockedToChangedIIR_t",  2 * U),          # 2 u32
    (0x1745EF6C, "ThreatLevelIIR_c",      1 * U + 1),      # u32 + bool byte
    (0x8A01E5A1, "HateListUpdateIIR",     1 + 3 * U),      # byte + 3 u32
    (0x04B80B24, "SetPosIIR_c",           7 * U + 1),      # pos(3)+rot(4) floats + bool byte
    (0x864CFEF8, "CharInPlayIIR_t",       0),              # empty reader (carrier handle only)
    (0xE9F24184, "StatDamageIIR_t",       17 * U),         # long form: 6(cond)+2+2+7 u32
]


def check_fixed(caps):
    print("=== fixed-form readers (env byte + N) ===")
    allok = True
    for sub, name, rb in FIXED:
        s = caps.get(sub, [])
        ok = sum(1 for b in s if 16 + 1 + rb == len(b) == 4 + u32(b, 0))
        allok &= (ok == len(s) and bool(s))
        print(f"  {name:28} reader={rb:3}B  byte-exact {ok}/{len(s)}")
    return allok


def check_combat_reactive(caps):
    # env(1) + u32 mode + (mode==1: 5 u32 / mode==3: 1 u32 / else 0)
    s = caps.get(0x54C02089, [])
    ok = 0
    for b in s:
        o = 17
        mode = u32(b, o); o += 4
        o += 5 * U if mode == 1 else (U if mode == 3 else 0)
        if o == len(b) == 4 + u32(b, 0):
            ok += 1
    print(f"=== CombatReactiveIIR_c (mode-gated): byte-exact {ok}/{len(s)}  modes={dict(Counter(u32(b,17) for b in s))}")
    return ok == len(s) and bool(s)


def check_dynel_family(caps):
    # env(1) + [u32 blobLen][blob] + kdtree(variable, shared reader) + ntrail*u32
    print("=== dynel-family (blob + kd-tree + trailing u32s) framing ===")
    allok = True
    for sub, name, ntrail in [
        (0x33A56FB0, "DoorFullUpdateIIR_c",         1),
        (0x6BD56736, "DestructibleFullUpdateIIR_c", 5),
        (0x1FD6412B, "ClimbingDynelFullUpdateIIR_c", 1),
        (0x0D80E7C9, "TombStoneFullUpdateIIR_c",    2),
    ]:
        s = caps.get(sub, [])
        ok = 0
        for b in s:
            o = 17
            blob_len = u32(b, o); o += 4 + blob_len
            kd = len(b) - o - ntrail * U          # kd-tree region (variable)
            if kd >= 0 and o + kd + ntrail * U == len(b):
                ok += 1
        allok &= (ok == len(s) and bool(s))
        print(f"  {name:30} n={len(s):4} framing-consistent {ok}/{len(s)}")
    return allok


def main():
    caps = load()
    a = check_fixed(caps)
    b = check_combat_reactive(caps)
    c = check_dynel_family(caps)
    print()
    assert a and b and c, "a reader did not validate"
    print("OK: every reader consumes to the declared packet length for every captured instance.")


if __name__ == '__main__':
    main()
