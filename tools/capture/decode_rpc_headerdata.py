#!/usr/bin/env python3
"""Decode the RpcHeader trailing `headerData` blob (Gitea #319).

`PacketStream.WriteHeader` appends an optional blob after the u16 opcode, inside the header length; the Wireshark
plugin surfaces it per packet as `HeaderExtraHex`. This scans every `Captures/*.tshark.json` and proves the blob is
NOT a generic per-packet grammar — it is exactly four per-opcode/constant cases:

  1 byte  0x01            constant marker; rides the ping/pong (0x2085 c2s / 0x2082 DispatchPong s2c) and a few
                          CSPlayerInterface dispatches. Always 0x01 across every capture; the overwhelming majority
                          of packets. Its *semantic* (flag? version?) is the one micro-residual left on #319.
  4 bytes <method id>     PlayerAgent/PlayerInterface ONLY — the 32-bit method id (Gitea #320,
                          docs/re/aoc-rpc-method-opcodes.md). Keyed by (opcode, DIRECTION), not opcode alone: a
                          couple of 16-bit opcode indices are reused by a c2s method and its s2c twin, which carry
                          different 32-bit ids (expected — the 16-bit value is a plain index, dispatch is on the hash).
  3 bytes 0x94A760        PlayerInterface SmallCharList (0x20A5), constant.
  13 bytes                PlayerInterface OverrideGameTweaks (0x1A07) — a protobuf endpoint reference, NOT an opaque
                          block: 0x0D <fixed32 class id 0x7D155738 = PlayerInterface> 0x10 <varint 1> 0x20 <varint
                          per-session instance id>. Decoded here (parse_endpoint13), consumes all 13 bytes.

Anything else printed under "UNEXPECTED" is a parser artifact, not a new grammar (e.g. a 60-byte record on opcode
0x0005 is the ServiceConnection handshake mis-attributed to HeaderExtraHex — its bytes are ProtoEndpoint-shaped).

Usage:  tools/capture/decode_rpc_headerdata.py [Captures/*.tshark.json ...]
"""
import json, glob, sys, collections

KNOWN = {1, 3, 4, 13}  # the four real case lengths


def flat_fields(node, acc):
    if isinstance(node, dict):
        acc.update({k: v for k, v in node.items() if not isinstance(v, (dict, list))})
        for v in node.values():
            flat_fields(v, acc)
    elif isinstance(node, list):
        for v in node:
            flat_fields(v, acc)


def _rd_varint(b, i):
    v = s = 0
    while True:
        x = b[i]
        i += 1
        v |= (x & 0x7F) << s
        s += 7
        if not x & 0x80:
            return v, i


def parse_endpoint13(hx):
    """Parse a 13-byte OverrideGameTweaks headerData as a protobuf endpoint reference; None if it doesn't fit."""
    try:
        b = bytes.fromhex(hx)
        i = 0
        assert b[i] == 0x0D  # field 1, fixed32
        i += 1
        classid = int.from_bytes(b[i:i + 4], 'little')
        i += 4
        assert b[i] == 0x10  # field 2, varint
        i += 1
        f2, i = _rd_varint(b, i)
        assert b[i] == 0x20  # field 4, varint
        i += 1
        f4, i = _rd_varint(b, i)
        if i != len(b):
            return None
        return classid, f2, f4
    except Exception:  # noqa: BLE001
        return None


def scan(node, out):
    if isinstance(node, dict):
        if 'HeaderExtraHex' in node:
            acc = {}
            flat_fields(node, acc)
            hx = (acc.get('HeaderExtraHex') or '').replace(' ', '').replace(':', '').upper()
            if hx:
                out.append((len(hx) // 2, hx, str(acc.get('Opcode') or acc.get('OpcodeName') or '?')))
        for v in node.values():
            scan(v, out)
    elif isinstance(node, list):
        for v in node:
            scan(v, out)


def main(argv):
    files = argv or glob.glob('Captures/*.tshark.json')
    recs = []
    for f in files:
        try:
            scan(json.load(open(f)), recs)
        except Exception as e:  # noqa: BLE001 - diagnostic tool, keep scanning
            print(f"  (skip {f}: {e})")

    bylen = collections.Counter(L for L, _, _ in recs)
    op4 = collections.defaultdict(set)
    for L, hx, op in recs:
        if L == 4:
            op4[op].add(hx)

    print("headerData by byte length:")
    for L in sorted(bylen):
        tag = "" if L in KNOWN else "   <-- UNEXPECTED (parser artifact, not a headerData case)"
        print(f"  {L:>2} bytes : {bylen[L]:>6}{tag}")

    print(f"\n4-byte (PlayerAgent method id): {len(op4)} distinct opcodes")
    ambiguous = {op: vs for op, vs in op4.items() if len(vs) > 1}
    print(f"  opcodes carrying >1 id (c2s/s2c reuse of one index): {len(ambiguous)}")
    for op, vs in sorted(ambiguous.items()):
        print(f"    {op}: {sorted(vs)}")

    thirteen = sorted({hx for L, hx, _ in recs if L == 13})
    print(f"\n13-byte (OverrideGameTweaks) as protobuf endpoint: {len(thirteen)} distinct")
    for hx in thirteen:
        p = parse_endpoint13(hx)
        if p:
            classid, f2, f4 = p
            print(f"  {hx}  -> class 0x{classid:08X} (PlayerInterface), field2={f2}, instance(field4)={f4}")
        else:
            print(f"  {hx}  -> does not parse as the endpoint form")

    ok = set(bylen) <= KNOWN
    print(f"\nAll lengths within the 4 known cases: {ok}")
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
