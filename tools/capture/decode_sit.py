#!/usr/bin/env python3
"""Decode the bench SIT / STAND interaction from
Captures/NecroL80_mage-necromancer_login_path-of-azura_sit-bench.tshark.json.

Sitting on a bench is a server-originated BehaviorControlCenterIIR (0x642CD3D6) ANIMATION subop broadcast on the
PLAYER entity (the same generic behavior channel as spellweave poses / combat swings). The trigger is the client's
BCC interact on the bench (typetag 0xC878, action 6); standup is triggered by the player MOVING (not a 2nd click).

BCC animation framing (shared with BehaviorAnimationCodec):
  [op 642cd3d6][0000c350][actor:4][seq:u16=0004][behaviorId:u32=0x2a][argsLen:u32=20][args][tail]
  args (20B) = protobuf {f1=animActionId, f3=<p3>, f5=8, f6=<p6>, f10=Target{f1=class 0xC878, f2=bench handle}}
  tail       = [u32 a][u16 strLen][animName][u32 b][float blend][5B pad][u8 flag]<remoteformat ...>[u32 trailer]

Discovered per-animation constants (byte-exact vs F2795 sit / F2840 stand):
  SIT   : animActionId=42052 p3=10 p6=0  animName="chair_sit_calm" a=2 b=0x86 blend=2.6153848 flag=0xAA trailer=0x4C
  STAND : animActionId=42053 p3=12 p6=20 animName="standup"        a=0 b=0     blend=0.0       flag=0x84 trailer=0x01
  animActionId sits in the same table as spellweave trance (42041) — an ANIMATION-action id, NOT a session counter.

Run: python3 tools/capture/decode_sit.py
"""
import json, binascii, struct, os, sys

CAP = os.path.join(os.path.dirname(__file__), "..",
                   "NecroL80_mage-necromancer_login_path-of-azura_sit-bench.tshark.json")


def load():
    d = json.load(open(CAP))
    return d if isinstance(d, list) else next((v for v in d.values() if isinstance(v, list)), [])


def vi(b, o):
    r = s = 0
    while True:
        x = b[o]; o += 1; r |= (x & 0x7f) << s; s += 7
        if not x & 0x80:
            return r, o


def asc(b):
    return "".join(chr(c) if 32 <= c < 127 else "." for c in b)


def decode(b, name):
    print("=" * 78)
    print(f"{name}  ({len(b)} bytes)")
    assert b[4:8].hex() == "642cd3d6", "not a BCC record"
    actor = int.from_bytes(b[12:16], "big")
    seq = int.from_bytes(b[16:18], "big")
    behaviorId = int.from_bytes(b[18:22], "big")
    argsLen = int.from_bytes(b[22:26], "big")
    args = b[26:26 + argsLen]
    tail = b[26 + argsLen:]
    print(f"  actor=0x{actor:08x} seq=0x{seq:04x} behaviorId=0x{behaviorId:x} argsLen={argsLen}")
    # args protobuf
    o = 0
    while o < len(args):
        tag = args[o]; o += 1; f = tag >> 3; wt = tag & 7
        if wt == 0:
            v, o = vi(args, o); print(f"    f{f}={v}")
        elif wt == 2:
            ln = args[o]; o += 1; sub = args[o:o + ln]; o += ln
            so = {}; oo = 0
            while oo < len(sub):
                t = sub[oo]; oo += 1; vv, oo = vi(sub, oo); so[t >> 3] = vv
            print(f"    f{f}=Target{{class=0x{so.get(1,0):x}, handle=0x{so.get(2,0):x}}}")
    # tail
    a = int.from_bytes(tail[0:4], "big")
    slen = int.from_bytes(tail[4:6], "big")
    animName = tail[6:6 + slen].decode("latin1")
    p = 6 + slen
    bconst = int.from_bytes(tail[p:p + 4], "big")
    blend = struct.unpack(">f", tail[p + 4:p + 8])[0]
    flag = tail[p + 13]
    rf_start = tail.find(b"<remoteformat")
    rf_end = tail.find(b"</remoteformat>") + len("</remoteformat>")
    trailer = int.from_bytes(tail[rf_end:rf_end + 4], "big")
    print(f"    tail.a={a} animName={animName!r} tail.b=0x{bconst:x} blend={blend} flag=0x{flag:02x} trailer=0x{trailer:x}")
    print(f"    remoteformat: {asc(tail[rf_start:rf_end])}")


def main():
    recs = load()
    found = 0
    for r in recs:
        bh = r.get("BodyHex") or ""
        if "642cd3d6" not in bh:
            continue
        b = binascii.unhexlify(bh)
        t = b.decode("latin1", "ignore")
        if "chair_sit_calm" in t:
            decode(b, f"SIT   F{r['Frame']} {r['Direction']}"); found += 1
        elif "standup" in t and "<remoteformat" in t:
            decode(b, f"STAND F{r['Frame']} {r['Direction']}"); found += 1
    if not found:
        print("no sit/stand records found", file=sys.stderr); sys.exit(1)


if __name__ == "__main__":
    main()
