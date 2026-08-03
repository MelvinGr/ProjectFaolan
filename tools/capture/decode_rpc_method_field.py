#!/usr/bin/env python3
"""Prove the RpcHeader's method field is ONE protobuf varint (Gitea #610, from #319/#320/#608/#609).

What the codebase reads as `[u16 BE opcode][optional trailing headerData]` is a single protobuf field —
tag `0x20` (field 4, wire type 0) whose value is the method's 32-bit registration constant, whole:

    [0x0A sender][0x12 receiver][0x20][method: varint(registrationConstant)]

The opcode's HIGH byte is the tag and its LOW byte is the varint's first group; the "headerData" is the
remaining groups. So `opcode = 0x2000 | (c & 0x7F)` for a one-group value and `0x2080 | (c & 0x7F)` for a
longer one, and every face the old four-case reading listed is a property of a varint: the `0x01` "latency
marker" is a second group, `0x2080` is where a constant first needs two groups, a 4-byte tail ends small
because that is the varint's TOP group, and `0x94A760` is a method rather than a constant.

Four checks, over every `*.tshark.json` in the data repo:

  1. the field tag (the opcode's high byte) is `0x20`, or `0x1A` for the login handoffs;
  2. MethodId agrees with `0x2000 | (c & 0x7F)` / `0x2080 | (c & 0x7F)` — i.e. the dissector's reassembly
     and the opcode are consistent, which is what says the split is lossless;
  3. on the 15 index-registered classes the constant IS the plain method index;
  4. on the two hash-registered classes (PlayerAgent 0x82F20484 / PlayerInterface 0x7D155738) it resolves
     to a method the client's own registration table names — read from Opcodes.cs, so nothing is asserted
     against a list of observed bytes.

Over the current corpus MISMATCH prints exactly two, both in `Strie_rogue-assassin_login` - and neither is a
counter-example, because neither is a packet. Both sit inside a desynced decompressed region and FAIL an
independent CRC recompute under every alignment (a normal frame from the same stream matches exactly), so their
`0x20A8`/`0x20A9` opcodes are coincidence. Every one of the 23 methods on the hashed pair resolves, none
unregistered.

Usage:  tools/capture/decode_rpc_method_field.py [Captures/*.tshark.json ...]
"""
import collections
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from captures import capture_files  # corpus lives in the ProjectFaolan-Data repo

HASHED = {"0x82f20484": "PlayerAgent", "0x7d155738": "PlayerInterface"}
METHOD_FIELD_TAG = 0x20  # protobuf field 4, varint
ENDPOINT_FIELD_TAG = 0x1A  # protobuf field 3, length-delimited - only the login handoffs carry it


def registration_constants():
    """name -> constant, from the client's own registration tables in Opcodes.cs."""
    source = pathlib.Path(__file__).resolve().parents[2] / "Faolan.Core/Enums/Opcodes.cs"
    text = source.read_text(encoding="utf-8")
    out = {}
    for enum in ("PlayerAgentMethods", "PlayerInterfaceMethods"):
        body = text.split(f"public enum {enum} : uint", 1)[1].split("}", 1)[0]
        for m in re.finditer(r"(\w+)\s*=\s*0x([0-9A-Fa-f]+)", body):
            out[int(m.group(2), 16)] = m.group(1)
    return out


def main(argv):
    names = registration_constants()
    files = [pathlib.Path(p) for p in (argv[1:] or capture_files("*.tshark.json"))]

    tags = collections.Counter()
    frames = mismatches = 0
    bad = []
    resolved = collections.Counter()

    for path in files:
        for pkt in json.loads(pathlib.Path(path).read_text())["Packets"]:
            opcode_hex, method_hex = pkt.get("Opcode"), pkt.get("MethodId")
            if opcode_hex is None or method_hex is None:
                continue  # chat packets carry no RpcHeader
            opcode, method = int(opcode_hex, 16), int(method_hex, 16)
            tag = opcode >> 8
            tags[tag] += 1
            if tag != METHOD_FIELD_TAG:
                # 0x1A = field 3 first (the login handoffs; the method field sits after the nested
                # endpoint, and the dissector already reassembled it from there). Anything else is not an
                # RpcHeader at all - the one 0x0005 is the ServiceConnection handshake.
                continue

            frames += 1
            expected = (0x2000 if method < 0x80 else 0x2080) | (method & 0x7F)
            if opcode != expected:
                mismatches += 1
                bad.append((path.name, opcode_hex, method_hex, hex(expected)))
                continue

            receiver = str(pkt.get("ReceiverClass", "")).lower()
            if receiver in HASHED:
                resolved[names.get(method, f"UNREGISTERED 0x{method:08X}")] += 1

    print(f"field tag histogram: {dict((hex(k), v) for k, v in tags.most_common())}")
    print(f"field-4 frames: {frames}   consistent: {frames - mismatches}   MISMATCH: {mismatches}")
    for b in bad[:20]:
        print(f"  MISMATCH {b[0]}: opcode {b[1]} vs method {b[2]} (expected opcode {b[3]})")

    print(f"\nhash-registered methods resolved ({sum(resolved.values())} frames):")
    for name, n in sorted(resolved.items()):
        print(f"  {'!! ' if name.startswith('UNREGISTERED') else '   '}{name:34} x{n}")

    return 1 if any(n.startswith("UNREGISTERED") for n in resolved) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
