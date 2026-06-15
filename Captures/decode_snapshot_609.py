#!/usr/bin/env python3
"""Decode the @609 OBJECTIVE-DETAIL region of the 0x737A6DF9 FullChar snapshot TAILS.

Context: the tails open with the Quest_c journal region (@0..609, decoded by decode_questc.py +
the C# QuestcRegion codec). The bytes from @609 up to the quest INDEX (the 0x000003F1 tag) were
PREVIOUSLY believed to be a "QuestFullUpdate-style block authored by WorldQuest.BuildQuestFullUpdate"
(docs/fullplayer-snapshot-reader.md side-thread). That is WRONG for the beach starter: empirically
BuildQuestFullUpdate's content for quest 10238 is 869 B and starts [questId][1][questId][3][0][3][0x412],
whereas the template's @609 region is 559 B and starts [0x0BD3][1][9][instance][questId][goalId]. They
differ at byte 2. (Proven by Faolan.Tests/Snapshot609Tests.)

The @609 region is its OWN grammar: a per-GOAL objective-detail list. For each goal of the quest:

    [0x00000BD3 chapterTag]            # once, opens the region (per-chapter grouping word)
    u32 1
    u32 9                             # region/type constant (meaning open)
    --- per goal ---
    u32 instance                      # the live player's client instance (re-addressed like the rest)
    u32 questId
    u32 goalId
    u32 n                             # small int (goal 1 -> 1, goal 2 -> 2 ... an ordinal/count)
    u32 goalId                        # repeated
    u32 1
    u32 x7 0                          # padding/reserved
    u32 subId                         # a secondary text id (= the cat-51200 record's id, e.g. 180)
    u32 mapId
    u32 markerX                       # this goal's world marker (goal1 416/701 == QuestDefinition marker)
    u32 markerY
    u32 0
    u32 0
    u32 textLen ; <objective remoteformat cat=50304>   # length-prefixed (u32) inline ascii, NOT 00-len
    <text record cat=51200 / <localized> cat=51000>     # a 00-<u8 len> record

Then the quest INDEX (decode_snapshot_quests.py): [0x000003F1][marker][questId x N][N][0x000000F1]...

So the objective TEXT is DB-sourced (every key matches QuestTextRefs), but the STRUCTURE is per-goal and
the beach starter has TWO goals (1448 + 3490), each with its own marker - richer than QuestDefinition's
single GoalId/marker. Generating this region needs a per-goal definition model (see Gitea #186).

Run:  python3 Captures/decode_snapshot_609.py   # defaults to the beach tails template
"""
import struct, sys

TEMPLATE = "Faolan/GameServer/Packets/FullCharSnapshot/BeachTailsTemplate.bin"
CHAPTER_TAG = 0x00000BD3
INDEX_TAG = 0x000003F1


def u32(b, o):
    return struct.unpack(">I", b[o:o + 4])[0]


def read_text(b, o):
    # 00 <u8 len> <ascii '<'...'>'>
    if o + 2 <= len(b) and b[o] == 0x00 and b[o + 1] and o + 2 + b[o + 1] <= len(b) and b[o + 2:o + 3] == b"<":
        ln = b[o + 1]
        return o + 2 + ln, b[o + 2:o + 2 + ln].decode("latin1")
    return None, None


def decode(path, start=609):
    b = open(path, "rb").read()
    end = b.find(b"\x00\x00\x03\xf1")  # the quest index tag ends the region
    print(f"=== {path.split('/')[-1]}  @{start}..{end} ({end - start} B) ===")
    o = start
    assert u32(b, o) == CHAPTER_TAG, f"expected chapterTag at {o}, got 0x{u32(b,o):x}"
    print(f"@{o} chapterTag 0x{CHAPTER_TAG:x}")
    o += 4
    goal = 0
    # Per goal: [1][k][instance][questId][goalId][n][goalId][1][0 x7][subId][map][mx][my][0][0]
    #           [u32 objLen][objective remoteformat cat=50304] [00-len text record cat=51200/51000]
    while o + 8 <= end and u32(b, o) == 1:
        goal += 1
        k = u32(b, o + 4)
        inst, qid, gid, n, gid2 = (u32(b, o + 8 + j) for j in (0, 4, 8, 12, 16))
        sub, mp, mx, my = (u32(b, o + 8 + j) for j in (52, 56, 60, 64))
        print(f"@{o:4d} GOAL {goal} (sep [1][{k}]): questId={qid} goalId={gid} n={n} rep={gid2} "
              f"subId={sub} map={mp} marker=({mx},{my})")
        o += 8 + 19 * 4  # the [1][k] separator + the 19-word fixed header
        # objective text: [u32 len][ascii '<...>']
        ln = u32(b, o)
        print(f"@{o:4d}   OBJ[{ln}]  {b[o+4:o+4+ln].decode('latin1')}")
        o += 4 + ln
        # trailing text record: 00 <u8 len> <ascii>
        no, txt = read_text(b, o)
        assert txt is not None, f"expected text record at {o}: {b[o:o+8].hex()}"
        print(f"@{o:4d}   TEXT  {txt}")
        o = no
    print(f"  -> parsed {goal} goal(s); ended @{o} (index @{end}, {'OK' if o == end else 'MISALIGNED'})")


if __name__ == "__main__":
    for a in (sys.argv[1:] or [TEMPLATE]):
        decode(a)
