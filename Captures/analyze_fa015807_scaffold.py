#!/usr/bin/env python3
"""Column<->offset map analysis for the FA015807 NPC-spawn pre-IIR scaffold (Gitea #185 / #54).

The grammar is proven (docs/fa015807-spawn-reader.md, decode_fa015807_reader.py). The remaining step is
the SEMANTIC map: which of the 53 scaffold U32s (+ 6 U8s) are constant across all spawns (-> emit as
literals from NpcSpawnBuilder) vs which vary (-> need a DB column / re-address to the live session).

This walks every count==0 FA015807 record across the captures, extracts each scaffold field by its read-
sequence index, and reports per-field cardinality. For the varying fields it tries to explain the value:
  - == record handle              (session entity ref -> re-address, NOT a column)
  - == playfield instance         (from the transform -> session token)
  - lands in the CoreCharacter    (sanity: the post-sublist 27 words butt up against CoreChar ver 0x41)
Constant fields print their single value; low-cardinality fields print the distribution.

Run:  python3 Captures/analyze_fa015807_scaffold.py [capture.tshark.json ...]
"""
import json, struct, sys, glob
from collections import defaultdict

FA = 0xFA015807

# The fixed pre-IIR read sequence after the leading u8 version=0x05 (docs/fa015807-spawn-reader.md):
#   U8 . 17*U32 . 3*U8 . 4*U32 . 2*U8 . 5*U32 . SUBLIST . 27*U32 . CoreChar
PRE_SEQ = [(1, 1), (17, 4), (3, 1), (4, 4), (2, 1), (5, 4)]  # before the sublist
POST_U32 = 27                                                 # after the sublist, before CoreChar


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


def parse(rec):
    """Return (fields, ctx) for a count==0 record, or None if count>0 / framing off.
    fields: ordered list of (label, value). ctx: handle + playfield for correlation."""
    o = 0
    typ, hnd, zero = struct.unpack('>III', rec[o:o + 12]); o += 12
    tlen, o = read_varint(rec, o)
    transform = rec[o:o + tlen]; o += tlen
    if rec[o] != 0x05:
        return None
    o += 1

    fields = []
    seq_idx = 0  # running index into the pre-sublist read sequence (for stable labels)
    for n, sz in PRE_SEQ:
        for _ in range(n):
            if sz == 1:
                v = rec[o]; o += 1
                fields.append((f'pre.u8[{seq_idx}]', v))
            else:
                v = struct.unpack('>I', rec[o:o + 4])[0]; o += 4
                fields.append((f'pre.u32[{seq_idx}]', v))
            seq_idx += 1

    framed = struct.unpack('>I', rec[o:o + 4])[0]; o += 4
    if framed % 0x3F1 != 0 or framed // 0x3F1 - 1 != 0:
        return None  # count>0: variable sub-object, not part of this map
    fields.append(('sublist.frame', framed))

    for i in range(POST_U32):
        v = struct.unpack('>I', rec[o:o + 4])[0]; o += 4
        fields.append((f'post.u32[{i}]', v))

    corechar_off = o
    corechar_ver = rec[o]

    # playfield instance = last u32 of the transform protobuf, if present (field 3, fixed32-ish tail)
    playfield = struct.unpack('>I', transform[-4:])[0] if len(transform) >= 4 else None
    return fields, dict(handle=hnd, type=typ, playfield=playfield,
                        corechar_off=corechar_off, corechar_ver=corechar_ver)


def main(paths):
    per_field_vals = defaultdict(list)   # label -> list of values
    per_field_ctx = defaultdict(list)    # label -> list of ctx dicts (parallel)
    total = 0
    corechar_ok = 0
    for path in paths:
        try:
            d = json.load(open(path))
        except Exception:
            continue
        pkts = d['Packets'] if isinstance(d, dict) else d
        for rec in fa_records(pkts):
            r = parse(rec)
            if r is None:
                continue
            fields, ctx = r
            total += 1
            if ctx['corechar_ver'] == 0x41:
                corechar_ok += 1
            for label, val in fields:
                per_field_vals[label].append(val)
                per_field_ctx[label].append(ctx)

    print(f'count==0 records analysed: {total}   CoreChar ver==0x41: {corechar_ok}\n')
    print(f'{"field":<14} {"distinct":>8}  explanation / value')
    print('-' * 72)
    # preserve read order
    order = [l for l, _ in (parse(next(iter([r for p in paths[:1] for r in fa_records(json.load(open(p))['Packets']) if parse(r)]), b'')) or ([], {}))[0]] if False else list(per_field_vals.keys())
    for label in per_field_vals:
        vals = per_field_vals[label]
        ctxs = per_field_ctx[label]
        distinct = set(vals)
        nd = len(distinct)
        if nd == 1:
            v = next(iter(distinct))
            print(f'{label:<14} {nd:>8}  CONST 0x{v:08x} ({v})')
        else:
            # correlation: how often does the value equal the handle / playfield?
            eq_handle = sum(1 for v, c in zip(vals, ctxs) if v == c['handle'])
            eq_pf = sum(1 for v, c in zip(vals, ctxs) if c['playfield'] is not None and v == c['playfield'])
            note = []
            if eq_handle: note.append(f'==handle {eq_handle}/{len(vals)}')
            if eq_pf: note.append(f'==playfield {eq_pf}/{len(vals)}')
            sample = sorted(distinct)[:6]
            samp = ' '.join(f'0x{x:08x}' for x in sample)
            print(f'{label:<14} {nd:>8}  VARIES [{", ".join(note) or "uncorrelated"}]  e.g. {samp}')


if __name__ == '__main__':
    paths = sys.argv[1:] or sorted(glob.glob('Captures/*login*.tshark.json'))
    main(paths)
