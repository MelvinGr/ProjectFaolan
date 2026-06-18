#!/usr/bin/env python3
"""Humanoid NPC post-IIR APPEARANCE / morph table (FA015807 SimpleCharFullUpdate), Gitea #185 / #243.

A HUMANOID NPC's FA015807 post-IIR (the stream after the embedded ProtoCoreCharacterIIR) is NOT pure runtime
state - it carries that NPC's MORPH / APPEARANCE table. Dropping it (emitting the 41-byte minimal animal form)
makes the client render the NPC bald (#243). This is the grammar that lets the table generate from DB rows so
NpcSpawn.Data can be retired (the construct-don't-replay end state, #193).

Grammar (offsets relative to the post-IIR start; all u32 big-endian, floats big-endian raw-body):
  [u32 word0=0][u32 firstRecordLen=5][u32 recordCount=2]      header
  record1: 00 00 00 03 F1                                     ([u8 0][u32 0x3F1] empty leading list)
  [u32 entryCount]
  entryCount x ENTRY:
     [u32 morphId][f32 value][u32 field2=0][u8 field3=0][u8 strLen][strLen bytes]
       - plain entries have strLen=0 (14 bytes); the texscript entry (id 0x10d) carries e.g. "*ii=-1;*bl=-1"
  terminator (constant 20 bytes): 00 00 03 F1  00 00 03 F1  00 00 00 00 00 00 00 00  00 00 03 F1

Animals carry no morph table - they ship the constant 41-byte minimal post-IIR (NpcSpawnScaffold.MinimalBody);
TryParse below returns None for them and for the larger runtime-state forms (recordCount > 2 / extra records),
so only the clean humanoid appearance form is lifted.

Run:  python3 Captures/scripts/decode_npc_morphs.py [golden-or-capture ...]   # round-trip + dump every humanoid Casilda-shaped post-IIR
"""
import struct, sys, glob

TERMINATOR = bytes.fromhex('000003f1000003f10000000000000000000003f1')
RECORD1 = bytes.fromhex('00000003f1')


def u32(b, i):
    return int.from_bytes(b[i:i + 4], 'big')


def find_iir(b):
    """(offset, length) of the embedded ProtoCoreCharacterIIR (0A <len> <printable name>, u32 len @-4)."""
    for i in range(24, len(b) - 2):
        if b[i] != 0x0A:
            continue
        n = b[i + 1]
        if not (1 <= n <= 63) or i + 2 + n > len(b):
            continue
        if not all(0x20 <= c <= 0x7E for c in b[i + 2:i + 2 + n]):
            continue
        L = u32(b, i - 4)
        if L < n + 2 or i + L > len(b):
            continue
        return i, L
    return None


def try_parse(post):
    """Parse a clean humanoid appearance post-IIR -> list of (morphId, valueBytes(4), text). Returns None for the
    minimal animal form and the larger runtime-state forms (anything not matching the clean recordCount==2 shape)."""
    if len(post) < 21 or u32(post, 0) != 0:
        return None
    i = 12
    if post[i:i + 5] != RECORD1:
        return None
    i += 5
    count = u32(post, i)
    i += 4
    if count == 0 or count > 0x100:
        return None
    entries = []
    for _ in range(count):
        if i + 14 > len(post):
            return None
        morph_id = u32(post, i)
        value = post[i + 4:i + 8]
        if u32(post, i + 8) != 0 or post[i + 12] != 0:
            return None
        str_len = post[i + 13]
        i += 14
        text = post[i:i + str_len]
        i += str_len
        entries.append((morph_id, value, text))
    # what remains must be exactly the constant terminator
    if post[i:] != TERMINATOR:
        return None
    return entries


def emit(entries):
    out = bytearray()
    out += (0).to_bytes(4, 'big') + (5).to_bytes(4, 'big') + (2).to_bytes(4, 'big')
    out += RECORD1
    out += len(entries).to_bytes(4, 'big')
    for morph_id, value, text in entries:
        out += morph_id.to_bytes(4, 'big') + value + (0).to_bytes(4, 'big') + bytes([0, len(text)]) + text
    out += TERMINATOR
    return bytes(out)


def main(argv):
    paths = argv or sorted(glob.glob('Faolan.Tests/Golden/SpawnNpc*.bin'))
    ok = fail = skipped = 0
    for path in paths:
        b = open(path, 'rb').read()
        loc = find_iir(b)
        if not loc:
            continue
        off, ln = loc
        post = b[off + ln:]
        entries = try_parse(post)
        name = path.split('/')[-1]
        if entries is None:
            print(f'{name:26} post={len(post):4}B  (not a humanoid appearance table - animal/runtime form, skipped)')
            skipped += 1
            continue
        roundtrip = emit(entries) == post
        ok += roundtrip
        fail += not roundtrip
        strings = [(hex(i), t.decode('ascii', 'replace')) for i, v, t in entries if t]
        print(f'{name:26} post={len(post):4}B  entries={len(entries):2}  roundtrip={"OK" if roundtrip else "FAIL"}  texscript={strings}')
        for mid, val, t in entries:
            print(f'    id=0x{mid:04X}  value={struct.unpack(">f", val)[0]:+.5f}' + (f'  "{t.decode("ascii","replace")}"' if t else ''))
    print(f'\n{ok} round-trip OK, {fail} FAIL, {skipped} non-humanoid skipped')
    return 1 if fail else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
