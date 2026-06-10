#!/usr/bin/env python3
"""96C46740 (per-NPC MoveTask / patrol path) dissection workbench — PARTIAL.

Each entity spawn (FA015807) is paired with a 96C46740 MoveTask that registers the NPC's movement: its current
position and a patrol PATH (a variable-length list of waypoint vec3s) plus movement params (speed, flags,
orientation). This is the world burst's largest/most-variable remaining family and carries data not in any
table yet (docs/spawn-dissection-plan.md). This workbench identifies the stable structure; the control-byte
grammar between waypoints (the per-segment count/flag bytes, speed, orientation, trailer) is NOT yet nailed
byte-exact - that is the focused follow-up before a PatrolPath schema can be built.

Stable structure found (offsets body-relative; NOT the 0x2C transform framing the spawns/objects use):
  [0:16]   header: bodyLen, subOp=96C46740, ack=0xC350, handle  (handle == the paired FA015807 spawn)
  [16:18]  prefix (00 00)
  @18      current position: vec3 of 3 BIG-ENDIAN floats (NOTE: BE here, unlike the spawn transform's LE)
  ...      movement control bytes (04 01 00 02 01 01 ... - type/flags), then a count and the waypoint list:
  @..      N x waypoint vec3 (BE floats) - the patrol path; N varies (short patrol = 2 pts, longer = 8+)
  ...      a speed float, a repeated path segment, an orientation, and a trailer (00..bf..3f5da8df..00 01)

Usage:
  python3 Captures/dissect_movetask.py            # table: handle, current pos, waypoint count, length
  python3 Captures/dissect_movetask.py one <hex>  # waypoint/byte map of one MoveTask by handle
"""
import importlib.util
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('c', ROOT / 'Captures/compare_log_to_dump.py')
cdt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cdt)

CAP = 'Captures/account-stricken997-character-Strie-login.json'
SUBOP = '96c46740'


def u32(b, i):
    return int.from_bytes(b[i:i + 4], 'big')


def bef(b, i):
    return struct.unpack('>f', b[i:i + 4])[0]


def is_world_float(v):
    return 50 < abs(v) < 2000


def move_tasks():
    return [p[4] for p in cdt.from_dump(CAP) if p[2] == 0x2000 and len(p[4]) >= 16 and p[4][4:8].hex() == SUBOP]


def vec3_offsets(b):
    """Offsets of plausible waypoint vec3s (3 consecutive big-endian world-range floats)."""
    out = []
    i = 16
    while i + 12 <= len(b):
        if all(is_world_float(bef(b, i + 4 * k)) for k in range(3)):
            out.append(i)
            i += 12
        else:
            i += 1
    return out


def cmd_table():
    rows = move_tasks()
    print(f'{len(rows)} MoveTasks (96C46740) in {Path(CAP).name}\n')
    print(f'{"handle":>9} {"len":>4} {"curPos":28} {"vec3s":>5}')
    print('-' * 52)
    for b in sorted(rows, key=lambda x: u32(x, 12)):
        cp = (bef(b, 18), bef(b, 22), bef(b, 26))
        print(f'0x{u32(b,12):07X} {len(b):>4} ({cp[0]:7.1f},{cp[1]:6.1f},{cp[2]:7.1f}) {len(vec3_offsets(b)):>5}')


def cmd_one(handle_hex):
    h = int(handle_hex, 16)
    b = next(x for x in move_tasks() if u32(x, 12) == h)
    print(f'MoveTask 0x{h:X} len={len(b)} ack={u32(b,8):#x}')
    offsets = set(vec3_offsets(b))
    i = 16
    while i < len(b):
        if i in offsets:
            print(f'  @{i:3} WAYPOINT ({bef(b,i):.2f}, {bef(b,i+4):.2f}, {bef(b,i+8):.2f})')
            i += 12
        else:
            run = []
            while i < len(b) and i not in offsets:
                run.append(b[i])
                i += 1
            print(f'  @{i-len(run):3} bytes {bytes(run).hex(" ")}')


if __name__ == '__main__':
    if len(sys.argv) > 2 and sys.argv[1] == 'one':
        cmd_one(sys.argv[2])
    else:
        cmd_table()
