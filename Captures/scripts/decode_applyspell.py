#!/usr/bin/env python3
"""Decode the ApplySpell (0x6B87E316 = MagicBuffIIR) effect payload (#232).

The payload is the 54 bytes after applyFrom in an ApplySpell carrier
([len][subop][c350][applyTo][u16][spellId][c350][applyFrom][PAYLOAD]). It is the
MagicBuffIIR effect descriptor the client reads in MagicBuffIIR_c::ReadSubClass
(FUN_007a12a0, Ghidra: Captures/ghidra/DumpMagicBuffReader.java) and logs as
"UpdateBuff with parameters: BuffTemplate ..." - so it is a BUFF/effect record,
NOT a raw damage value (the HP change rides a separate vitals StatUpdate).

Field map recovered by the reader grammar + an empirical diff across same-spell
instances (per-cast fields) vs different spells (spell params):

    @0-7    const 0
    @8-11   u32 MAGNITUDE        - per-SPELL (0x4b0/0x2bc/0x190 = 1200/700/400); the effect strength
    @12-19  const framing (@17=0x01)
    @20-26  the echoed spell id  (e.g. 31 6a a6 ..)
    @28-31  rank/element         (@29, small per-spell value)
    @32-43  const 0
    @44-47  small (@45 per-cast)
    @48-53  u48 per-cast TIMESTAMP/counter (the only steadily-incrementing field across instances)

The rank-1 (xx01) twin of each cast carries an EMPTY payload = the buff-end /
effect-landed marker (the same apply-then-end shape as the mount buff).

Run: python3 Captures/scripts/decode_applyspell.py
"""
import json, struct, glob
from collections import defaultdict

SUB = 0x6B87E316
MAGNITUDE_OFFSET = 8


def npc_casts():
    out = defaultdict(list)
    for path in sorted(glob.glob('Captures/*.tshark.json')):
        try:
            d = json.load(open(path))
        except Exception:
            continue
        for p in d['Packets']:
            b = bytes.fromhex(p.get('BodyHex', '') or '')
            if len(b) < 30 or struct.unpack('>I', b[4:8])[0] != SUB:
                continue
            applyTo = struct.unpack('>I', b[12:16])[0]
            spellId = struct.unpack('>I', b[18:22])[0]
            applyFrom = struct.unpack('>I', b[26:30])[0]
            ln = struct.unpack('>I', b[:4])[0]
            payload = b[30:4 + ln]
            if len(payload) == 54 and applyTo != applyFrom and applyFrom < 0x1000000:
                out[spellId].append(payload)
    return out


def magnitude(payload):
    return struct.unpack('>I', payload[MAGNITUDE_OFFSET:MAGNITUDE_OFFSET + 4])[0]


def main():
    casts = npc_casts()
    print(f"{sum(len(v) for v in casts.values())} NPC-cast payloads, {len(casts)} distinct spells\n")
    print("spell        magnitude  echoed-id-ok  instances")
    print("-" * 52)
    for spellId, payloads in sorted(casts.items()):
        mags = {magnitude(p) for p in payloads}
        # the echoed spell id (@20..27 region) carries the base; check the base matches the cast spell base.
        base = spellId >> 8
        echoed_ok = all(bytes([(base >> 16) & 0xff, (base >> 8) & 0xff, base & 0xff]) in p for p in payloads)
        assert len(mags) == 1, f"magnitude not constant for {spellId:#x}: {mags}"
        print(f"{spellId:#011x}  {next(iter(mags)):>9}  {str(echoed_ok):>11}  x{len(payloads)}")


if __name__ == '__main__':
    main()
