#!/usr/bin/env python3
"""Diff our server's logs.txt GameServer output against a retail capture JSON dump.

Both sides are decoded into (sender, receiver, opcode, headerData, body) so the
comparison is independent of the client instance id (only the conversation family
and the opcode/length structure are compared). Use this to find which packets we
send wrong / omit during the world-entry flow.

Usage:
    python3 Captures/compare_log_to_dump.py [logs.txt] [dump.json]

Defaults: logs.txt in repo root, the Strie login capture.
"""
import sys, re, json, base64
from collections import Counter

DEFAULT_LOG = 'logs.txt'
DEFAULT_DUMP = 'Captures/account-stricken997-character-Strie-login.json'


def _read_varint(b, i):
    r = s = 0
    while True:
        x = b[i]; i += 1
        r |= (x & 0x7f) << s
        if not x & 0x80:
            return r, i
        s += 7


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


def from_dump(path, proto='GameServer', download=True):
    d = json.load(open(path))
    out = []
    for pk in d['Packets']:
        if pk['ProtocolName'] != proto or pk['IsDownload'] != download:
            continue
        p = parse(base64.b64decode(pk['RawBytesBase64']))
        if p:
            out.append(p)
    return out


def collapsed_timeline(dump_path):
    """Chronological (both directions) collapsed run-length timeline from a dump."""
    d = json.load(open(dump_path))
    ev = []
    for pk in d['Packets']:
        if pk['ProtocolName'] != 'GameServer':
            continue
        p = parse(base64.b64decode(pk['RawBytesBase64']))
        if p:
            ev.append((pk['TimestampTicks'], 'DOWN' if pk['IsDownload'] else 'UP', p[2]))
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
