#!/usr/bin/env python3
"""Decode the ScryScript cutscene sequence (IIR 0xA36D3B74 = ScryScriptIIR_c) from a capture.

ScryScriptIIR is one of the 3 protobuf IIRs that map to a wire sub-opcode (docs/archive/opcode-re-plan.md Stage 5 #3).
The body is `ProtoScryScriptIIR` (Faolan.Core/Protobuf/aoc-client-protos.proto): a cutscene/scry script
control message. The create-world intro (TODO #7) is a sequence of these: PreloadAssets (carries the RDB resource
manifest the client must load!) -> PlayOrSkip -> StartScriptGroupOnClient -> ScriptMessage(ThisIsKalanthes/...).

Usage: decode_scryscript.py <capture.tshark.json>   (defaults to the dikketest create-world capture)
"""
import json, struct, sys, os

TYPE = {0: "StartScriptGroupOnClient", 1: "StartScriptGroupOnClientWait", 2: "ScriptMessage",
        3: "StopScriptGroupOnClient", 4: "RestartScriptGroupOnClient", 5: "ScriptCompleted",
        6: "PlayOrSkipQuestion", 7: "PreloadAssets", 8: "PlayOrSkipResultPlay", 9: "PlayOrSkipResultSkip"}

CAP = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "Dikketest_rogue-barbarian_create_random-actions.tshark.json")

def rv(b, p):
    s = 0; v = 0
    while True:
        c = b[p]; p += 1; v |= (c & 0x7f) << s
        if not c & 0x80:
            return v, p
        s += 7

def parse_pb(b):
    p = 0; out = {}
    while p < len(b):
        try:
            tag, p = rv(b, p)
        except Exception:
            break
        fn = tag >> 3; wt = tag & 7
        if fn == 0:
            break
        try:
            if wt == 0:
                v, p = rv(b, p)
            elif wt == 2:
                ln, p = rv(b, p); v = b[p:p + ln]; p += ln
            elif wt == 5:
                v = struct.unpack_from("<I", b, p)[0]; p += 4
            elif wt == 1:
                v = struct.unpack_from("<Q", b, p)[0]; p += 8
            else:
                break
        except Exception:
            break
        out.setdefault(fn, []).append((wt, v))
    return out, p

def looks_scry(d):
    return 2 in d and d[2][0][0] == 0 and d[2][0][1] in TYPE and 1 in d

def decode_scry(b):
    for off in range(0, min(len(b), 48)):
        d, end = parse_pb(b[off:])
        if looks_scry(d) and end >= len(b) - off - 2:
            return d
    return None

d = json.load(open(CAP))
print(f"ScryScript (0xA36D3B74) sequence in {os.path.basename(CAP)}:\n")
i = 0
for p in d["Packets"]:
    if not p.get("IsDownload") or (p.get("Opcode") or "").lower() != "0x2000":
        continue
    body = bytes.fromhex(p.get("BodyHex") or "")
    if len(body) < 8 or struct.unpack(">I", body[4:8])[0] != 0xA36D3B74:
        continue
    dd = decode_scry(body[8:])
    i += 1
    if not dd:
        print(f"#{i}: (unparsed)")
        continue
    t = TYPE[dd[2][0][1]]
    g = lambda k: (dd.get(k, [(0, b"")])[0][1])
    s1 = g(4); s1 = s1.decode("latin1", "ignore") if isinstance(s1, bytes) else s1
    s2 = g(5); s2 = s2.decode("latin1", "ignore") if isinstance(s2, bytes) else s2
    ip = g(3)
    extra = f" int={ip}" if ip else ""
    print(f"#{i:2} {t}{extra}")
    if s1:
        print(f"      s1: {s1}")
    if s2:
        print(f"      s2: {s2[:300]}{'...' if len(s2) > 300 else ''}")
