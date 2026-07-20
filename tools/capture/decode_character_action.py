#!/usr/bin/env python3
"""Dissector for CharacterActionIIR (0x6B6FD368) - the per-action grant record the create-editor WorldPrep
burst streams (issue #189 item 4). Each record grants one action/ability id to a character; the burst sends
one per id (the same ability/feat id space as the player-spawn scaffold's id list, e.g. 0x050E24).

Grammar (41-byte IIR-carrier body, after the [u32 len][u32 0x6B6FD368] carrier prefix):
    [u32 type=0x0000C350][u32 handle][u32 0][u8 0x1D][16x 0x00][u32 actionId BE][8x 0x00]

Only `handle` (the entity, = the live character) and `actionId` vary; everything else is fixed. Validated
byte-exact against Faolan.Tests/Golden/WorldPrepBurst.bin (all 26 records).

Usage: python3 tools/capture/decode_character_action.py [path/to/WorldPrepBurst.bin]
"""
import struct
import sys

OPCODE = bytes.fromhex("6B6FD368")
CHARACTER_TYPE = 0x0000C350
MARKER = 0x1D
RECORD_LEN = 41


def parse(body: bytes):
    """Returns (handle, actionId) if `body` is a well-formed CharacterActionIIR record, else None."""
    if len(body) != RECORD_LEN:
        return None
    type_, handle, zero = struct.unpack(">III", body[0:12])
    if type_ != CHARACTER_TYPE or zero != 0 or body[12] != MARKER:
        return None
    if body[13:29] != bytes(16) or body[33:41] != bytes(8):
        return None
    (action_id,) = struct.unpack(">I", body[29:33])
    return handle, action_id


def iter_records(stream: bytes):
    pos = 0
    while True:
        j = stream.find(OPCODE, pos)
        if j < 0:
            return
        pos = j + 1
        if j < 4:
            continue
        (length,) = struct.unpack(">I", stream[j - 4:j])
        yield stream[j + 4:j + 4 + length - 4]


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "Faolan.Tests/Golden/WorldPrepBurst.bin"
    data = open(path, "rb").read()
    ok = 0
    total = 0
    for body in iter_records(data):
        total += 1
        parsed = parse(body)
        if parsed is None:
            print(f"  NON-CONFORMING ({len(body)} bytes): {body.hex()}")
            continue
        ok += 1
        handle, action_id = parsed
        print(f"  handle=0x{handle:08X} actionId=0x{action_id:06X} ({action_id})")
    print(f"{ok}/{total} CharacterActionIIR records conform to the grammar")


if __name__ == "__main__":
    main()
