#!/usr/bin/env python3
"""Cross-capture classification of the FA015807 NPC-spawn POST-IIR scaffold (Gitea #185, companion to
classify_fa015807_fields.py which did the pre-IIR scaffold).

The post-IIR is the stream after the embedded ProtoCoreCharacterIIR. Animals carry a constant 41-byte
minimal form; humanoids carry a larger block (documented as an appearance/float-stat table + runtime
records). This sweep answers: for the from-scratch builder, is the humanoid extra a per-NPC-TYPE constant
(=config we must author) or per-INSTANCE runtime (=droppable), and is there a constant minimal floor.

Run:  python3 tools/capture/classify_fa015807_postiir.py
"""
import json, struct, glob, re
from collections import defaultdict, Counter

FA = 0xFA015807


def fa_records(pkts):
    for p in pkts:
        if not (p.get('IsDownload') and p.get('Opcode') == '0x2000'):
            continue
        b = bytes.fromhex(p['BodyHex'])
        if len(b) < 8:
            continue
        ln, it = struct.unpack('>II', b[0:8])
        if ln == len(b) - 4 and it == FA:
            yield b[8:]  # [type][handle][0] + transform + pre-IIR + IIR + post-IIR


def find_iir(body):
    """Replicate PlayerSpawnReader.TryReadCharacterMessage: 0x0A name field, 4-byte BE len at i-4.
    Returns (iir_offset, iir_len) or None."""
    for i in range(4, len(body) - 2):
        if body[i] != 0x0A:
            continue
        nlen = body[i + 1]
        if not (1 <= nlen <= 63) or i + 2 + nlen > len(body):
            continue
        if any(c < 0x20 or c > 0x7E for c in body[i + 2:i + 2 + nlen]):
            continue
        mlen = struct.unpack('>I', body[i - 4:i])[0]
        if mlen < nlen + 2 or i + mlen > len(body):
            continue
        return i, mlen, body[i + 2:i + 2 + nlen].decode('latin1')
    return None


def has_equipment(iir):
    """Shallow protobuf scan for field 23 (inventory_entry) = worn equipment -> humanoid."""
    i = 0
    while i < len(iir):
        v = s = 0
        while True:
            x = iir[i]; i += 1; v |= (x & 0x7f) << s; s += 7
            if not x & 0x80:
                break
        f, wt = v >> 3, v & 7
        if f == 23 and wt == 2:
            return True
        if wt == 0:
            while iir[i] & 0x80:
                i += 1
            i += 1
        elif wt == 2:
            ln = 0; s = 0
            while True:
                x = iir[i]; i += 1; ln |= (x & 0x7f) << s; s += 7
                if not x & 0x80:
                    break
            i += ln
        elif wt == 5:
            i += 4
        elif wt == 1:
            i += 8
        else:
            break
    return False


# the documented minimal 41-byte post-IIR: header [u32 0][u32 5][u32 1] + constant body
MINIMAL = bytes.fromhex('000000000000000500000001') + bytes([
    0x00,0x00,0x00,0x03,0xF1,
    0x00,0x00,0x00,0x00,0x00,0x00,0x03,0xF1,
    0x00,0x00,0x03,0xF1,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x03,0xF1])


def main():
    # rec: name -> list of (is_humanoid, post_bytes, capture)
    recs = defaultdict(list)
    total = 0
    for path in sorted(glob.glob('Captures/*.tshark.json')):
        try:
            d = json.load(open(path))
        except Exception:
            continue
        pk = d['Packets'] if isinstance(d, dict) else d
        cap = path.split('/')[-1][:36]
        for body in fa_records(pk):
            r = find_iir(body)
            if not r:
                continue
            off, ln, name = r
            iir = body[off:off + ln]
            post = body[off + ln:]
            recs[name.split('"')[0].strip()].append((has_equipment(iir), post, cap))
            total += 1

    flat = [(n, h, p) for n, lst in recs.items() for (h, p, _) in lst]
    animals = [(n, p) for n, h, p in flat if not h]
    humans = [(n, p) for n, h, p in flat if h]
    print(f'records: {total}   animals(no equip): {len(animals)}   humanoids(equip): {len(humans)}\n')

    print(f'MINIMAL 41-byte form length check: len(MINIMAL)={len(MINIMAL)}')
    amin = sum(1 for _, p in animals if p == MINIMAL)
    hmin = sum(1 for _, p in humans if p == MINIMAL)
    print(f'records byte-equal to MINIMAL:  animals={amin}/{len(animals)}   humanoids={hmin}/{len(humans)}\n')

    def lenhist(rows, label):
        c = Counter(len(p) for _, p in rows)
        print(f'{label} post-IIR length histogram (len: count), min={min(c) if c else 0}:')
        for k in sorted(c):
            print(f'  {k:>5}: {c[k]}')
        print()
    lenhist(animals, 'ANIMAL')
    lenhist(humans, 'HUMANOID')

    # Is the humanoid post-IIR constant per NPC type? (=appearance config) or per-instance? (=runtime)
    print('=== HUMANOID post-IIR: constant per NPC type? ===')
    byname = defaultdict(list)
    for n, h, p in flat:
        if h:
            byname[n].append(p)
    const_type = vary_type = 0
    examples_const = []
    examples_vary = []
    for n, ps in byname.items():
        if len(ps) < 2:
            continue
        if len(set(ps)) == 1:
            const_type += 1
            if len(examples_const) < 6:
                examples_const.append(f'{n}({len(ps)}x len={len(ps[0])})')
        else:
            vary_type += 1
            if len(examples_vary) < 8:
                examples_vary.append(f'{n}({len(ps)}x lens={sorted(set(len(x) for x in ps))})')
    print(f'  NPC types with >=2 humanoid instances: {const_type+vary_type}')
    print(f'  CONSTANT across instances (appearance config): {const_type}  e.g. {examples_const}')
    print(f'  VARIES across instances (has runtime tail):   {vary_type}  e.g. {examples_vary}')

    # For a varying humanoid, does the HEAD (first N bytes) stay constant while the tail grows?
    print('\n=== Does a varying humanoid share a constant HEAD (appearance) with a growing runtime TAIL? ===')
    for n, ps in byname.items():
        if len(set(ps)) > 1 and len(ps) >= 4:
            ps_sorted = sorted(ps, key=len)
            shortest = ps_sorted[0]
            # common prefix length across all instances
            cpl = len(shortest)
            for p in ps_sorted[1:]:
                cpl = min(cpl, next((k for k in range(min(len(p), len(shortest))) if p[k] != shortest[k]), min(len(p), len(shortest))))
            print(f'  {n}: {len(ps)}x  lens={sorted(set(len(x) for x in ps))}  shortest={len(shortest)}  common-prefix={cpl}B')
            if len([1 for _ in range(0)]) or n in ('Citizen', 'Nobleman', 'Guard', 'Commoner', 'Black Dragon Guard'):
                pass


if __name__ == '__main__':
    main()
