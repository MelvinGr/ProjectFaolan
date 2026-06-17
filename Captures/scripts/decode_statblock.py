#!/usr/bin/env python3
"""Decode the 0x737A6DF9 tails' STAT block (the SnapshotTails PostAccessBlock) for from-scratch generation (#186).

The stat block is the last template-sourced region of BeachTailsTemplate.bin: 338 u32 values (markers
0x3F1,0x3F1, blockType 0x46F2) - a per-character stat sheet with a repeated sub-block (indices 35..182
copied to 183..330). To GENERATE it for a fresh character instead of carrying the .bin, we need to know
which values are character-derived vs a fixed skeleton.

FINDING (diff across 10 fresh L1 character login snapshots - Strie/combat/xxx/casilda/loopback...): only
**25 of 338** indices vary; the other 313 are a FIXED fresh-character SKELETON. The 25 categorize as:

  CLASS/ARCHETYPE (from CharacterMaxStats):
    [78] archetype (Rogue=3, Mage=2)   <-- WRONG, see correction below
    [21]/[56] HP base ; [23]/[54] current HP ; [64] current mana ; [66] stamina base ; [68] current stamina

  *** CORRECTION 2026-06-17c (VERIFIED across FOUR archetypes' retail LOGIN snapshots + character-sheet
      screenshots; see the 4-way table in docs/handoff-2026-06-17-perclass-snapshot.md) ***
  Use the LOGIN snapshot, NOT create: at create the vitals are unsettled (Guardian create HP 100.3/Stam 11.45
  vs login HP 130/Stam 178.5). Per-index the ONLY class-dependent slots are vitals + session + a Soldier-only
  armor-ish slot - NO class enum. Displayed-sheet vitals map (each x100, + a +148 repeat copy):

    HP      = [23] (=[54], rep [202])    Guardian 13000=130, Rogue 9800=98, Mage 8900=89, BearSh 10800=108
    HP2     = [21] (=[56], rep [204])    a lower secondary HP (120 / 90 / 80 / 100)
    MANA    = [60] = [64] (rep 208/212)  Necro 24600=246, BearSh 17100=171   (Soldier/Rogue = 0)  <- DISPLAYED
    mana2   = [62] (rep [210])           ~6 lower than displayed (Necro 24000, BearSh 16500)  - NOT the sheet value
    STAMINA = [68] (rep [216])           Guardian 17850=179, Rogue 23600=236, BearSh 17800=178  (Mage ~0)
    stam2   = [66] (rep [214])           lower secondary (172.5 / 230 / 172)
    [92] (rep [240]) Soldier-only (Guardian 12060, others 1000)   [94]/[242] session

  Correcting the earlier (wrong) notes:
  - MANA *IS* in the stat block (the "24600 nowhere" was a search error). DISPLAYED max mana = [60]/[64]
    (Necro 24600=246 matches the sheet); [62] is a ~6-lower secondary. So the C# CurrentMana=64 was CORRECT
    all along - an earlier "fix" to 62 (based on an UNSETTLED create snapshot) was wrong and was reverted.
  - [78] is NOT archetype: every archetype has [78]=3.
  - The CLASS LABEL / weapon proficiency (#239) is NOT in this snapshot AT ALL. PROVEN: (a) the two create-
    editor snapshots (Necro vs Assassin, both 8082 B, byte-aligned) differ in only 24 bytes (instance ids);
    (b) 4 archetypes' stat blocks differ only in vitals magnitudes, no enum; (c) the front protobuf root is
    ProtoFullCharacterIIR (inventory + account flags only - NO class field). #239's class source is a SEPARATE
    packet (CoreCharacterIIR / self-spawn / stat-update), still to be located.
  XP:        [80]
  MAP (playfield token + position floats - already handled by TryReplacePlayfieldToken / char position):
    [17] playfield token ; [104] (~1.5e9 id) ; [134]/[136] (floats, 0 / 0xF0010000)
  SESSION / per-char (varies even between one char's own sessions; not a stable stat): [94]
  (each repeated-block index also varies at +148: 54->202, 56->204, 64->212, 66->214, 68->216, 78->226,
   80->228, 94->242, 104->252, 134->282, 136->284.)

So generation = the 313-value fixed skeleton (the fresh-char baseline, same for every new character) + patch
the ~14 distinct varying slots from {class/level via CharacterMaxStats, XP, map token/position}. Most are
already computed by BeachTailsBuilder. Run: python3 Captures/scripts/decode_statblock.py
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


def necro_vs_strie():
    """Diff the retail Necro (Mage) world-entry stat block against the Strie (Rogue) login stat block, to show
    exactly which slots are class-derived. Confirms vitals are class-dependent and there is no class enum."""
    def block(f, idx):
        b = binascii.unhexlify(json.load(open(f))["Packets"][idx]["BodyHex"])
        # Use the FULL 12-byte marker (0x3F1,0x3F1,0x46F2): the necro world-entry packet has an earlier
        # 0x3F1,0x3F1 pair inside the access-window region that the 2-marker regex would mismatch.
        m = re.search(b"\x00\x00\x03\xf1\x00\x00\x03\xf1\x00\x00\x46\xf2", b)
        o, vals = m.start() + 12, []
        while o + 4 <= len(b):
            vals.append(u32(b, o)); o += 4
        return vals
    necro = block("Captures/Stickienecro_mage-necromancer_create.tshark.json", 528)
    strie = block("Captures/Strie_rogue-assassin_login.tshark.json", 52)
    print(f"necro(Mage) len={len(necro)}  strie(Rogue) len={len(strie)}")
    for i in range(min(len(necro), len(strie))):
        if necro[i] != strie[i]:
            print(f"  [{i:3d}] necro={necro[i]:>12} (0x{necro[i]:08x})  strie={strie[i]:>12} (0x{strie[i]:08x})")


def main():
    if "--necro-vs-strie" in __import__("sys").argv:
        necro_vs_strie()
        return
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
