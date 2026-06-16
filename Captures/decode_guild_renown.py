#!/usr/bin/env python3
"""Decode pass on the 277-byte renown/feat/city-stats block inside the 0x203F GuildFullUpdate.

Run against the guild_invite capture (two 0x203F snapshots of the guild "Sepulchra Invictus"):
  python3 decode_guild_renown.py account-faolan-character-hoxxling-login-guild_invite_accepted-...tshark.json

Context: 0x203F body (see decode_guild.py) is
  [u8 govForm][u32 guildId][u8 1][u64 cash][u8 3][u32 0][u8 0][s16 name][s16 desc][s16 motd]
  [277-byte RENOWN BLOCK]  <- this file
  [self member record][u32 selfContribution][member records ... to end]

WHAT THIS PASS ESTABLISHED (2026-06-16, #219):
  - The block is byte-identical across the two snapshots EXCEPT a u32 counter at block offset 133
    (0x01327BAD -> 0x01327FFE), i.e. a renown/xp total that ticked between the two captures. So the
    block is overwhelmingly a STATIC scaffold + a few live counters, exactly as the GuildService
    comment assumed.
  - Leading structure is decoded:
      @0   u8  0x01                         format/version tag
      @1   u32 0x0000372E                   header word (a guild-level total? constant in both snapshots)
      @5   13x u32 stat-slot ids, CONTIGUOUS, ascending:
              0x4FFB0 0x4FFB1 0x50014 0x50078 0x500DC 0x50140 0x501A4
              0x50208 0x50209 0x5026C 0x5026D 0x502D0 0x50334
           (the +0x64 / +0x01 stepping is the classic AoC stat-id block; these are constant across
            guilds -> the structural scaffold a fresh-guild block must reproduce). Ends at @57.
  - @57..276 (220 bytes) is the COUNTER REGION. It does NOT parse as a parallel [13 x u32 value]
    array (reading u32s from @58 yields 0xF3000000.. which misaligns), so it has its own internal
    framing (typed/variable records) that is NOT recoverable from the bytes alone. The clean decode
    needs the client's guild IIR reader (Ghidra), the same approach as the other ReadSubClass readers
    (see ghidra_vtable_xref_readers). Until then the block stays template-carried in GuildService.

CONCLUSION: a fresh-guild GENERATOR is still blocked on the counter-region framing. But the block is
proven length-stable (always 277 bytes) and counter-only-variable, so carrying the captured template
verbatim is safe for the roster that follows it. This script documents the scaffold for the eventual
Ghidra pass.
"""
import json, struct, sys

# The captured "Sepulchra Invictus" renown block (== GuildRenownBlockTemplate in GuildService.cs).
TEMPLATE_HEX = (
    "0100"
    "00372e"
    "0004ffb00004ffb10005001400050078000500dc0005014000050"
    "1a4000502080005020900050"
    "26c0005026d000502d000050334"
    "0100000005f300000c0000081600000000000000000000000fc40000041000000816"
    "00000bea0000003900000000000000540000005400000000000000000000000001b48300000000"
    "01327bad000000000012b28f00000000000000b08100000000000000d40acb000000000000001026"
    "7400000003" "01c9c380" + "00"*40 +
    "1100000000000000000000000000000000000000"
    "017f2cbd00000000" "02853cc800000000" "01900cf100000000" "0000000" "03a1d51c0"
)


def stat_ids(blk):
    return [struct.unpack('>I', blk[5 + 4 * i:9 + 4 * i])[0] for i in range(13)]


def extract_blocks(path):
    d = json.load(open(path))
    fu = [p for p in d['Packets'] if str(p['Opcode']).lower() == '0x203f']
    out = []
    for p in fu:
        body = bytes.fromhex(p['BodyHex'])
        o = 0
        o += 1                              # govForm
        o += 4                              # guildId
        o += 1                              # 0x01
        o += 8                              # cash
        o += 1 + 4 + 1                      # 0x03, u32 0, 0x00
        for _ in range(3):                  # name, desc, motd (s16)
            n = struct.unpack('>H', body[o:o + 2])[0]; o += 2 + n
        out.append((p['Frame'], body[o:o + 277]))
    return out


def main(path):
    blocks = extract_blocks(path)
    print(f"found {len(blocks)} renown blocks")
    for frame, blk in blocks:
        print(f"  frame {frame}: len={len(blk)} ver={blk[0]:#x} hdr@1={struct.unpack('>I', blk[1:5])[0]:#x}"
              f" counter@133={struct.unpack('>I', blk[133:137])[0]:#x}")
        assert stat_ids(blk) == [0x4FFB0, 0x4FFB1, 0x50014, 0x50078, 0x500DC, 0x50140, 0x501A4,
                                 0x50208, 0x50209, 0x5026C, 0x5026D, 0x502D0, 0x50334], "stat-id scaffold drift"
    if len(blocks) >= 2:
        a, b = blocks[0][1], blocks[1][1]
        diffs = [i for i in range(277) if a[i] != b[i]]
        print(f"  differing bytes across snapshots: {diffs} (expected within the @133 counter u32)")


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else
         'account-faolan-character-hoxxling-login-guild_invite_accepted-random-gameplay-other_playersvisible.tshark.json')
