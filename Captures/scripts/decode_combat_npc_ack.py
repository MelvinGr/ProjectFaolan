#!/usr/bin/env python3
"""Decode the NPC counter-attack ANIMATION ack (the "second BccAck with actor = target handle").

Source: Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json
(the player is repeatedly hit by NPCs while leveling). See docs/combat.md "Known gaps".

Finding: when an NPC swings at the player, retail sends a BehaviorControlCenterIIR (0x642CD3D6) ack that is
structurally the SAME packet as the player's targeted-hit attack ack (SendNormalAttackResponse), but with the
roles SWAPPED:
  * top-level `actor` field  = the NPC handle           (player's ack puts the player here)
  * args.SpellTarget.handle  = the player handle        (the NPC's victim)
  * result-block target slot = the player handle        (player's ack puts the NPC here)
  * args has NO Mode field (field 2) - present only for player-initiated actions
  * seq is the constant 0x0004 (NPC acks are server-initiated, not echoing a client seq)

Wire body (inside the 0x2000 IirCarrier, after the u32 totalLen):
  [subop 0x642CD3D6][0x0000C350][actor=npcHandle][seq u16 = 0x0004][behaviorId u32 = 0x0F NormalAttack]
  [argsLen u32][args][result block]

args (protobuf, big-endian varints are standard LE base-128):
  f1  TypeId   - the NPC's NormalAttack behavior instance id (large, ~48800-49800; per-NPC + per-swing)
  f3  varint   - a small per-actor swing/animation counter (3..21 observed)
  f5  varint   - 24 (mostly) or 8 - animation variant
  f6  0
  f10 SpellTarget { f1 = 50000 (0xC350), f2 = the TARGET (player) handle }
  f11 varint   - animation param (18..76)
  f12 varint   - animation param (52..112)
  (NO f2/Mode - the player-ack distinguisher)

result block (the 81-byte tail of the common 132-byte frame):
  [u32 0][u32 0][u32 count=1]
  [6×u32 entry]                         (all 0 in the 132-byte case; extra records widen to 137/138/144)
  [u32 1][u32 0xC350][u32 target=player][u32 0]
  [u8 0x06]
  [u32 resultConst][u32 0][u32 k][u32 damage][u32 k][u32 0][u32 0]   (k = 1 or 3; resultConst 0xC9..0xCD = hit type)
"""
import json, struct, sys, glob

CAP = "Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json"
NORMAL_ATTACK = 0x0F


def rdvarint(b, o):
    sh = v = 0
    while True:
        c = b[o]; o += 1; v |= (c & 0x7F) << sh; sh += 7
        if not c & 0x80:
            return v, o


def parse_args(a):
    o = 0; out = {}
    while o < len(a):
        tag = a[o]; o += 1; f = tag >> 3; wt = tag & 7
        if wt == 0:
            out[f], o = rdvarint(a, o)
        elif wt == 2:
            ln, o = rdvarint(a, o); out[f] = a[o:o + ln]; o += ln
        else:
            raise ValueError(f"unexpected wiretype {wt} for field {f}")
    return out


def main():
    path = CAP
    matches = glob.glob(path) or glob.glob("Captures/" + path)
    if matches:
        path = matches[0]
    pkts = json.load(open(path))["Packets"]
    # player = most common upload sender instance
    from collections import Counter
    player = int(Counter(p.get("SenderInstance") for p in pkts
                         if not p["IsDownload"] and p.get("SenderInstance")).most_common(1)[0][0], 16)

    ok = bad = 0
    lens = Counter()
    for p in pkts:
        h = (p.get("BodyHex") or "").lower()
        if "642cd3d6" not in h or not p["IsDownload"]:
            continue
        b = bytes.fromhex(h); i = h.find("642cd3d6") // 2
        actor = struct.unpack(">I", b[i + 8:i + 12])[0]
        behid = struct.unpack(">I", b[i + 14:i + 18])[0]
        if behid != NORMAL_ATTACK or actor == player:
            continue  # player's own attacks handled elsewhere
        al = struct.unpack(">I", b[i + 18:i + 22])[0]
        args = b[i + 22:i + 22 + al]
        tail = b[i + 22 + al:]
        try:
            ar = parse_args(args)
            assert 2 not in ar, "NPC ack must NOT carry a Mode field"
            assert ar[10][:1] == b"\x08", "SpellTarget shape"
            st = parse_args(ar[10])
            assert st[1] == 0xC350 and st[2] == player, "SpellTarget targets the player"
            # result block must name the player as the hit target after the 0x06 marker
            assert struct.pack(">I", player) in tail, "result-block target = player"
            assert b"\x06" in tail, "0x06 result marker present"
            ok += 1
            lens[len(b)] += 1
        except Exception as e:
            bad += 1
            print(f"  FRAME {p['Frame']} FAILED: {e}\n    args={args.hex()}\n    tail={tail.hex()}")

    print(f"player handle = 0x{player:08x}")
    print(f"NPC-attack animation acks: {ok} parsed OK, {bad} failed")
    print(f"frame lengths: {dict(lens)}")
    return 0 if bad == 0 and ok > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
