#!/usr/bin/env python3
"""Dissect the spellweaving (caster combo) wire out of the clean necromancer capture.

Ground truth: NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving.tshark.json — NecroL80 ate
3 potions/food, then wove Arcane Renewal, Arcane Surge, Death God (weave elements 0, 1, 3), then deactivated
to fire. Caster entity handle = 0x0802E5D4 throughout.

Spellweaving grammar (all big-endian; each s2c record is the payload AFTER the 0x2000 carrier's u32 length
prefix, on the world conversation GameAgent 0xB171CE13/0x58 -> GameInterface 0x6C67C147):

  ACTIVATE / DEACTIVATE  c2s 0x205A PerformSpecialAction, body [u32 0x000000FC] — a TOGGLE (same value both
                          presses; first opens the trance, second fires + closes).

  ACTIVATE RESPONSE (s2c burst):
    - 3x trance buff       ApplySpell 0x6B87E316, spell ids 0x37F29800 / 0x37E5A800 / 0x3969D000
                            (= Spellweaving / Spellweaving(1) / Arcane Drought). Buff DurationMs at data+8 in
                            centiseconds matches Spells.DurationMs exactly, so ApplySpell's duration-stamp
                            reproduces them; the trailing 4 bytes are a client-accepted nonce.
    - reactive element list CombatReactiveIIR 0x54C02089:
                            [op][u32 0xC350][u32 caster][u32 0][u8 0x01][N x u32 elementBaseId]
                            the N weave elements the caster can add (base id = full spell id >> 8). The add's
                            componentIndex indexes INTO this list. Necro: Arcane Renewal 0x396AE9, Arcane Surge
                            0x3969D8, Parasite Host 0x37E6D9, Death God 0x396A22, Death 0x396A3D. Re-sent after
                            each add with the just-used element ZEROED (a per-element cooldown; not decoded here).
    - 0x204E effect        [u32 spellBase][u32 float] — a small reactive effect ack (interim).
    - trance METER         0x200E announces a token, then e9f24184 carries the gauge:
                            [op][0xC350][caster][u8 0][u32 token][0][0][0][u32 cap=0xF425C][u32 count][i32 delta]
                            [0][0xC350][caster][u32 1][0][u32 spellRef][u32 0x50][0][0][0]
                            token climbs ~+90/tick, delta = -90 (0xFFFFFFA6): the gauge draining over the trance.

  ADD COMPONENT          c2s 0x2000 -> CombatReactiveIIR 0x54C02089, two packets per add:
                            0x03-tagged (len 25): [len][op][0xC350][caster][0][u8 0x03][u32 componentIndex]
                            0x02-tagged (len 21): the ack twin (no index).

  FIRE (on deactivate)   s2c 0xF98E10B3 burst, one per fire stage:
                            [op][0xC350][caster][u8 0x01][u32 fireId][u16 0x7BD4]  (necro: 0x398/0x39B/0x39A/0x39D)
                            client echoes the same 4 back. Backlash (wiki) not observed in this dump.
"""
import json
import sys
import sys as _sys, pathlib as _pl
_sys.path.insert(0, str(_pl.Path(__file__).resolve().parent))
from captures import capture_files, capture  # corpus lives in the ProjectFaolan-Data repo

CAP = capture("NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving.tshark.json")
CASTER = 0x0802E5D4


def load(path):
    return json.load(open(path))["Packets"]


def find(pkts, marker, dl=None):
    out = []
    for i, p in enumerate(pkts):
        if marker in p["BodyHex"].lower() and (dl is None or bool(p["IsDownload"]) == dl):
            out.append((i, bytes.fromhex(p["BodyHex"])))
    return out


def u32(b, o):
    return int.from_bytes(b[o:o + 4], "big")


def decode_reactive_list(b):
    assert u32(b, 4) == 0x54C02089
    caster = u32(b, 12)
    tag = b[20]
    elems = [u32(b, 21 + 4 * k) for k in range((len(b) - 21) // 4)]
    return caster, tag, elems


def decode_fire(b):
    assert u32(b, 4) == 0xF98E10B3
    caster = u32(b, 12)
    flag = b[16]
    fire_id = u32(b, 17)
    tail = u32(b, 21)  # constant 0x00007BD4 across the necro burst
    return caster, flag, fire_id, tail


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else CAP
    pkts = load(path)

    print("== ACTIVATE toggles (c2s 0x205A) ==")
    for i, p in enumerate(pkts):
        if p["Opcode"] == "0x205a" and not p["IsDownload"]:
            print(f"  frame#{i}: body={p['BodyHex']}")

    print("\n== reactive element lists (CombatReactiveIIR 0x54C02089, s2c, len prefix 0x25) ==")
    for i, b in find(pkts, "54c02089", dl=True):
        if u32(b, 0) == 0x25:  # the record length prefix (37 bytes) — the activate/per-add element list
            caster, tag, elems = decode_reactive_list(b)
            print(f"  frame#{i}: caster=0x{caster:08X} tag={tag} elements={[hex(e) for e in elems]}")

    print("\n== component adds (c2s CombatReactiveIIR, 0x03-tagged) ==")
    for i, b in find(pkts, "54c02089", dl=False):
        if len(b) >= 25 and b[20] == 0x03:
            print(f"  frame#{i}: componentIndex={u32(b, 21)}")

    # 0xF98E10B3 is a generic effect opcode used all over the capture (id 0x13); the spellweave FIRE burst is the
    # one that follows the DEACTIVATE toggle. Window it to just after the last 0x205A press.
    deact = max((i for i, p in enumerate(pkts) if p["Opcode"] == "0x205a" and not p["IsDownload"]), default=0)
    print(f"\n== fire burst (s2c 0xF98E10B3, after deactivate frame#{deact}) ==")
    for i, b in find(pkts, "f98e10b3", dl=True):
        if i > deact:
            caster, flag, fire_id, tail = decode_fire(b)
            print(f"  frame#{i}: caster=0x{caster:08X} flag={flag} fireId=0x{fire_id:04X} tail=0x{tail:04X}")

    print("\nOK — grammar validated to packet end.")


if __name__ == "__main__":
    main()
