#!/usr/bin/env python3
"""Knubot conversation IIR dissectors (#188) — the NPC dialogue window the GameServer drives.

Four sub-opcodes carry the dialogue, all sharing a 19-byte IIR header then a per-type tail:

  shared header (after the 16-bit dispatch sub-opcode):
    [u32 0x0000C350]   IIR marker (Character class)
    [u32 instance]     the conversing NPC's live handle/instance (substituted per send)
    [u16 0x0000]       const
    [u8  0x01]         const
    [u32 0x0000C350]   second marker (Character class)
    [u32 id]           conversation/knubot object id (substituted per send)

  per type (each "screen" is a remoteformat XML <remoteformat ...> doc the client resolves against its RDB):
    OpenChatWindow  0x045B0B0F : [u8 flag]                                  -- open the window (flag 0/1/4)
    AppendText      0x4A3D899A : [u32 field][u16 xmlLen][remoteformat][u8 flag]   -- ONE NPC line + continue flag (0/1)
    CloseChatWindow 0xC4AF39EB : [u32 field][u16 xmlLen][remoteformat]      -- ONE farewell screen
    AnswerList      0xF164A4A5 : [u32 count]( [u16 xmlLen][remoteformat] )*count   -- the answer options

NB the u32 after the header is the ANSWER COUNT for AnswerList (1..5 seen), but for Append/Close it's a scalar
that is almost always 1 (rarely 6 on Append) with exactly ONE screen - i.e. NOT a screen count for those two.
Append's trailing flag is 1 in ~all samples (0 twice). Semantics of the two flags + the Append/Close scalar are
carried but not yet named. Validated to the packet end across all 307 matching ConversationPacket rows + captures.

Usage:
  python3 Captures/scripts/decode_knubot.py [capture.tshark.json ...]   # dissect from captures
  python3 Captures/scripts/decode_knubot.py --db faolan.db             # dissect the stored ConversationPackets
"""
import glob
import json
import sqlite3
import struct
import sys

OPS = {
    '045b0b0f': 'OpenChatWindow',
    '4a3d899a': 'AppendText',
    'f164a4a5': 'AnswerList',
    'c4af39eb': 'CloseChatWindow',
}
MARKER = 0x0000C350


def _u32(b, o):
    return int.from_bytes(b[o:o + 4], 'big')


def _u16(b, o):
    return int.from_bytes(b[o:o + 2], 'big')


def _screen(packet, o):
    ln = _u16(packet, o)
    return packet[o + 2:o + 2 + ln].decode('latin1'), o + 2 + ln


def decode(packet: bytes):
    """Decode one raw conversation packet. Returns a dict, or None if it isn't one of the four Knubot IIRs."""
    h = packet.hex()
    # Anchor on opcode + the 0x0000C350 marker so a stray opcode inside the XML payload can't false-match.
    hit = next(((op, h.find(op + f'{MARKER:08x}')) for op in OPS if (op + f'{MARKER:08x}') in h), None)
    if hit is None:
        return None
    op, bytepos = hit[0], hit[1] // 2
    o = bytepos + 4  # past the sub-opcode

    marker = _u32(packet, o); o += 4
    instance = _u32(packet, o); o += 4
    const_u16 = _u16(packet, o); o += 2
    const_u8 = packet[o]; o += 1
    marker2 = _u32(packet, o); o += 4
    conv_id = _u32(packet, o); o += 4
    assert marker == MARKER and marker2 == MARKER, f'marker {marker:#x}/{marker2:#x}'
    assert const_u16 == 0 and const_u8 == 1, f'const {const_u16}/{const_u8}'

    name = OPS[op]
    screens, field, flag = [], None, None
    if name == 'OpenChatWindow':
        flag = packet[o]; o += 1
    elif name == 'AnswerList':
        count = _u32(packet, o); o += 4
        for _ in range(count):
            s, o = _screen(packet, o); screens.append(s)
    elif name == 'AppendText':
        field = _u32(packet, o); o += 4
        s, o = _screen(packet, o); screens.append(s)
        flag = packet[o]; o += 1
    else:  # CloseChatWindow
        field = _u32(packet, o); o += 4
        s, o = _screen(packet, o); screens.append(s)

    assert o == len(packet), f'{name}: trailing {len(packet) - o} byte(s)'
    return dict(op=op, type=name, instance=instance, conv_id=conv_id, field=field, flag=flag, screens=screens)


def _iter_capture(path):
    try:
        pk = json.load(open(path))['Packets']
    except Exception:
        return
    for p in pk:
        b = p.get('BodyHex', '')
        if any(o in b.lower() for o in OPS):
            yield bytes.fromhex(b)


def _iter_db(path):
    con = sqlite3.connect(path)
    for (blob,) in con.execute('SELECT Data FROM ConversationPackets'):
        yield bytes(blob)
    con.close()


def main():
    args = sys.argv[1:]
    if args and args[0] == '--db':
        sources = _iter_db(args[1] if len(args) > 1 else 'faolan.db')
    else:
        caps = args or glob.glob('Captures/*.tshark.json')
        sources = (pkt for c in caps for pkt in _iter_capture(c))

    ok = {v: 0 for v in OPS.values()}
    bad = skipped = 0
    for pkt in sources:
        try:
            d = decode(pkt)
        except Exception as e:
            bad += 1
            if bad <= 5:
                print('FAIL:', e)
            continue
        if d is None:  # some other packet type in the conversation stream
            skipped += 1
        else:
            ok[d['type']] += 1
    total = sum(ok.values())
    print(f"decoded {total} conversation packet(s) ({skipped} non-Knubot skipped): "
          + ', '.join(f"{k}={v}" for k, v in ok.items()))
    if bad:
        print(f"{bad} FAILED")
        sys.exit(1)


if __name__ == '__main__':
    main()
