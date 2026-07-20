#!/usr/bin/env python3
"""Decode the character-create EDITOR download stream (issue #189).

The create-editor runs on a GameServer connection and, between the client's 0x205F
"ready" pokes, the server streams a burst of 0x2000 packets. Those 0x2000 packets are
NOT all the same thing - 0x2000 multiplexes by the (SenderClass, ReceiverClass) endpoint
pair (the usual GameServer RPC-class multiplexing). This script splits the stream by
sub-protocol, frames the IIR-carrier records, and decodes the fixed-grammar record bodies
so the verbatim RetailCreateEditor*Burst replay can be retired for a constructive builder.

Sub-protocols seen on a create-editor 0x2000 download (class pair -> framing):
  b171ce13 -> 6c67c147 (GameAgent -> GameInterface) : IIR carrier  [u32 len][u32 iirType][body]
  5b9756fc -> 28377648                               : entity register [u32 0xC350][u32 handle][u32 val]
  604ddba0 -> 0c384054                               : server-id text  [u16 len][ascii hex token]
  6dd55614 -> bbd560c8                               : ack             [u32 0x00000001]

Run:  python3 tools/capture/decode_create_editor.py [capture.tshark.json ...]
"""
import json, struct, sys, glob
from collections import Counter, defaultdict

IIR_NAMES = {
    0x5D85BFC7: 'PlayfieldFIIR', 0x4F57DC08: 'GameTimeIIR', 0x40749B8A: 'EnergyIIR',
    0x28D0AC99: 'RegenIIR', 0xBADF5A4B: 'SetMorphValueIIR', 0xADCE0CDA: 'SetNameIIR',
    0xF98E10B3: 'StatIIR', 0x96B8DC59: 'StatUpdateIIR', 0x66AEDD50: 'SimpleDynelFullUpdateIIR',
    0xFA015807: 'SimpleCharFullUpdateIIR', 0x6B87E316: 'MagicBuffIIR', 0x4517AA81: 'CharDCMoveIIR',
    0x642CD3D6: 'BehaviorControlCenterIIR', 0xA36D3B74: 'ScryScriptIIR', 0x737A6DF9: 'FullPlayerCharacterIIR',
    0xF508F4C1: 'GameActionListIIR', 0x864CFEF8: 'CharInPlayIIR', 0xA8BBEB7F: 'ComboBookIIR',
    0x96C46740: 'MoveTaskIIR', 0xD751EBB4: 'StopIIR', 0x04B82B2D: 'N3ToClientQuitIIR',
}
IIR_CARRIER_SENDER = '0xb171ce13'  # GameAgent class; create-editor IIR records ride this pair


def f32(b, o):
    return struct.unpack('>f', b[o:o + 4])[0]


def read_varint(b, o):
    """protobuf base-128 varint -> (value, next_offset)."""
    v = 0; shift = 0
    while True:
        x = b[o]; o += 1
        v |= (x & 0x7f) << shift
        if not (x & 0x80):
            return v, o
        shift += 7


def decode_transform(b, o):
    """The embedded ProtoGameCodeDynelIIR transform sub-message that prefixes every entity
    full-update: [varint len][ 0a 0f <Vec3 pos> ][ 12 14 <Quat rot> ][ 18 <varint playfield> ].
    Returns (text, next_offset)."""
    ln, o = read_varint(b, o)
    end = o + ln
    pos = rot = None; pf = None
    while o < end:
        tag = b[o]; o += 1
        if tag == 0x0a:  # field1 position (Vec3)
            vl, o = read_varint(b, o)
            # Vec3 = 0d <x> 15 <y> 1d <z>  (fixed32 floats, little-endian payload)
            pos = (struct.unpack('<f', b[o + 1:o + 5])[0], struct.unpack('<f', b[o + 6:o + 10])[0], struct.unpack('<f', b[o + 11:o + 15])[0])
            o += vl
        elif tag == 0x12:  # field2 rotation (Quat)
            vl, o = read_varint(b, o)
            rot = tuple(struct.unpack('<f', b[o + 1 + 5 * i:o + 5 + 5 * i])[0] for i in range(4))
            o += vl
        elif tag == 0x18:  # field3 playfield_instance (varint)
            pf, o = read_varint(b, o)
        else:
            break
    return f'pos={pos} rot={rot} playfield={pf}', end


def ascii_strings(b, minlen=4):
    out = []; cur = bytearray()
    for c in b:
        if 32 <= c < 127:
            cur.append(c)
        else:
            if len(cur) >= minlen:
                out.append(cur.decode())
            cur = bytearray()
    if len(cur) >= minlen:
        out.append(cur.decode())
    return out


def carriers(pkts):
    """Yield (iirType, body) for every well-framed IIR-carrier 0x2000 download."""
    for p in pkts:
        if not (p.get('IsDownload') and p.get('Opcode') == '0x2000'):
            continue
        if p.get('SenderClass') != IIR_CARRIER_SENDER:
            continue
        b = bytes.fromhex(p['BodyHex'])
        if len(b) < 8:
            continue
        ln, it = struct.unpack('>II', b[0:8])
        if ln != len(b) - 4:
            continue  # not a single-record carrier on this pair
        yield it, b[8:]


def decode(path):
    d = json.load(open(path))
    pkts = d['Packets'] if isinstance(d, dict) else d
    recs = defaultdict(list)
    for it, body in carriers(pkts):
        recs[it].append(body)

    print(f'\n########## {path}')
    total = sum(len(v) for v in recs.values())
    print(f'IIR-carrier records: {total} across {len(recs)} types\n')
    for it in sorted(recs, key=lambda k: -len(recs[k])):
        nm = IIR_NAMES.get(it, '?%08X' % it)
        rs = recs[it]
        print(f'{len(rs):4d}  {nm:26s} lens={sorted(set(len(r) for r in rs))}')

    # --- fully-decoded fixed grammars -----------------------------------------------------------
    if 0xF98E10B3 in recs:
        # StatIIR: [u32 type][u32 handle][u8 flag][u32 statId][u32 value]  (17 bytes)
        ok = all(len(r) == 17 for r in recs[0xF98E10B3])
        print(f'\nStatIIR grammar [type][handle][u8 flag][u32 statId][u32 value]: {"VALID" if ok else "MISMATCH"} (all 17b)')
        sample = recs[0xF98E10B3][0]
        ty, hn = struct.unpack('>II', sample[0:8]); fl = sample[8]; sid, val = struct.unpack('>II', sample[9:17])
        print(f'   e.g. type={ty:08x} handle={hn:08x} flag={fl} statId={sid} value={val}')

    if 0x40749B8A in recs:
        # EnergyIIR: [u32 type][u32 handle][u8 ?][u32 value]  (13 bytes)
        r = recs[0x40749B8A][0]
        print(f'\nEnergyIIR [type][handle][u8][u32 value] (13b): {r.hex()}')

    # --- entity full-updates: shared SimpleChar/SimpleDynel grammar ------------------------------
    for it, nm in ((0x66AEDD50, 'SimpleDynelFullUpdateIIR'), (0xFA015807, 'SimpleCharFullUpdateIIR')):
        if it not in recs:
            continue
        r = recs[it][0]
        ty, hn, zero = struct.unpack('>III', r[0:12])
        tf, after = decode_transform(r, 12)
        strs = ascii_strings(r[after:])
        print(f'\n{nm}: [type={ty:08x}][handle={hn:08x}][u32={zero}] + transform({tf})')
        print(f'   embedded protobuf strings: {strs[:4]}')


if __name__ == '__main__':
    paths = sys.argv[1:] or sorted(glob.glob('Captures/*create*.tshark.json'))
    for p in paths:
        try:
            decode(p)
        except Exception as e:
            print(f'{p}: ERROR {e}')
