#!/usr/bin/env python3
"""Static world-object dissection workbench (66AEDD50 / F096251E / 33A56FB0).

These are the per-map static world build records (props, zone/city labels, resource volumes) that ride the
0x2000 channel alongside the entity spawns. They share the FA015807 header + 0x2C transform group, but instead
of a CharacterIIR they carry a length-prefixed object message, and there is NO 228-byte pre-IIR float block -
the message length prefix immediately follows the transform. Goal: lift these into a MapObjects table so the
static world is BUILT from data, retiring the MapWorldBurst blob (docs/spawn-dissection-plan.md Phase 6).

Body layout (offsets body-relative):
  [0:20]    header: bodyLen, subOp, ack, handle, fieldA
  [20:~65]  transform group 0x2C: 0A vec3(pos), 12 quat(rot), 18 playfield-varint  (no pre-IIR block)
  @msg      [u32 BE msgLen][object message]: f1=id(varint), f2=name(string), f3=nested, f5/f7/f8/f12=trailers
  [msgEnd:] tail (post scaffold, still raw)

Usage:
  python3 Captures/dissect_world_object.py            # table of every world object in the Strie login burst
  python3 Captures/dissect_world_object.py one <hex>  # full decode of one object by handle
"""
import importlib.util
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('c', ROOT / 'Captures/compare_log_to_dump.py')
cdt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cdt)

CAP = 'Captures/account-stricken997-character-Strie-login.json'
SUBOPS = ('66aedd50', 'f096251e', '33a56fb0')


def u32(b, i):
    return int.from_bytes(b[i:i + 4], 'big')


def rv(b, i):
    v = s = 0
    while True:
        x = b[i]
        v |= (x & 0x7F) << s
        i += 1
        s += 7
        if not x & 0x80:
            return v, i


def transform_end(b):
    """Offset just after the 0x2C transform group (where the u32 message-length prefix begins)."""
    i = 21  # past 0x2C
    i += 2 + b[i + 1]  # 0A vec3
    i += 2 + b[i + 1]  # 12 quat
    if b[i] == 0x25:
        i += 5  # optional 25 scale
    _, i = rv(b, i + 1)  # 18 playfield varint
    return i


def fvec(v):
    return [round(struct.unpack('<f', v[j + 1:j + 5])[0], 2) for j in range(0, len(v), 5)]


def message(b):
    """(msgOffset, msgLen, message bytes, tail bytes)."""
    o = transform_end(b)
    n = u32(b, o)
    return o + 4, n, b[o + 4:o + 4 + n], b[o + 4 + n:]


def walk(b, base=0, ind=0):
    i = 0
    while i < len(b):
        st = i
        try:
            tag, i = rv(b, i)
        except IndexError:
            return
        f, wt = tag >> 3, tag & 7
        pad = '  ' * ind
        if wt == 0:
            v, i = rv(b, i)
            print(f'{pad}f{f} varint {v} (0x{v:x})')
        elif wt == 2:
            ln, i = rv(b, i)
            sub = b[i:i + ln]
            printable = ln > 0 and all(0x20 <= c <= 0x7E for c in sub)
            if printable and ln < 60:
                print(f'{pad}f{f} str[{ln}] {sub.decode()!r}')
            else:
                print(f'{pad}f{f} msg[{ln}] {sub[:24].hex()}')
                if 0 < ln < 160:
                    walk(sub, base + i, ind + 1)
            i += ln
        elif wt == 5:
            print(f'{pad}f{f} f32 {struct.unpack("<f", b[i:i+4])[0]:.4g}')
            i += 4
        elif wt == 1:
            print(f'{pad}f{f} f64')
            i += 8
        else:
            print(f'{pad}f{f} wt{wt} STOP')
            return


def decode_tail(t):
    """Decompose a world-object tail into labeled fields. Round-trips byte-exact on 1854/1854 captured objects.

    Grammar:  [u32 stateHandle][u32 b][u32 strLen][state string][00 00 00 00][bbox: 6x f32 BE][f32 scale]
              [interaction protobuf]
    The state string is the object STATE ('object_closed'/'object_open'/'Open', empty for decor/zones); the
    bbox is the collision bounds (decor carries tiny normal-like floats, chests/gates the 7f7fffee default);
    `interaction` is the per-session collision/interaction protobuf (empty for decor, big for chests). The
    stateHandle + interaction coords are the per-session state that differs cross-capture.
    """
    if len(t) < 40:
        return None
    state_handle = u32(t, 0)
    b1 = u32(t, 4)
    slen = u32(t, 8)
    o = 12
    if o + slen + 28 > len(t):
        return None
    state = t[o:o + slen]
    o += slen
    pad = t[o:o + 4]
    o += 4
    bbox = [struct.unpack('>f', t[o + 4 * k:o + 4 * k + 4])[0] for k in range(6)]
    o += 24
    scale = struct.unpack('>f', t[o:o + 4])[0]
    o += 4
    return dict(state_handle=state_handle, b=b1, state=state, pad=pad, bbox=bbox, scale=scale, interaction=t[o:])


def objects():
    out = []
    for p in cdt.from_dump(CAP):
        if p[2] == 0x2000 and len(p[4]) >= 20 and p[4][4:8].hex() in SUBOPS:
            out.append(p[4])
    return out


def name_of(msg):
    i = 0
    while i < len(msg):
        tag, i = rv(msg, i)
        f, wt = tag >> 3, tag & 7
        if wt == 0:
            _, i = rv(msg, i)
        elif wt == 2:
            ln, i = rv(msg, i)
            if f == 2:
                return msg[i:i + ln].decode('ascii', 'replace')
            i += ln
        elif wt == 5:
            i += 4
        elif wt == 1:
            i += 8
        else:
            break
    return ''


def cmd_table():
    rows = objects()
    print(f'{len(rows)} static world objects in {Path(CAP).name}\n')
    hdr = f'{"subop":9} {"handle":>9} {"pos":24} {"msgLen":>6} {"tail":>4} name'
    print(hdr)
    print('-' * 78)
    for b in rows:
        sub = b[4:8].hex()
        pos = fvec(b[23:23 + b[22]])
        mo, ml, msg, tail = message(b)
        print(f'{sub:9} 0x{u32(b,12):07X} {str(pos):24} {ml:>6} {len(tail):>4} {name_of(msg)!r}')


def cmd_one(handle_hex):
    h = int(handle_hex, 16)
    b = next(x for x in objects() if u32(x, 12) == h)
    mo, ml, msg, tail = message(b)
    print(f'subop={b[4:8].hex()} handle=0x{h:X} len={len(b)} msgOff={mo} msgLen={ml} tail={len(tail)}b')
    print(f'pos={fvec(b[22:22 + b[21]])}  playfield={rv(b, transform_end(b) - 1)[0] if False else "-"}')
    print('object message:')
    walk(msg, mo, 1)
    d = decode_tail(tail)
    if d is None:
        print(f'tail ({len(tail)}b, undecoded): {tail.hex(" ")}')
    else:
        print(f'tail ({len(tail)}b):')
        print(f'  state_handle = 0x{d["state_handle"]:08X}   b = 0x{d["b"]:08X}')
        print(f'  state        = {d["state"].decode("ascii", "replace")!r}')
        print(f'  bbox         = {[round(f, 3) for f in d["bbox"]]}')
        print(f'  scale        = {d["scale"]}')
        print(f'  interaction  = {len(d["interaction"])}b protobuf: {d["interaction"][:32].hex(" ")}{"..." if len(d["interaction"]) > 32 else ""}')


def cmd_coverage():
    import glob
    total = ok = 0
    states = {}
    for cap in sorted(glob.glob(str(ROOT / 'Captures/*.json'))):
        try:
            rows = [p[4] for p in cdt.from_dump(cap)
                    if p[2] == 0x2000 and len(p[4]) >= 20 and p[4][4:8].hex() in SUBOPS]
        except Exception:
            continue
        for b in rows:
            mo, ml, msg, tail = message(b)
            total += 1
            d = decode_tail(tail)
            if d is None:
                continue
            rebuilt = (tail[:8] + len(d['state']).to_bytes(4, 'big') + d['state'] + d['pad']
                       + b''.join(struct.pack('>f', f) for f in d['bbox'])
                       + struct.pack('>f', d['scale']) + d['interaction'])
            if rebuilt == tail:
                ok += 1
                s = d['state'].decode('ascii', 'replace')
                states[s] = states.get(s, 0) + 1
    print(f'tail grammar round-trip: {ok}/{total}')
    print(f'object states: {states}')


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == 'coverage':
        cmd_coverage()
    elif len(sys.argv) > 2 and sys.argv[1] == 'one':
        cmd_one(sys.argv[2])
    else:
        cmd_table()
