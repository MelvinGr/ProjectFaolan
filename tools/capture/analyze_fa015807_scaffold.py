#!/usr/bin/env python3
"""Wire-word map for the FA015807 NPC-spawn pre-IIR scaffold (Gitea #185 / #54).

GROUNDED IN GHIDRA (tools/ghidra/out/spawn_iir.txt + spawn_fns.txt). The decompiled
SimpleCharFullUpdateIIR_t::ReadSubClass (FUN_01121c00) reads, after the u8 version==0x05:

    53 x FUN_0048f900 (ReadU32, a plain 4-byte read + optional byteswap)
     6 x FUN_004e0390 (ReadByte, a plain 1-byte read)
     1 x FUN_01122b10 (the count-framed sublist; count==0 on beach NPCs)
     1 x FUN_0145ac80 (the embedded CoreCharacterFullUpdateIIR)

There are NO float reads and NO string/blob reads in the sequence. The block is therefore a
FIXED-WIDTH 222 bytes (version+1 .. CoreChar) — that's why the framing walk always lands on
CoreChar 0x41 at +223. The ASCII seen in some words is the VALUE of u32 slots that are fixed-width
inline char buffers in the C++ struct (read 4 bytes at a time), not a special read kind.

WIRE_SEQ below is the exact read order recovered from the decompile, each u32 tagged with the
struct offset it is stored to (in_ECX + off); bytes/sublist/corechar carry no DB-relevant value.
This script classifies each wire word over one beach world-entry burst: constant-vs-variable and
numeric-vs-ascii (char-buffer) — the corrected "what feeds each word" map.

Run:  python3 tools/capture/analyze_fa015807_scaffold.py [capture.tshark.json]
"""
import json, struct, sys, glob
from collections import defaultdict

FA = 0xFA015807

# (kind, struct_offset)  — struct_offset None for byte/sublist/corechar. Order == wire order.
WIRE_SEQ = (
    [('u8', None)]
    + [('u32', o) for o in (0x184,0x188,0x18c,0x190,0x194,0x198,0x19c,
                            0x1a8,0x1b0,0x1c0,0x1c8,0x1d0,0x1d8,0x1e0,0x1e8,0x1f0,0x1b8)]
    + [('u8', None)] * 3
    + [('u32', o) for o in (0x1fc,0x200,0x204,0x208)]
    + [('u8', None)] * 2
    + [('u32', o) for o in (0x210,0x214,0x218,0x21c,0x220)]
    + [('sublist', None)]
    + [('u32', o) for o in (0x1a0,0x1a4,0x1ac,0x1b4,0x1c4,0x1cc,0x1d4,0x1dc,0x1e4,0x1ec,0x1f4,0x1bc,
                            0x224,0x230,0x228,0x234,0x238,0x23c,0x240,0x244,0x248,0x24c,0x254,0x258,0x25c,0x260,0x264)]
    + [('corechar', None)]
)


def fa_records(pkts):
    for p in pkts:
        if not (p.get('IsDownload') and p.get('Opcode') == '0x2000'):
            continue
        b = bytes.fromhex(p['BodyHex'])
        if len(b) < 8:
            continue
        ln, it = struct.unpack('>II', b[0:8])
        if ln == len(b) - 4 and it == FA:
            yield b[8:]


def read_varint(rec, o):
    val = shift = 0
    while True:
        x = rec[o]; o += 1
        val |= (x & 0x7f) << shift
        if not (x & 0x80):
            return val, o
        shift += 7


def words(rec):
    """Yield (wire_index, kind, offset, raw4) for each scaffold word of a count==0 record, else None."""
    o = 12  # [type][handle][0]
    tlen, o = read_varint(rec, o)
    o += tlen  # transform
    if rec[o] != 0x05:
        return None
    o += 1
    out = []
    for wi, (kind, off) in enumerate(WIRE_SEQ):
        if kind == 'u8':
            out.append((wi, kind, off, rec[o])); o += 1
        elif kind == 'u32':
            out.append((wi, kind, off, struct.unpack('>I', rec[o:o+4])[0])); o += 4
        elif kind == 'sublist':
            framed = struct.unpack('>I', rec[o:o+4])[0]; o += 4
            if framed != 0x3F1:
                return None  # count>0 — variable, out of scope
            out.append((wi, kind, off, framed))
        else:  # corechar
            out.append((wi, kind, off, rec[o]))
            break
    return out


def is_ascii_run(v):
    bs = struct.pack('>I', v)
    return sum(1 for c in bs if 32 <= c < 127) >= 3 and v != 0


def decode_text(path, limit=8):
    """Reassemble the 53 u32 slots in STRUCT-OFFSET order (not wire order) and read them little-endian:
    that recovers the contiguous char buffer the client scatters into struct fields 0x184..0x264. The
    content is the NPC's RDB script-binding diagnostic string (ScriptGroup/Script/Command + font markup)."""
    import re
    u32_offsets = [off for kind, off in WIRE_SEQ if kind == 'u32']
    n = 0
    for rec in fa_records(json.load(open(path))['Packets']):
        ws = words(rec)
        if not ws:
            continue
        u32s = [(off, struct.pack('>I', v)) for _, kind, off, v in ws if kind == 'u32']
        if len(u32s) != 53:
            continue
        buf = bytearray(0xE8)
        for off, raw in u32s:
            buf[off - 0x184: off - 0x184 + 4] = raw[::-1]  # client byteswaps (endian flag set) -> wire is LE
        txt = buf.decode('latin1')
        frags = [f for f in re.split(r'[^ -~]+', txt) if len(f) >= 6]
        if frags:
            hnd = struct.unpack('>I', rec[4:8])[0]
            print(f'{hnd:08x}: ' + ' | '.join(frags)[:160])
            n += 1
        if n >= limit:
            return


def main(path):
    recs = [r for r in fa_records(json.load(open(path))['Packets'])]
    parsed = [w for w in (words(r) for r in recs) if w]
    print(f'{path.split("/")[-1]}  —  {len(parsed)} count==0 records\n')
    print('wi  off     kind   card  class      sample')
    print('-' * 64)
    by_wi = defaultdict(list)
    for rowwords in parsed:
        for wi, kind, off, v in rowwords:
            by_wi[wi].append((kind, off, v))
    for wi in sorted(by_wi):
        col = by_wi[wi]
        kind, off, _ = col[0]
        vals = [v for _, _, v in col]
        distinct = set(vals)
        if kind in ('sublist', 'corechar', 'u8'):
            cls = kind
        elif len(distinct) == 1:
            cls = 'CONST' if next(iter(distinct)) == 0 else 'const-nz'
        else:
            ascii_frac = sum(1 for v in vals if is_ascii_run(v)) / len(vals)
            cls = 'ascii/charbuf' if ascii_frac > 0.3 else 'numeric'
        offs = f'0x{off:03x}' if off is not None else '   -'
        sample = sorted(distinct)[:3]
        samp = ' '.join(f'0x{x:08x}' for x in sample) if kind != 'u8' else ' '.join(str(x) for x in sample)
        print(f'{wi:>2}  {offs}  {kind:<6} {len(distinct):>4}  {cls:<13} {samp}')


if __name__ == '__main__':
    args = sys.argv[1:]
    text_mode = '--text' in args
    paths = [a for a in args if a != '--text'] or [sorted(glob.glob('Captures/*hoxxling-login.tshark.json'))[0]]
    for p in paths:
        if text_mode:
            print(f'== {p.split("/")[-1]} — struct-order char-buffer text ==')
            decode_text(p)
        else:
            main(p)
