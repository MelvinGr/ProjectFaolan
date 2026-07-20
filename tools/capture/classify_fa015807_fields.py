#!/usr/bin/env python3
"""Cross-capture field classification for the FA015807 NPC-spawn pre-IIR scaffold (Gitea #185).

Decodes the 6 u8 flags + 53 u32 words of every count==0 FA015807 record across ALL captures,
tags each record by its embedded CoreCharacter NPC name, and classifies every field so we know
what a from-scratch (no captured Data) scaffold builder must source:

  CONST-GLOBAL   same value on every NPC everywhere          -> hardcode
  CONST-PER-NPC  constant within a name, differs across names -> template by NPC type
  PER-INSTANCE   varies between instances of the SAME name    -> needs a live source
                 (numeric = scalar/handle/coord; ascii = the script-binding string union -> zeroable)

Run:  python3 tools/capture/classify_fa015807_fields.py
"""
import json, struct, sys, glob, re
from collections import defaultdict
import sys as _sys, pathlib as _pl
_sys.path.insert(0, str(_pl.Path(__file__).resolve().parent))
from captures import capture_files, capture  # corpus lives in the ProjectFaolan-Data repo

FA = 0xFA015807

# wire order: 6 u8 flags interleaved with 53 u32, one sublist, then corechar. (kind, struct_offset)
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


def parse(rec):
    """Return (flags[6], words[53], corechar_offset) for a count==0 record, else None."""
    o = 12  # [type][handle][0]
    try:
        tlen, o = read_varint(rec, o)
        o += tlen  # transform submsg
        if rec[o] != 0x05:
            return None
        o += 1
        flags, ws = [], []
        for kind, off in WIRE_SEQ:
            if kind == 'u8':
                flags.append(rec[o]); o += 1
            elif kind == 'u32':
                ws.append(struct.unpack('>I', rec[o:o+4])[0]); o += 4
            elif kind == 'sublist':
                if struct.unpack('>I', rec[o:o+4])[0] != 0x3F1:
                    return None
                o += 4
            else:  # corechar version byte
                if rec[o] != 0x41:
                    return None
                return flags, ws, o
    except (IndexError, struct.error):
        return None
    return None


def npc_name(rec, corechar_off):
    """First printable run (>=3) in the CoreCharacter protobuf = the NPC name. Cut at the trailing
    protobuf field tag (0x22 = field 4 wire-type 2) that the raw run leaks in."""
    seg = rec[corechar_off: corechar_off + 120]
    runs = re.findall(rb'[ -~]{3,}', seg)
    if not runs:
        return '?'
    s = runs[0].decode('latin1')
    # the leaked suffix is always a 0x22 (") followed by one framing byte; cut at it.
    return s.split('"')[0].strip()


def main():
    paths = capture_files("*.tshark.json")
    # records[name] = list of (flags, words, capture)
    records = defaultdict(list)
    total = 0
    for path in paths:
        try:
            d = json.load(open(path))
        except Exception:
            continue
        pkts = d['Packets'] if isinstance(d, dict) else d
        for rec in fa_records(pkts):
            r = parse(rec)
            if not r:
                continue
            flags, ws, cc = r
            records[npc_name(rec, cc)].append((flags, ws, path.split('/')[-1]))
            total += 1

    names = sorted(records)
    offs = [o for k, o in WIRE_SEQ if k == 'u32']

    # Flatten to a list of (name, flags, words). Split into two FORMS by non-zero word count:
    #   CLEAN  = the low-entropy rendering form (Crocodile"I, Casilda"N): few non-zero scalar words.
    #   RICH   = the script-bound / high-entropy form (Ancient Crocodile, Casilda"Q): most words set.
    flat = [(n, fl, ws) for n, grp in records.items() for (fl, ws, _) in grp]
    nz = lambda ws: sum(1 for v in ws if v != 0)
    clean = [(n, fl, ws) for (n, fl, ws) in flat if nz(ws) <= 20]
    rich = [(n, fl, ws) for (n, fl, ws) in flat if nz(ws) > 20]
    print(f'captures scanned: {len(paths)}   count==0 FA015807 records: {total}   distinct NPC names: {len(set(n for n,_,_ in flat))}')
    print(f'FORM SPLIT (by non-zero word count):  CLEAN(<=20 nz)={len(clean)}   RICH(>20 nz)={len(rich)}\n')

    def is_ascii(v):
        bs = struct.pack('>I', v)
        return v != 0 and sum(1 for c in bs if 32 <= c < 127) >= 3

    # --- Classify each word over the CLEAN (rendering) form only. This is the from-scratch builder spec.
    by_name = defaultdict(list)
    for n, fl, ws in clean:
        by_name[n].append((fl, ws))

    print('=== WORD CLASSIFICATION over CLEAN form (the build target) ===')
    print(' w  off     class           distinct  source            sample values')
    print('-' * 78)
    buckets = defaultdict(list)
    for wi in range(53):
        allvals = [ws[wi] for (_, _, ws) in clean]
        gd = set(allvals)
        const_within = all(len({ws[wi] for (_, ws) in g}) == 1 for g in by_name.values())
        if len(gd) == 1:
            cls, src = 'CONST-GLOBAL', 'hardcode'
        elif const_within:
            cls, src = 'CONST-PER-NPC', 'template by-type'
        else:
            cls, src = 'PER-INST', 'SOURCE (per-spawn)'
        buckets[cls].append(wi)
        sample = ' '.join(f'0x{x:08x}' for x in sorted(gd)[:3])
        flt = ''
        # flag plausible IEEE floats among the constants for interpretation.
        for v in list(gd)[:1]:
            if 0x38000000 <= v <= 0x47000000:
                flt = f'  (~{struct.unpack(">f", struct.pack(">I", v))[0]:g}f)'
        print(f'{wi:>2}  0x{offs[wi]:03x}  {cls:<14}  {len(gd):>6}   {src:<17} {sample}{flt}')

    print('\n=== U8 FLAGS over CLEAN form ===')
    for fi in range(6):
        allvals = [fl[fi] for (_, fl, _) in clean]
        gd = set(allvals)
        const_within = all(len({fl[fi] for (fl, _) in g}) == 1 for g in by_name.values())
        cls = 'CONST-GLOBAL' if len(gd) == 1 else ('CONST-PER-NPC' if const_within else 'PER-INST')
        print(f'flag{fi}: {cls:<14} distinct={sorted(gd)[:8]}')

    print('\n=== SUMMARY (CLEAN form) ===')
    for cls in ('CONST-GLOBAL', 'CONST-PER-NPC', 'PER-INST'):
        print(f'  {cls:<14}: {len(buckets[cls]):>2} words  {buckets[cls]}')

    # Spotlight known-rendering NPCs (clean form).
    print('\n=== KNOWN-RENDERING NPC SPOTLIGHT (clean form) ===')
    for target in ('Crocodile', 'Casilda'):
        recs = [(fl, ws) for (n, fl, ws) in clean if n == target]
        if not recs:
            continue
        fl, ws = recs[0]
        nonzero = [(wi, f'0x{ws[wi]:08x}') for wi in range(53) if ws[wi] != 0]
        print(f'\n{target}  ({len(recs)} clean instances)')
        print(f'  flags: {fl}')
        print(f'  non-zero words ({len(nonzero)}/53): {nonzero}')


if __name__ == '__main__':
    main()
