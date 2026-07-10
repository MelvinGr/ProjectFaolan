#!/usr/bin/env python3
"""Decode the QUEST-JOURNAL region embedded in the 0x737A6DF9 FullChar snapshot TAILS.

Why: the in-client quest log on LOGIN is delivered by the snapshot (retail sends ZERO separate quest
packets on an in-progress login - see Stihetfv_soldier-conqueror_login). So a quest a character
accepted only shows after relog if its records ride in the snapshot tails. The tails today are a captured
template carrying only the beach starter quest 10238 (Strie's); to show accepted quests we must GENERATE
this region from the character's quests. This script characterises the grammar so the generator is grounded.

Finding (decode): each quest is one block, structurally the SAME as the QuestFullUpdate (0x63268258) body's
content section (Faolan.Core.Network/Codecs/Quest/WorldQuest.cs BuildQuestFullUpdate):

    [0x00000BD3 ChapterTag]?          # grouping word - precedes the FIRST quest of a chapter run
    u32 questId
    u32 1
    u32 questId
    u32 state                         # per-quest (10238=1, 13479=3) - same field as QuestFullUpdate @ state
    u32 0
    u32 N                             # per-quest small int (10238=4, 13479=3)
    u32 0x412                         # constant marker (also in BuildQuestFullUpdate)
    <title    text slot: 00 <len> <remoteformat id=questId category=50301>>
    <summary  text slot:               remoteformat id=questId category=50302>
    <area     text slot:               remoteformat id=areaId  category=50300>
    <objective record(s): ... category=50304 ...>

Run:  python3 Captures/scripts/decode_snapshot_quests.py [capture.tshark.json ...]
Default captures: the beach template source + the in-progress (two-quest) login.
"""
import json, re, struct, sys

CHAPTER_TAG = 0x00000BD3
MARKER_412 = 0x00000412


def snapshot_body(path):
    doc = json.load(open(path))
    pk = doc["Packets"] if isinstance(doc, dict) else doc
    for p in pk:
        bh = (p.get("BodyHex") or "")
        if bh[8:16].lower() == "737a6df9":
            return bytes.fromhex(bh)
    return None


def u32(b, o):
    return struct.unpack(">I", b[o : o + 4])[0]


def decode(path):
    b = snapshot_body(path)
    print(f"\n=== {path.split('/')[-1]} ===")
    if not b:
        print("  no 0x737A6DF9 snapshot in capture")
        return
    print(f"  snapshot body {len(b)} bytes")

    # Each quest block opens with [questId][1][questId][state][0][N][0x412]; locate by the 0x412 marker
    # whose preceding u32s form that header, then read the questId 24 bytes back.
    blocks = []
    for m in re.finditer(b"\x00\x00\x04\x12", b):
        o = m.start()
        if o < 24:
            continue
        qid, one, qid2, state, zero, n = (u32(b, o - k) for k in (24, 20, 16, 12, 8, 4))
        if one == 1 and qid == qid2 and zero == 0 and 0 < qid < 0x10000:
            has_tag = o >= 28 and u32(b, o - 28) == CHAPTER_TAG
            blocks.append((o - 24, qid, state, n, has_tag))

    print(f"  quest blocks: {len(blocks)}")
    for off, qid, state, n, has_tag in blocks:
        tag = "0BD3 " if has_tag else "     "
        print(f"    @{off:5d} {tag}quest={qid} (0x{qid:04x}) state={state} N={n}")

    cats = {c: len(re.findall(("category=\"%s\"" % c).encode(), b)) for c in
            ("50300", "50301", "50302", "50304", "51000", "52000")}
    print(f"  remoteformat categories: {cats}")

    # The quest INDEX after the detail blocks: [1009=0x3f1][marker][questId x N][N][0xf1][0x57365154]...
    # N is the quest COUNT (template=1, xxx-login=2) - it sits AFTER the id-run, before the 0xf1 sentinel.
    mi = b.find(b"\x00\x00\x03\xf1")
    if mi >= 0:
        marker = u32(b, mi + 4)
        # ids are real quest ids (>= 0x1000); the COUNT (small) sits right before the 0xf1/0x57365154 sentinel.
        ids, o = [], mi + 8
        while o + 4 <= len(b) and u32(b, o) >= 0x1000:
            ids.append(u32(b, o)); o += 4
        count = u32(b, o)
        print(f"  index @{mi}: marker=0x{marker:x} ids={[hex(i) for i in ids]} count={count}"
              + ("  [OK count==#ids]" if count == len(ids) else "  [!! count mismatch]"))


if __name__ == "__main__":
    args = sys.argv[1:] or [
        "Captures/Strie_rogue-assassin_login.tshark.json",        # beach template source (1 quest)
        "Captures/Stihetfv_soldier-conqueror_login.tshark.json",          # in-progress (2 quests: 10238 + 13479)
    ]
    for a in args:
        decode(a)
