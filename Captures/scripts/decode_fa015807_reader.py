#!/usr/bin/env python3
"""Validate the recovered SimpleCharFullUpdateIIR_t::ReadSubClass grammar (the FA015807 NPC-spawn
pre-IIR scaffold) against captured spawn records (Gitea #185 / #54).

The reader was decompiled from AgeOfConan.exe (Ghidra, FUN_01121c00 + helpers; see
docs/fa015807-spawn-reader.md). Read primitives:
  FUN_0048f900 = ReadU32 (4 bytes, byte-swapped to BE)   -> U32
  FUN_004e0390 = ReadByte (1 byte)                        -> U8
  FUN_01122b10 = a count-framed sub-list: first U32 = (count+1)*0x3F1, then count*{U32 + sub-object}
  FUN_0145ac80 = embedded CoreCharacterFullUpdateIIR::ReadSubClass (its own u8 version='A'=0x41)

Pre-IIR scaffold read sequence (after the leading u8 version=0x05):
  U8 · 17*U32 · 3*U8 · 4*U32 · 2*U8 · 5*U32 · SUBLIST(0x3F1) · 27*U32 · CoreChar(0x41 ...)

This script reads each FA015807 record out of a capture, walks the FIXED part of that sequence, and
asserts: leading version byte == 0x05, the sub-list's first u32 is a clean multiple of 0x3F1 (the
framing invariant), and the embedded CoreCharacter version byte == 0x41 lands exactly where the grammar
predicts. If all three hold across every record, the read sequence is correct.

Run:  python3 Captures/scripts/decode_fa015807_reader.py [capture.tshark.json ...]
"""
import json, struct, sys, glob

FA = 0xFA015807
CARRIER_SENDER_PREFIXES = None  # FA015807 records appear on multiple class pairs (world-entry + editor); key on iirType


def fa_records(pkts):
    for p in pkts:
        if not (p.get('IsDownload') and p.get('Opcode') == '0x2000'):
            continue
        b = bytes.fromhex(p['BodyHex'])
        if len(b) < 8:
            continue
        ln, it = struct.unpack('>II', b[0:8])
        if ln == len(b) - 4 and it == FA:
            # body after [u32 len][u32 iirType]; the record itself is [u32 type][u32 handle][u32 0] + scaffold.
            yield b[8:]


def walk_scaffold(rec):
    """Walk the fixed pre-IIR read sequence. rec starts at [u32 type][u32 handle][u32 0] then the
    reader's content. Returns dict of checkpoints, or raises on a framing violation."""
    o = 0
    typ, hnd, zero = struct.unpack('>III', rec[o:o + 12]); o += 12

    # The base IIR Read consumes the embedded ProtoGameCodeDynelIIR transform (pos/rot/playfield) FIRST:
    # [varint len][protobuf submsg]. ReadSubClass (the decompiled FUN_01121c00) starts AFTER it.
    tlen = 0; shift = 0
    while True:
        x = rec[o]; o += 1
        tlen |= (x & 0x7f) << shift
        if not (x & 0x80):
            break
        shift += 7
    o += tlen  # skip the transform body

    # ReadSubClass begins with the 1-byte class version (must be 0x05).
    version = rec[o]; o += 1
    if version != 0x05:
        raise ValueError(f'version byte 0x{version:02x} != 0x05 at offset {o-1}')

    # U8 · 17*U32 · 3*U8 · 4*U32 · 2*U8 · 5*U32
    for n, sz in ((1, 1), (17, 4), (3, 1), (4, 4), (2, 1), (5, 4)):
        o += n * sz

    # SUBLIST: first U32 = (count+1) * 0x3F1
    framed = struct.unpack('>I', rec[o:o + 4])[0]; o += 4
    if framed % 0x3F1 != 0:
        raise ValueError(f'sublist frame {framed} not a multiple of 0x3F1 at offset {o-4}')
    count = framed // 0x3F1 - 1
    # each entry: U32 + a sub-object. The sub-object size isn't fixed-decoded here; on the captured
    # spawns count == 0 (no per-spawn sublist entries), so the sublist is just its 4-byte frame.
    if count != 0:
        return dict(type=typ, handle=hnd, version=version, sublist_count=count, corechar_offset=None, note='count>0: variable sub-objects, boundary not walked')

    # 27*U32, then the embedded CoreCharacter whose first byte is its version (0x41 = 'A').
    o += 27 * 4
    corechar_version = rec[o]
    return dict(type=typ, handle=hnd, version=version, sublist_count=count, corechar_offset=o, corechar_version=corechar_version)


def run(path):
    d = json.load(open(path))
    pkts = d['Packets'] if isinstance(d, dict) else d
    recs = list(fa_records(pkts))
    if not recs:
        return
    ok = bad = corechar_ok = 0
    sublist_counts = {}
    msgs = []
    for rec in recs:
        try:
            r = walk_scaffold(rec)
            ok += 1
            sublist_counts[r['sublist_count']] = sublist_counts.get(r['sublist_count'], 0) + 1
            if r.get('corechar_version') == 0x41:
                corechar_ok += 1
            elif r['sublist_count'] == 0:
                msgs.append(f"  corechar version 0x{r.get('corechar_version'):02x} != 0x41 (handle {r['handle']:08x})")
        except Exception as e:
            bad += 1
            if len(msgs) < 5:
                msgs.append(f'  FAIL: {e}')
    print(f'{path.split("/")[-1]}')
    print(f'  FA015807 records: {len(recs)}   version==0x05: {ok}   framing-fail: {bad}')
    print(f'  sublist count distribution: {sublist_counts}')
    print(f'  CoreCharacter version==0x41 (where count==0): {corechar_ok}/{sublist_counts.get(0,0)}')
    for m in msgs[:6]:
        print(m)


if __name__ == '__main__':
    paths = sys.argv[1:] or sorted(glob.glob('Captures/*login*.tshark.json'))[:6]
    for p in paths:
        try:
            run(p)
        except Exception as e:
            print(f'{p}: ERROR {e}')
