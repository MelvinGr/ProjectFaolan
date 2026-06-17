#!/usr/bin/env python3
"""Diff our server's logs.txt GameServer output against a retail capture JSON dump.

Both sides are decoded into (sender, receiver, opcode, headerData, body) so the
comparison is independent of the client instance id (only the conversation family
and the opcode/length structure are compared). Use this to find which packets we
send wrong / omit during the world-entry flow.

Usage:
    python3 Captures/scripts/compare_log_to_dump.py [logs.txt] [dump.json]

Defaults: logs.txt in repo root, the Strie login capture.
"""
import sys, re, json, struct
from collections import Counter

DEFAULT_LOG = 'logs.txt'
DEFAULT_DUMP = 'Captures/Strie_rogue-assassin_login.tshark.json'

# GameServer RPC endpoint class ids (ConanEndpoints.cs). The .tshark.json dump tags every Conan packet
# Protocol="Conan", so the GameServer conversation is selected by these classes, not the old ProtocolName.
GAMESERVER_CLASSES = {f'0x{c:08x}' for c in (
    0x6C67C147,  # GameInterface
    0xB171CE13,  # GameAgent
    0x5B9756FC,  # GameCharAgentProject
    0x28377648,  # GameCharInterfaceProject
    0xA9ECB95D,  # GameCharAgent
    0x8B87F791,  # GameCharInterface
)}


def _read_varint(b, i):
    r = s = 0
    while True:
        x = b[i]; i += 1
        r |= (x & 0x7f) << s
        if not x & 0x80:
            return r, i
        s += 7


def _write_varint(v):
    out = bytearray()
    while True:
        b = v & 0x7f; v >>= 7
        out.append(b | (0x80 if v else 0))
        if not v:
            return bytes(out)


def _endpoint_blob(cls_hex, inst_hex):
    """Rebuild the on-wire ProtoEndpoint blob [0x0d <classId LE32> (0x10 <instance varint>)?] so the dump side
    yields the same (sender, receiver, ...) hex tuple shape the raw-frame log side produces."""
    blob = bytes([0x0d]) + struct.pack('<I', int(cls_hex, 16))
    if inst_hex is not None:
        blob += bytes([0x10]) + _write_varint(int(inst_hex, 16))
    return blob


def _is_gameserver(pk):
    return ((pk.get('SenderClass') or '').lower() in GAMESERVER_CLASSES
            or (pk.get('ReceiverClass') or '').lower() in GAMESERVER_CLASSES)


def dump_tuple(pk):
    """(sender, receiver, opcode, headerData, body) from a de-framed .tshark.json record — same shape as parse()."""
    if not pk.get('Opcode'):
        return None
    sender = _endpoint_blob(pk['SenderClass'], pk.get('SenderInstance'))
    receiver = _endpoint_blob(pk['ReceiverClass'], pk.get('ReceiverInstance'))
    opcode = int(pk['Opcode'], 16)
    hd = bytes.fromhex((pk.get('HeaderExtraHex') or '').replace(' ', ''))
    return sender.hex(), receiver.hex(), opcode, hd.hex(), bytes.fromhex(pk.get('BodyHex') or '')


def parse(b):
    """Parse a full Conan packet (len4 + crc4 + header...) -> tuple or None."""
    try:
        i = 8
        hlen = int.from_bytes(b[i:i+4], 'big'); i += 4; hend = i + hlen
        i += 1; slen, i = _read_varint(b, i); sender = b[i:i+slen]; i += slen
        i += 1; rlen, i = _read_varint(b, i); receiver = b[i:i+rlen]; i += rlen
        opcode = int.from_bytes(b[i:i+2], 'big'); i += 2
        return sender.hex(), receiver.hex(), opcode, b[i:hend].hex(), b[hend:]
    except Exception:
        return None


def from_log(path, proto='GameServer', direction='<='):
    """direction '<=' = our outgoing, '=>' = client->server (our incoming)."""
    out = []
    rx = re.compile(r'(<=|=>) ConanPacket raw \((\d+) bytes\): \{([^}]*)\}')
    for line in open(path, encoding='utf-8', errors='replace'):
        if proto not in line:
            continue
        m = rx.search(line)
        if not m or m.group(1) != direction:
            continue
        b = bytes(int(x, 16) for x in re.findall(r'0x([0-9A-Fa-f]{2})', m.group(3)))
        p = parse(b)
        if p:
            out.append(p)
    return out


def from_dump(path, download=True):
    d = json.load(open(path))
    out = []
    for pk in d['Packets']:
        if not _is_gameserver(pk) or bool(pk['IsDownload']) != download:
            continue
        p = dump_tuple(pk)
        if p:
            out.append(p)
    return out


def collapsed_timeline(dump_path):
    """Chronological (both directions) collapsed run-length timeline from a dump."""
    d = json.load(open(dump_path))
    ev = []
    for pk in d['Packets']:
        if not _is_gameserver(pk):
            continue
        p = dump_tuple(pk)
        if p:
            ev.append((pk['Frame'], 'DOWN' if pk['IsDownload'] else 'UP', p[2]))
    ev.sort()
    out = []; prev = None; cnt = 0
    for _, dr, op in ev:
        k = (dr, op)
        if k == prev:
            cnt += 1
        else:
            if prev:
                out.append(f'{prev[0]} {hex(prev[1])} x{cnt}')
            prev = k; cnt = 1
    if prev:
        out.append(f'{prev[0]} {hex(prev[1])} x{cnt}')
    return ' | '.join(out)


def counts_by_family_opcode(pkts, is_dump):
    # log tuple: (s, r, op, hd, body); dump tuple identical
    c = Counter((p[1][:12], hex(p[2])) for p in pkts)
    return c


def main():
    log = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_LOG
    dump = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_DUMP

    ours = from_log(log)
    theirs = from_dump(dump)

    print(f'OUR outgoing GameServer packets: {len(ours)}   DUMP downloads: {len(theirs)}\n')

    co = counts_by_family_opcode(ours, False)
    cd = counts_by_family_opcode(theirs, True)
    keys = sorted(set(co) | set(cd))
    print(f'{"family":14}{"opcode":10}{"ours":>6}{"dump":>6}  flag')
    for fam, op in keys:
        o = co.get((fam, op), 0); t = cd.get((fam, op), 0)
        flag = '' if o == t else ('  <-- MISSING' if o == 0 else ('  <-- EXTRA' if t == 0 else '  <-- DIFF'))
        print(f'{fam:14}{op:10}{o:6}{t:6}{flag}')

    print('\nDUMP chronological timeline (both directions):')
    print(collapsed_timeline(dump))


if __name__ == '__main__':
    main()
