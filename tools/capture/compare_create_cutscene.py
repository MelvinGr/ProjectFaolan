#!/usr/bin/env python3
"""Compare OUR create-world beach cutscene against retail, packet-by-packet.

The beach destiny cutscene is a call-and-RESPONSE on the world GameServer connection (see
create-flow reconstruct): the client REQUESTS each script group by id and the
server must ECHO back a matching StartGroup. This script extracts the ScryScriptIIR (0xA36D3B74) sequence in
BOTH directions from a tshark.json and prints the interleave, so a test run can be diffed against retail.

Usage:
  tools/capture/tshark_dump.sh /tmp/faolan_test.pcapng           # our loopback capture -> /tmp/faolan_test.tshark.json
  python3 tools/capture/compare_create_cutscene.py /tmp/faolan_test.tshark.json   # ours
  python3 tools/capture/compare_create_cutscene.py                                # retail (default)
"""
import json, struct, sys, os

RETAIL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..",
                      "Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json")
CAP = sys.argv[1] if len(sys.argv) > 1 else RETAIL
TYPE = {0: "StartGroup", 1: "StartGroupWait", 2: "Msg", 3: "StopGroup", 4: "RestartGroup",
        5: "Completed", 6: "PlayOrSkipQ", 7: "Preload", 8: "PlayResult", 9: "SkipResult"}


def rv(b, p):
    s = v = 0
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
        if wt == 0:
            v, p = rv(b, p)
        elif wt == 2:
            ln, p = rv(b, p); v = b[p:p + ln]; p += ln
        else:
            break
        out.setdefault(fn, []).append(v)
    return out


def main():
    d = json.load(open(CAP))
    pk = d["Packets"]
    print(f"ScryScript call-and-response in {os.path.basename(CAP)}:\n")
    print(f"  {'dir':4} {'type':12} {'token':20} group/int")
    print("  " + "-" * 50)
    for p in pk:
        body = bytes.fromhex(p.get("BodyHex") or "")
        if len(body) < 22 or struct.unpack(">I", body[4:8])[0] != 0xA36D3B74:
            continue
        pblen = struct.unpack(">I", body[17:21])[0]
        f = parse_pb(body[21:21 + pblen])
        typ = f.get(2, [None])[0]
        tok = f.get(4, [b""])[0]
        tok = tok.decode("latin1", "ignore") if isinstance(tok, (bytes, bytearray)) else (tok or "")
        i3 = f.get(3, [None])[0]
        d_ = "S2C" if p.get("IsDownload") else "C2S"
        extra = f"  int={i3}" if i3 else ""
        print(f"  {d_:4} {TYPE.get(typ, typ):12} {tok:20}{extra}")


if __name__ == "__main__":
    main()
