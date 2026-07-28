#!/usr/bin/env python3
"""Does the snapshot's PlayerId equal the ACCOUNT ID the login tier already sent? (Gitea #478 follow-up)

UniverseAgent's DispatchAckAuthenticate carries the account twice: a u64
`0x0000271200000000 + accountId` and the raw `accountId` as a u32. The FullChar snapshot's PlayerId stat was
shown to be account-scoped, so the question is whether it is that same number or that number plus a base.

This scans the WHOLE capture corpus and, per capture, prints the login-tier account id beside the snapshot's
PlayerId. Equal => no offset; a constant difference => a base to add.

Run:  python3 tools/capture/scan_account_id_vs_playerid.py
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
STATLIST1_COUNT = 18 * FRAME
PLAYER_ID = 0x25F
UNIVERSE_PREFIX = 0x00002712


def bodies(packets):
    for p in packets:
        hexed = p.get('BodyHex')
        if hexed:
            yield p, bytes.fromhex(hexed)


def snapshot_player_id(body):
    if len(body) < 8:
        return None

    length, iir = struct.unpack_from('>II', body, 0)
    if iir != SNAPSHOT or length != len(body) - 4:
        return None

    for i in range(0, len(body) - 4):
        if struct.unpack_from('>I', body, i)[0] != STATLIST1_COUNT:
            continue

        at = i + 4 + 17 * 8
        if at + 4 > len(body):
            return None

        frame = struct.unpack_from('>I', body, at)[0]
        at += 4
        if frame % FRAME or frame == 0:
            return None

        for _ in range(frame // FRAME - 1):
            if at + 8 > len(body):
                return None

            sid, value = struct.unpack_from('>II', body, at)
            if sid == PLAYER_ID:
                return value

            at += 8

    return None


def universe_account_ids(body):
    """Any `0x0000271200000000 + id` u64 in the body yields its low half - the account id the login tier sent."""
    out = []
    for i in range(0, len(body) - 8):
        high, low = struct.unpack_from('>II', body, i)
        if high == UNIVERSE_PREFIX and low != 0:
            out.append(low)

    return out


def main():
    rows = []
    for path in capture_files():
        name = os.path.basename(path)
        try:
            with open(path) as handle:
                packets = json.load(handle)['Packets']
        except Exception as e:                      # noqa: BLE001
            print(f"  !! {name}: {e}")
            continue

        accounts, players = set(), set()
        for _, body in bodies(packets):
            accounts.update(universe_account_ids(body))
            found = snapshot_player_id(body)
            if found is not None:
                players.add(found)

        if accounts or players:
            rows.append((name, sorted(accounts), sorted(players)))

    print(f"captures with either: {len(rows)}\n")
    both = [r for r in rows if r[1] and r[2]]
    print(f"captures carrying BOTH a login account id and a snapshot PlayerId: {len(both)}")
    for name, accounts, players in both:
        marks = []
        for a in accounts:
            for p in players:
                marks.append(f"account={a} (0x{a:X}) player={p} (0x{p:X}) equal={a == p} diff={p - a}")

        print(f"  {name}\n    " + "\n    ".join(marks))

    print("\n--- captures with a login id but no snapshot (and vice versa) ---")
    for name, accounts, players in rows:
        if bool(accounts) != bool(players):
            print(f"  {name}: accounts={[hex(a) for a in accounts]} players={[hex(p) for p in players]}")


if __name__ == '__main__':
    main()
