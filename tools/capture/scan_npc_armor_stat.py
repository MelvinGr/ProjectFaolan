#!/usr/bin/env python3
"""Does retail ever send an NPC's ARMOUR to the client? (Gitea #401, the player->NPC half)

Player-side mitigation is wired: the client's own armour/protection reduce incoming damage. The other half -
a player's hit being reduced by the NPC's armour - is decided SERVER-side, so the question is whether the wire
carries the NPC's armour rating at all. If it never does, that half cannot be sourced from captures and needs a
modelled curve (like CombatResolver.DefaultNpcMaxHealth), which is a design decision rather than a decode.

Censuses the stat ids carried by the stat-update IIR (0x96B8DC59) across the whole corpus, and reports whether
Armor (448) / Protection (451) / PvpArmor (454) ever appear.

The spawn's own `generic_stats` array is the other place an NPC stat could hide; census that with
`dotnet run --project Faolan.Importer -- --report npc-generic-stats`, which decodes the protobuf properly.
Do NOT hand-scan the wire for the field tag - `\xb2\x01` occurs inside strings and reports ASCII as stat values,
which is how a first version of this script "found" runs of 105/108/111.

Run:  python3 tools/capture/scan_npc_armor_stat.py
"""
import json
import os
import struct
import sys
from collections import Counter

sys.path.insert(0, __file__.rsplit('/', 1)[0])
from captures import capture_files  # noqa: E402

STAT_UPDATE = 0x96B8DC59
ARMOUR_IDS = {448: 'Armor', 451: 'Protection', 454: 'PvpArmor', 450: 'BonusArmor', 334: 'BonusProtection'}


def stat_records(body):
    """[u32 len][u32 iir][u32 class][u32 handle][u8 count][count x ([u32 statId][u32 value])]."""
    if len(body) < 21:
        return

    length, iir = struct.unpack_from('>II', body, 0)
    if iir != STAT_UPDATE or length != len(body) - 4:
        return

    count = body[20]
    at = 21
    for _ in range(count):
        if at + 8 > len(body):
            return

        yield struct.unpack_from('>II', body, at)
        at += 8


def main():
    ids = Counter()
    armour_hits = []
    packets_seen = 0

    for path in capture_files():
        name = os.path.basename(path)
        try:
            with open(path) as handle:
                packets = json.load(handle)['Packets']
        except Exception as e:                      # noqa: BLE001
            print(f"  !! {name}: {e}")
            continue

        for p in packets:
            hexed = p.get('BodyHex')
            if not hexed or not p.get('IsDownload'):
                continue

            body = bytes.fromhex(hexed)
            found = list(stat_records(body))
            if found:
                packets_seen += 1

            for stat_id, value in found:
                ids[stat_id] += 1
                if stat_id in ARMOUR_IDS:
                    armour_hits.append((name, ARMOUR_IDS[stat_id], value))

    print(f"stat-update packets: {packets_seen}, distinct stat ids: {len(ids)}\n")
    print("top ids:")
    for stat_id, n in ids.most_common(15):
        print(f"  {stat_id:6} x{n}")

    print(f"\nARMOUR-family stats on the wire: {len(armour_hits)}")
    for name, stat, value in armour_hits[:20]:
        print(f"  {stat} = {value}   ({name})")




if __name__ == '__main__':
    main()
