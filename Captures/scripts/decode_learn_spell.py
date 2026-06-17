#!/usr/bin/env python3
"""Decode DispatchLearnSpell (s2c 0x200A) - the live spell-grant packet (#73).

The retail "learn a spell" grant that the opcode RE flagged long ago. Rides the GameCharInterface
dispatch conversation (the same class pair as DispatchAddSpecialAbility 0x2052: sender = GameCharAgent
type 0xA9ECB95D / sub-id 0x58, receiver = GameCharInterface type 0x8B87F791 + the client instance).

FINDING: the body is a single big-endian u32 - the spell id (`i_nSpellID`), nothing else. Invariant
across ALL 68 real LearnSpell packets in the captures (every one is exactly 4 bytes). The bare opcode
0x200A also appears on OTHER classes (CharacterSearch, and tiny 1-3 byte bodies on the world/0x5B9756FC
conversations) - those are opcode collisions, filtered out here by the GameCharAgent sender class.

The sibling grant/forget ops the RE recovered (DispatchForgetSpell 0x200B, DispatchRemoveSpecialAbility
0x2053, DispatchUntrainSingleFeat 0x2011, DispatchPassiveAbilityEquip/Unequip) have ZERO packets on this
class in any capture, so they have no byte-exact oracle and are deferred (decode-first).

Run: python3 Captures/scripts/decode_learn_spell.py
"""
import json, glob, struct

GAMECHARAGENT_TYPE = "0xa9ecb95d"  # sender class of the s2c GameCharInterface grants


def learn_spell_packets():
    for f in sorted(glob.glob("Captures/*.tshark.json")):
        try:
            packets = json.load(open(f))["Packets"]
        except Exception:
            continue
        for p in packets:
            if ((p.get("Opcode") or "").lower() == "0x200a" and p.get("IsDownload")
                    and (p.get("SenderClass") or "").lower() == GAMECHARAGENT_TYPE):
                yield f.split("/")[-1], p["Frame"], bytes.fromhex(p["BodyHex"])


def main():
    n = 0
    ids = []
    for _, _, body in learn_spell_packets():
        n += 1
        assert len(body) == 4, f"LearnSpell body is not a bare u32 (len {len(body)})"
        ids.append(struct.unpack(">I", body)[0])
    print(f"{n} DispatchLearnSpell packets on the GameCharAgent class; every body is a 4-byte u32 spell id.")
    print(f"distinct spell ids: {len(set(ids))}; sample: {[hex(i) for i in sorted(set(ids))[:12]]}")
    assert n > 0, "no LearnSpell packets found"
    print("OK: DispatchLearnSpell 0x200A body == [u32 spellId].")


if __name__ == "__main__":
    main()
