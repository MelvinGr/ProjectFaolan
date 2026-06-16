#!/usr/bin/env python3
"""Decode the 0x737A6DF9 tails' STAT block (the SnapshotTails PostAccessBlock) for from-scratch generation (#186).

The stat block is the last template-sourced region of BeachTailsTemplate.bin: 338 u32 values (markers
0x3F1,0x3F1, blockType 0x46F2) - a per-character stat sheet with a repeated sub-block (indices 35..182
copied to 183..330). To GENERATE it for a fresh character instead of carrying the .bin, we need to know
which values are character-derived vs a fixed skeleton.

FINDING (diff across 10 fresh L1 character login snapshots - Strie/combat/xxx/casilda/loopback...): only
**25 of 338** indices vary; the other 313 are a FIXED fresh-character SKELETON. The 25 categorize as:

  CLASS/ARCHETYPE (from CharacterMaxStats):
    [78] archetype (Rogue=3, Mage=2)
    [21]/[56] HP base ; [23]/[54] current HP ; [64] current mana ; [66] stamina base ; [68] current stamina
  XP:        [80]
  MAP (playfield token + position floats - already handled by TryReplacePlayfieldToken / char position):
    [17] playfield token ; [104] (~1.5e9 id) ; [134]/[136] (floats, 0 / 0xF0010000)
  SESSION / per-char (varies even between one char's own sessions; not a stable stat): [94]
  (each repeated-block index also varies at +148: 54->202, 56->204, 64->212, 66->214, 68->216, 78->226,
   80->228, 94->242, 104->252, 134->282, 136->284.)

So generation = the 313-value fixed skeleton (the fresh-char baseline, same for every new character) + patch
the ~14 distinct varying slots from {class/level via CharacterMaxStats, XP, map token/position}. Most are
already computed by BeachTailsBuilder. Run: python3 Captures/decode_statblock.py
"""
import json, glob, binascii, struct, re

REPEAT_OFFSET = 148  # indices 35..182 are copied to 183..330


def u32(b, o):
    return struct.unpack_from(">I", b, o)[0]


def stat_block(b):
    m = re.search(b"\x00\x00\x03\xf1\x00\x00\x03\xf1", b)
    if not m:
        return None
    o = m.start() + 12
    vals = []
    while o + 4 <= len(b):
        vals.append(u32(b, o))
        o += 4
    return vals


def fresh_blocks():
    out = {}
    for f in sorted(glob.glob("Captures/*.tshark.json")):
        try:
            d = json.load(open(f))["Packets"]
        except Exception:
            continue
        for p in d:
            bh = (p.get("BodyHex") or "").lower()
            if bh[8:16] == "737a6df9":
                sb = stat_block(binascii.unhexlify(bh))
                if sb and len(sb) == 338:  # 338 == a fresh L1 character
                    out.setdefault(f.split("/")[-1].split("-character-")[-1][:18], sb)
    return out


def main():
    blocks = fresh_blocks()
    names = list(blocks)
    print(f"fresh-char (338-value) stat blocks: {len(names)}")

    varying = {i: [blocks[n][i] for n in names] for i in range(338)
               if len({blocks[n][i] for n in names}) > 1}
    print(f"varying indices: {len(varying)} of 338 (the other {338 - len(varying)} are a FIXED skeleton)")
    for i, vs in varying.items():
        print(f"  [{i:3d}] {vs}")

    assert len(varying) < 40, "expected a small set of character-derived slots"
    print("OK: the stat block is a 313-value fixed skeleton + a small set of char/class/map-derived slots.")


if __name__ == "__main__":
    main()
