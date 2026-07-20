#!/usr/bin/env python3
"""Pinpoint the DEAD-STATE field in the 0x737A6DF9 world-entry FullPlayerChar snapshot (#214).

Context: the in-client release/resurrect window is gated on a
client-side LOCAL-PLAYER dead-flag (DAT_0297d374->vtable[0x10]()). Live gdb proved our death PACKETS
(SimpleCharKilledBy + ApplySpell + death-pose MoveTask + 0x2024) are byte-correct and the 0x2024 handler
reaches its window-feed SUCCESS path - yet the window stays hidden because that flag is false. The retail
`login-while-dead` capture carries the dead state with NO SimpleCharKilledBy and NO ApplySpell at all, so the
dead-state must live in the world-entry SNAPSHOT we already generate.

FINDING (this script): the dead-state field is the **current-health stat** in the tails STAT block:

    statId 0x001B (= 27, LegacyCharacterStatId.HealthCurrent) value = 0

The stat block is the SnapshotTails PostAccessBlock - a 0x3F1/0x3F1/0x46F2-framed array read as [statId,value]
pairs (the positional TailsPostAccessBlock.Values index = the pair element index, so stat 27's VALUE lands at
flat index 23 = CharacterValueIndex.CurrentHpExtra, with repeated-block copies at [54]/[202]). In the dead
capture HealthCurrent = 0 while HealthMax-side slots stay full; in EVERY alive login HealthCurrent = full HP.

This maps 1:1 onto TailsPostAccessBlock.CurrentHp (Values[23]/[54], +[202]). So a dead login snapshot =
the normal snapshot with CurrentHp patched to 0 (hpBase [21]/[56] left at max). The client constructs the
local-player SimpleChar from the snapshot with health 0 -> the dead-flag is true at construction -> the
window shows. (Mid-session this same health=0 via a StatUpdate does NOT re-flip the latched flag - that is
the separate in-session setter, handoff step 3.)

Run:  python3 tools/capture/decode_death_snapshot.py
"""
import glob
import json
import re
import struct
import sys

HEALTH_CURRENT = 0x001B   # LegacyCharacterStatId.HealthCurrent (statId 27)
HEALTH_MAX = 0x0001       # LegacyCharacterStatId.HealthMax
# stat-block frame: two empty 0x3F1 StatList counts then the 0x46F2 block-type word.
STAT_MARKER = re.compile(b"\x00\x00\x03\xf1\x00\x00\x03\xf1\x00\x00\x46\xf2")

DEAD = "Captures/retail_login-while-dead_resurrect-window.tshark.json"


def snapshot_body(path):
    try:
        doc = json.load(open(path))
    except Exception:
        return None
    pk = doc["Packets"] if isinstance(doc, dict) else doc
    for p in pk:
        bh = (p.get("BodyHex") or "")
        if bh[8:16].lower() == "737a6df9":
            return bytes.fromhex(bh)
    return None


def stat_pairs(b):
    """Return {statId: value} from the tails stat block (first id wins, like the client's map insert)."""
    m = STAT_MARKER.search(b)
    if not m:
        return None
    o = m.start() + 12
    vals = []
    while o + 4 <= len(b):
        vals.append(struct.unpack_from(">I", b, o)[0])
        o += 4
    d = {}
    for i in range(0, len(vals) - 1, 2):
        d.setdefault(vals[i], vals[i + 1])
    return d, vals


def report(path, tag):
    b = snapshot_body(path)
    if not b:
        print(f"  {tag:5} {path.split('/')[-1][:46]:46} -- no snapshot")
        return None
    r = stat_pairs(b)
    if not r:
        print(f"  {tag:5} {path.split('/')[-1][:46]:46} -- no stat block")
        return None
    d, vals = r
    cur = d.get(HEALTH_CURRENT)
    mx = d.get(HEALTH_MAX)
    state = "DEAD" if cur == 0 else "alive"
    print(f"  {tag:5} {path.split('/')[-1][:46]:46} HealthCurrent(0x1B)={str(cur):>6}  "
          f"HealthMax(0x01)={str(mx):>6}  -> {state}")
    return cur


def main():
    print("=== dead-state field: tails stat block HealthCurrent (statId 0x1B) ===\n")
    print("Reference (the retail login-while-dead capture - the window shows):")
    dead = report(DEAD, "DEAD")
    assert dead == 0, "expected HealthCurrent == 0 in the login-while-dead snapshot"

    print("\nEvery normal (alive) login snapshot - HealthCurrent is full HP, never 0:")
    n_alive = 0
    for f in sorted(glob.glob("Captures/*login*.tshark.json")):
        if "while-dead" in f:
            continue
        cur = report(f, "live")
        if cur is not None and cur != 0:
            n_alive += 1
    print(f"\nOK: HealthCurrent==0 ONLY in the dead snapshot ({n_alive} alive snapshots all non-zero).")
    print("Dead login snapshot = normal snapshot with TailsPostAccessBlock.CurrentHp patched to 0.")


if __name__ == "__main__":
    sys.argv[1:] and [report(a, "?") for a in sys.argv[1:]] or main()
