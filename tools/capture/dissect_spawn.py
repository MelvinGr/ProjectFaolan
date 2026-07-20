#!/usr/bin/env python3
"""FA015807 (entity Spawn) dissection workbench.

Early dissection workbench (Phase 0/1): pulls every Spawn body from one or more captures, annotates the
transform/name, and diffs two spawns byte-for-byte. SUPERSEDED for the full player-spawn format by
tools/capture/parse_spawn_scaffold.py (the validated grammar - parses + regenerates every captured player spawn
byte-exact; see docs/re/player-spawn-generation-plan.md). Kept for quick NPC-spawn poking.

Layout discovered so far:
  [u32 len][u32 0xFA015807][u32 0x0000C350][u32 handle][u32 fieldA]
  0x2c <block>   where <block> begins protobuf-ish nested messages:
     0x0a <len> { 0d <x:f32 LE> 15 <y:f32 LE> 1d <z:f32 LE> }     position
     0x12 <len> { 0d <rx> 15 <ry> 1d <rz> 25 <rw> ... }           rotation/quaternion
     ... (the rest is fully mapped - see parse_spawn_scaffold.py)

Usage:
  python3 tools/capture/dissect_spawn.py                 # annotate the Strie player + 2 NPC spawns
  python3 tools/capture/dissect_spawn.py diff A B         # byte-diff spawn index A vs B (Strie login capture)
"""
import sys, struct, importlib.util

CAP = 'Captures/Strie_rogue-assassin_login.tshark.json'
spec = importlib.util.spec_from_file_location('c', 'tools/capture/compare_log_to_dump.py')
cdt = importlib.util.module_from_spec(spec); spec.loader.exec_module(cdt)


def spawns(cap=CAP):
    return [p[4] for p in cdt.from_dump(cap) if p[2] == 0x2000 and p[4][4:8].hex() == 'fa015807']


def le_f32(b):
    return struct.unpack('<f', b)[0]


def annotate(b):
    handle = int.from_bytes(b[12:16], 'big')
    fieldA = int.from_bytes(b[16:20], 'big')
    print(f'  handle=0x{handle:X}  fieldA=0x{fieldA:X}  bodylen={len(b)}')
    i = 20
    if b[i] == 0x2c:
        i += 1
    # position block 0x0a
    if i < len(b) and b[i] == 0x0a:
        ln = b[i + 1]; blk = b[i + 2:i + 2 + ln]; i += 2 + ln
        # 0d x 15 y 1d z
        x, y, z = le_f32(blk[1:5]), le_f32(blk[6:10]), le_f32(blk[11:15])
        print(f'  position (LE): ({x:.3f}, {y:.3f}, {z:.3f})')
    # rotation block 0x12
    if i < len(b) and b[i] == 0x12:
        ln = b[i + 1]; blk = b[i + 2:i + 2 + ln]; i += 2 + ln
        print(f'  rotation block ({ln}b): {blk.hex()}')
    print(f'  tail from offset {i}: {b[i:i+48].hex()}')
    # name search: look for printable ASCII runs >= 3
    runs = []
    cur = b''
    for byte in b[20:]:
        if 0x20 <= byte < 0x7f:
            cur += bytes([byte])
        else:
            if len(cur) >= 3:
                runs.append(cur.decode('ascii'))
            cur = b''
    if runs:
        print(f'  ascii runs: {runs}')


def diff(a, b):
    n = max(len(a), len(b))
    i = 0
    print(f'  byte-diff len {len(a)} vs {len(b)}')
    while i < n:
        if i < len(a) and i < len(b) and a[i] == b[i]:
            i += 1
            continue
        j = i
        while j < n and not (j < len(a) and j < len(b) and a[j] == b[j]):
            j += 1
        print(f'  @{i:4} A={a[i:j].hex()}  B={b[i:j].hex()}')
        i = j


def main():
    sp = spawns()
    if len(sys.argv) >= 4 and sys.argv[1] == 'diff':
        diff(sp[int(sys.argv[2])], sp[int(sys.argv[3])])
        return
    # find the player spawn (handle == Strie instance) + two NPCs
    player = next((s for s in sp if int.from_bytes(s[12:16], 'big') == 0x02165EFF), sp[0])
    print('=== PLAYER (Strie) spawn ==='); annotate(player)
    others = [s for s in sp if s is not player][:2]
    for k, s in enumerate(others):
        print(f'=== NPC spawn {k} ==='); annotate(s)


if __name__ == '__main__':
    main()
