#!/usr/bin/env python3
"""Decode the 0x2038 DispatchGuildRankingsUpdate body (the global guild-renown leaderboard).

Grammar (big-endian), validated to the packet end against the hoxxling guild_invite_accepted capture. Every list
is AoC-count-framed: a leading u32 word == (elementCount + 1) * 0x3F1.
    [u32 (guildCount+1)*0x3F1]
    record*   : [u32 0x0000DEB7][u32 guildId][s16 name][u64 total][u64 valor][u64 glory][u64 artistry]
                [u32 members][u32 level]          # total == valor + glory + artistry
    [u32 (4+1)*0x3F1]
    section x4: [u32 categoryIndex 0..3][u32 (entryCount+1)*0x3F1]
                entry* : [u32 0x0000DEB7][u32 guildId][u64 renown][u32 prevRank]
    [u32 nextUpdateUnix]
Category index -> tab: 0 Total, 1 Valor, 2 Glory, 3 Artistry. prevRank = previous-snapshot position (the "Change"
column); current rank is the entry's position.

Usage: decode_guild_rankings.py <capture.tshark.json> [packetIndex]
The C# counterpart is Faolan.Core.Network/Codecs/Guild/GuildRankings.cs (GuildRankingsTests locks it byte-exact).
"""
import json
import sys

GUILD_CLASS = 0x0000DEB7
LIST_MULT = 0x3F1


def decode(b: bytes):
    o = 0

    def u16():
        nonlocal o
        v = int.from_bytes(b[o:o + 2], "big"); o += 2; return v

    def u32():
        nonlocal o
        v = int.from_bytes(b[o:o + 4], "big"); o += 4; return v

    def u64():
        nonlocal o
        v = int.from_bytes(b[o:o + 8], "big"); o += 8; return v

    def s16():
        nonlocal o
        n = u16(); v = b[o:o + n]; o += n; return v.decode("latin1")

    def list_count():
        word = u32()
        assert word % LIST_MULT == 0 and word != 0, f"list word {word:#x} not a positive multiple of 0x3F1"
        return word // LIST_MULT - 1

    records = []
    for _ in range(list_count()):
        assert u32() == GUILD_CLASS
        gid = u32(); name = s16()
        total, valor, glory, artistry = u64(), u64(), u64(), u64()
        members, level = u32(), u32()
        assert valor + glory + artistry == total, f"{name}: total {total} != {valor + glory + artistry}"
        records.append((gid, name, total, valor, glory, artistry, members, level))

    tabs = ["Total", "Valor", "Glory", "Artistry"]
    section_count = list_count()
    assert section_count == 4, section_count
    sections = []
    for category in range(4):
        index = u32()
        assert index == category, f"section {category}: index {index}"
        entries = []
        for _ in range(list_count()):
            assert u32() == GUILD_CLASS
            entries.append((u32(), u64(), u32()))
        sections.append(entries)

    next_update = u32()
    assert o == len(b), f"decoded {o} of {len(b)} bytes"

    print(f"nextUpdateUnix={next_update} ({next_update:#x})")
    print(f"records: {len(records)}")
    for gid, name, total, valor, glory, artistry, members, level in records[:5]:
        print(f"  {name!r:32} id={gid:#x} total={total} V/G/A={valor}/{glory}/{artistry} members={members} level={level}")
    for category, entries in enumerate(sections):
        top = ", ".join(f"{gid:#x}:{ren}(#{rank})" for gid, ren, rank in entries[:3])
        print(f"section {category} ({tabs[category]}): {len(entries)} entries  {top}")


def main():
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(1)
    data = json.load(open(sys.argv[1]))
    packets = data["Packets"]
    if len(sys.argv) >= 3:
        indices = [int(sys.argv[2])]
    else:
        indices = [i for i, p in enumerate(packets)
                   if str(p.get("Opcode", "")).lower() == "0x2038" and p.get("Direction") == "s2c"]
    for i in indices:
        print(f"=== packet #{i} ===")
        decode(bytes.fromhex(packets[i]["BodyHex"]))


if __name__ == "__main__":
    main()
