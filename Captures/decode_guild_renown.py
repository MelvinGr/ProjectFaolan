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
  - @57..276 (220 bytes) is the COUNTER REGION (see the FRAMING CRACKED note below).

FRAMING CRACKED via Ghidra (2026-06-16, #219). The block is NOT an IIR ReadSubClass (the named
GuildUpdateIIR_c is an unrelated {bool,string} object). The 0x203F packet is the RPC method
CSPlayerInterface case 0x3F = DispatchGuildUpdate (FUN_013428d0), which reads three args:
i_pcGuild + i_pcMember + i_cMemberList. The renown block is the TAIL of i_pcGuild, read by
CoreGuild_c::Read (FUN_00565900, streaming version == 3, then a switch on govForm 0..3). Reader chain
recovered by Captures/ghidra/DumpGuildDispatch.java + DumpFnList.java. The block grammar:
  @0   u8  flag (ReadByte; 0x01 here)
  @1   0x3F1-FRAMED u32 LIST (FUN_004d4c70): word = (count+1)*0x3F1, then count x u32.
       0x0000372E == 14*0x3F1 -> count 13 -> the 13 stat-ids @5..57. (This is why reading @58 as a
       raw array misaligned: the leading region is a 0x3F1 list, the SAME primitive as the spawn
       reader's sub-list, not a flat array.)
  @57..276  a govForm-dependent run of ReadU64 (FUN_0048f720) / ReadU32 (FUN_0048f900) / ReadByte
       fields - the renown/feat/city counters. Most are u64 stored big-endian [hi u32][lo u32], which
       is the value/zero alternation seen below (e.g. @269..276 = u64 975,000,000; @169..176 a
       count(3)+u64 30,000,000). The @133 u32 that ticks across snapshots is one such counter.

CONCLUSION: the framing is no longer a black box - a fresh-guild GENERATOR is now a field-mapping job
(walk CoreGuild_c::Read case-by-case and emit the same ReadU64/ReadU32/ReadByte order with the live
guild's counters, zeroed for a brand-new guild). Carrying the captured template verbatim remains safe
(length-stable 277 bytes, counter-only-variable) until the generator lands. The @81 0xFC4 (==4*0x3F1)
is coincidental, not a nested list (it sits mid-u64-field).

FULL READER WALK (2026-06-17, #219): `--walk` deserializes the ENTIRE i_pcGuild arg the way CoreGuild_c::Read
does (helpers confirmed in Ghidra: FUN_0048f900=ReadU32, FUN_0048f720=ReadU64, FUN_0048f6d0/FUN_004e0390=
ReadByte, FUN_0048f840=ReadString, FUN_004d4c70=ReadList) and asserts the block ends EXACTLY on the next
member record (0x0303). This pinned two earlier MISLABELS:
  - the 0x203F's FIRST body byte is the CoreGuild_c STREAMING VERSION (==3), NOT the government form;
  - the government form is the byte AFTER guildId (==1 here) -> reader case 1, which falls through case 3.
cash + name/desc/motd are part of CoreGuild_c::Read itself (not read separately by the 0x203F handler).
RenownCounters[1] (param_1[0x40], block offset 133) is the live renown total; everything else is constant
across the two snapshots.
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


F3F1 = 0x3F1


def stat_ids(blk):
    return [struct.unpack('>I', blk[5 + 4 * i:9 + 4 * i])[0] for i in range(13)]


def parse_framing(blk):
    """Validate the cracked grammar: u8 flag, then the 0x3F1-framed u32 list (the 13 stat-ids)."""
    o = 0
    flag = blk[o]; o += 1
    word = struct.unpack('>I', blk[o:o + 4])[0]; o += 4
    assert word % F3F1 == 0, f"list word 0x{word:x} not a 0x3F1 multiple"
    count = word // F3F1 - 1
    ids = [struct.unpack('>I', blk[o + 4 * i:o + 4 * i + 4])[0] for i in range(count)]
    o += 4 * count
    return flag, word, count, ids, o


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


def walk(path):
    """Deserialize the whole i_pcGuild arg the way CoreGuild_c::Read does and assert the renown block ends
    exactly on the following member record (0x0303). Proves the version/govForm reinterpretation + the block grammar."""
    d = json.load(open(path))
    for p in [x for x in d['Packets'] if str(x['Opcode']).lower() == '0x203f']:
        b = bytes.fromhex(p['BodyHex']); o = [0]
        def u32():
            v = struct.unpack('>I', b[o[0]:o[0] + 4])[0]; o[0] += 4; return v
        def u64():
            v = struct.unpack('>Q', b[o[0]:o[0] + 8])[0]; o[0] += 8; return v
        def byte():
            v = b[o[0]]; o[0] += 1; return v
        def s16():
            n = struct.unpack('>H', b[o[0]:o[0] + 2])[0]; o[0] += 2; o[0] += n
        def lst():
            w = u32(); assert w % F3F1 == 0, hex(w); return [u32() for _ in range(w // F3F1 - 1)]

        ver = byte(); assert ver == 3, f"streaming version {ver} != 3"
        gid = u32()
        gov = byte(); assert gov == 1, f"government form {gov} != 1 (case 1 -> case 3)"
        u64()                       # cash
        byte(); u32(); byte()       # param[0x35], param[5], param[0x36]
        s16(); s16(); s16()         # name, desc, motd
        block_start = o[0]
        byte()                      # Flag -> param[4]
        ids = lst()                 # StatIds -> param+0x39
        assert len(ids) == 13, ids
        byte(); byte()              # BoolA/BoolB
        [u32() for _ in range(3)]   # Group1
        byte(); byte()              # BoolC/BoolD
        [u32() for _ in range(2)]   # Group2
        l2 = lst()                  # List2 -> param+0x1c
        assert len(l2) == 3, l2
        [u32() for _ in range(6)]   # Group3
        ren = [u64() for _ in range(6)]   # RenownCounters -> param[0x3e,0x40,...]
        [u32() for _ in range(2)]   # Group4
        [u64() for _ in range(8)]   # Counters64B
        [u64() for _ in range(3)]   # Counters64C
        u32()                       # Group5
        u64()                       # Counters64D
        block_len = o[0] - block_start
        nxt = b[o[0]:o[0] + 2].hex()
        print(f"  frame {p['Frame']}: guild {gid} block_len={block_len} (expect 277) "
              f"renownTotal(param[0x40])={ren[1]} next={nxt} (expect 0303)")
        assert block_len == 277 and nxt == "0303", "reader walk did not land on the member record"
    print("  OK - full CoreGuild_c::Read walk lands byte-exact on the member record")


def main(path):
    blocks = extract_blocks(path)
    print(f"found {len(blocks)} renown blocks")
    for frame, blk in blocks:
        flag, word, count, ids, counter_off = parse_framing(blk)
        print(f"  frame {frame}: len={len(blk)} flag={flag:#x} listword={word:#x}=({word // F3F1})*0x3F1"
              f" -> {count} stat-ids, counter region @{counter_off}, counter@133={struct.unpack('>I', blk[133:137])[0]:#x}")
        assert ids == [0x4FFB0, 0x4FFB1, 0x50014, 0x50078, 0x500DC, 0x50140, 0x501A4,
                       0x50208, 0x50209, 0x5026C, 0x5026D, 0x502D0, 0x50334], "stat-id scaffold drift"
    if len(blocks) >= 2:
        a, b = blocks[0][1], blocks[1][1]
        diffs = [i for i in range(277) if a[i] != b[i]]
        print(f"  differing bytes across snapshots: {diffs} (expected within the @133 counter u32)")


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if a != '--walk']
    path = args[0] if args else \
        'account-faolan-character-hoxxling-login-guild_invite_accepted-random-gameplay-other_playersvisible.tshark.json'
    (walk if '--walk' in sys.argv else main)(path)
