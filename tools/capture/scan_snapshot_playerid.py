#!/usr/bin/env python3
"""What does the FullChar snapshot's PlayerId stat (0x25F) track - the character, or the account? (Gitea #478)

The snapshot's stat sheet declares `PlayerId` among the values our SubClassStatBlock carries as a "fixed
default", so every character we create ships one captured character's id. It is not fixed: the slot varies.
This scans the WHOLE capture corpus - not the tests' corpus extract, which is a derived slice - and groups the
observed ids by capture, whose file names carry the account and character.

Reads the stat table PAIR-ALIGNED from StatList#1's count word rather than scanning the body for the id: the
flat scan matches ordinary data (see SubClassPostAccessBlock).

Run:  python3 tools/capture/scan_snapshot_playerid.py
"""
import json
import os
import struct
import sys
from collections import defaultdict

sys.path.insert(0, __file__.rsplit('/', 1)[0])
from captures import capture_files  # noqa: E402

SNAPSHOT = 0x737A6DF9
FRAME = 0x3F1
STATLIST1_COUNT = 18 * FRAME  # (17 displayed stats + 1) * 0x3F1 = 0x46F2
PLAYER_ID = 0x25F
LEVEL = 0x36


def snapshot_bodies(packets):
    for p in packets:
        if not (p.get('IsDownload') and p.get('Opcode') == '0x2000'):
            continue

        b = bytes.fromhex(p.get('BodyHex') or '')
        if len(b) < 8:
            continue

        length, iir = struct.unpack_from('>II', b, 0)
        if iir == SNAPSHOT and length == len(b) - 4:
            yield b


def stat_pairs(body):
    """Every [statId][value] pair of the two StatList_t, found from StatList#1's count word."""
    for i in range(0, len(body) - 4, 1):
        if struct.unpack_from('>I', body, i)[0] != STATLIST1_COUNT:
            continue

        at = i + 4
        pairs = []
        # StatList#1: 17 pairs. Then StatList#2's own count word, then that many pairs (it carries both copies).
        for _ in range(17):
            if at + 8 > len(body):
                return pairs
            pairs.append(struct.unpack_from('>II', body, at))
            at += 8

        if at + 4 <= len(body):
            frame = struct.unpack_from('>I', body, at)[0]
            at += 4
            if frame % FRAME == 0 and frame > 0:
                for _ in range(frame // FRAME - 1):
                    if at + 8 > len(body):
                        break
                    pairs.append(struct.unpack_from('>II', body, at))
                    at += 8

        return pairs

    return []


def main():
    by_id = defaultdict(set)
    by_capture = defaultdict(set)
    snapshots = 0
    files = 0

    for path in capture_files():
        name = os.path.basename(path)
        try:
            with open(path) as handle:
                packets = json.load(handle)['Packets']  # the dump is {"Packets": [...]}, not a bare list
        except Exception as e:                      # noqa: BLE001 - a broken dump should name itself, not stop the sweep
            print(f"  !! {name}: {e}")
            continue

        files += 1
        for body in snapshot_bodies(packets):
            pairs = stat_pairs(body)
            ids = [v for (sid, v) in pairs if sid == PLAYER_ID]
            if not ids:
                continue

            snapshots += 1
            for value in set(ids):
                by_id[value].add(name)
                by_capture[name].add(value)

    print(f"files={files} snapshots={snapshots} distinct PlayerIds={len(by_id)}\n")
    for value, captures in sorted(by_id.items(), key=lambda kv: -len(kv[1])):
        print(f"0x{value:08X} ({value})  {len(captures)} capture(s)")
        for name in sorted(captures):
            print(f"    {name}")

    mixed = {k: v for k, v in by_capture.items() if len(v) > 1}
    print(f"\ncaptures carrying MORE than one PlayerId: {len(mixed)}")
    for name, values in mixed.items():
        print(f"    {name}: {[hex(v) for v in values]}")


if __name__ == '__main__':
    main()
