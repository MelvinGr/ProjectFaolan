#!/usr/bin/env python3
"""Prove the retail NPC-death animation sequence (#204/#349).

A dying NPC is NOT killed with SimpleCharKilledByUpdateIIR (0xB066AFC2) — that record is player-death-only
(it drives the local release/resurrect UI). Retail plays an NPC's death animation with a two-record pair on
the dying handle, right after the killing-blow StatDamage takes its health stat (0x0A) to 0:

    1. death-pose MoveTaskIIR   0x96C46740  movement-type 0x06  at the death position  (freezes the corpse)
    2. death BehaviorControlCenterIIR 0x642CD3D6  form 0x0004, slot 0x0000001F  (= the death behavior)

The DEATH is identified by the BccIIR ENVELOPE — form 0x0004 in slot 0x0000001F with a 13-byte protobuf of
shape `08 <behaviorId varint> 18 <variant> 28 08 30 00 60 <f12 varint>`. The **behaviorId is creature-specific**
(the creature's own death animation): 41877 for the common humanoid mobs here, 17036 for the F023 creature. The
field-3 `<variant>` byte varies per kill (hit-direction/ragdoll selector). Our `WorldDeath.BuildDeathBcc` sends
the common 41877 + a representative variant, which is correct for humanoid NPCs; per-creature death behaviorIds
need per-NPC behavior data we don't yet carry (follow-up).

Carrier framing (one record per captured packet, `BodyHex`):
    [u32 len][u32 subop][u32 0xC350][u32 handle][u16 form][u32 slot][u32 pblen][pb...]

This validator finds every StatDamage that zeroes an NPC's health stat (0x0A) in the casting-combat capture and
asserts each dying handle gets the death-pose MoveTask + a form-4 slot-0x1F death BccIIR (reporting its
behaviorId), and that NO SimpleCharKilledBy is sent for it. Mirrors WorldDeath.BuildDeathPose /
BuildDeathBcc(handle, NpcDeathVariant) + WorldDeathTests.

Run: python3 tools/capture/decode_npc_death.py
"""
import json
import struct
import sys
import sys as _sys, pathlib as _pl
_sys.path.insert(0, str(_pl.Path(__file__).resolve().parent))
from captures import capture_files, capture  # corpus lives in the ProjectFaolan-Data repo

CAP = capture("NecroL80_mage-necromancer_login_casting-combat.tshark.json")

STATDAMAGE = "e9f24184"
MOVETASK = "96c46740"
DEATHBCC = "642cd3d6"
KILLEDBY = "b066afc2"
DEATH_SLOT = 0x0000001F   # the BccIIR slot the death behavior lands in (form 0x0004)
HEALTH_STAT = 0x0A


def u32(b, o):
    return struct.unpack(">I", b[o:o + 4])[0]


def u16(b, o):
    return struct.unpack(">H", b[o:o + 2])[0]


def varint(b, o):
    v = s = 0
    while True:
        x = b[o]
        o += 1
        v |= (x & 0x7F) << s
        if not x & 0x80:
            return v, o
        s += 7


def death_behavior_id(b):
    """behaviorId of a form-4, slot-0x1F death BccIIR, else None."""
    if len(b) < 26 or u16(b, 16) != 0x0004 or u32(b, 18) != DEATH_SLOT:
        return None
    pb = 26
    if b[pb] != 0x08:   # protobuf field 1 (behaviorId)
        return None
    return varint(b, pb + 1)[0]


def records(path):
    doc = json.load(open(path))
    out = []
    for p in doc["Packets"]:
        b = bytes.fromhex(p.get("BodyHex") or "")
        if len(b) < 16:
            continue
        out.append((b[4:8].hex(), u32(b, 12), b, p.get("BodyHex")))
    return out


def kills(recs):
    """StatDamage records whose statValue (word 0) is 0 for the health stat (word 7 == 0x0A)."""
    for i, (sub, handle, b, _) in enumerate(recs):
        if sub != STATDAMAGE or len(b) < 17 + 17 * 4:
            continue
        words = [u32(b, 17 + 4 * k) for k in range(17)]
        if words[0] == 0 and words[7] == HEALTH_STAT:
            yield i, handle


def main():
    recs = records(CAP)
    kill_list = list(kills(recs))
    assert kill_list, "no NPC kills found in the capture"
    print(f"=== {len(kill_list)} NPC kills in {CAP.split('/')[-1]} ===\n")

    ok = True
    for kp, handle in kill_list:
        pose = False
        behavior = None
        killedby = False
        for sub, h, b, hexs in recs[kp:kp + 120]:
            if h != handle:
                continue
            if sub == MOVETASK and b[-1] == 0x06:
                pose = True
            if sub == DEATHBCC and behavior is None:
                behavior = death_behavior_id(b)
            if sub == KILLEDBY:
                killedby = True
        state = "OK" if (pose and behavior is not None and not killedby) else "??"
        if state != "OK":
            ok = False
        bid = f"behaviorId {behavior}" if behavior is not None else "NO"
        print(f"  [{state}] handle 0x{handle:08X}  death-pose={'yes' if pose else 'NO'}  "
              f"death-bcc(slot 0x1F)={bid}  KilledBy={'PRESENT!' if killedby else 'absent'}")

    n_killedby = sum(1 for sub, *_ in recs if sub == KILLEDBY)
    print(f"\nSimpleCharKilledBy (0xB066AFC2) in the whole capture: {n_killedby} (player-death only; 0 for a pure NPC-combat trace).")
    assert n_killedby == 0, "expected NO SimpleCharKilledBy in a pure NPC-combat capture"
    print("OK — every NPC kill = death-pose MoveTask(0x06) + form-4 slot-0x1F death BccIIR, no KilledBy." if ok
          else "MISMATCH — see ?? rows above.")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
