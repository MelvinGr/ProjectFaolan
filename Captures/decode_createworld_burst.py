#!/usr/bin/env python3
"""Inventory / construction-coverage pass over the create-world INTRO CUTSCENE burst (#234).

The post-creation world entry (shipwreck -> beach -> Kalanthes -> ApproachCasilda) is still served
verbatim from Faolan/GameServer/CreateFlow/RetailCreateWorldEntryBurst.cs. This script reads the source
capture, tallies the s2c stream, and classifies every 0x2000 IIR sub-opcode by how close it is to being
CONSTRUCTED (vs replayed) — the decode-first inventory the ticket calls for. No client needed.

Usage: decode_createworld_burst.py [capture.tshark.json]
"""
import json, struct, sys, os, collections

CAP = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "account-faolan-character-hoxxling-create-modified_all_sliders-very_long_loading_screen-chat_message_before_exit.tshark.json")

# sub-opcode -> (name, status, ref). status:
#   BUILT   = byte-exact builder exists; constructing the burst = wire it to the create-world map's DB rows.
#   GRAMMAR = wire grammar decoded + a builder exists for another flow (login/world); reuse it here.
#   TRIVIAL = fixed 4/9-byte no-arg dispatch or flag record; emit a constant.
#   CONTENT = cutscene choreography: the ScryScript step tokens are server-driven, the camera paths are RDB (#80).
CATALOG = {
    0x96C46740: ("MoveTaskIIR",            "GRAMMAR", "#190/#53"),
    0x642CD3D6: ("BehaviorControlCenterIIR","BUILT",  "#223/#225"),
    0xFA015807: ("NPC spawn (pre/post-IIR)","GRAMMAR", "#185"),
    0x04B82B2D: ("n3ToClientQuit (despawn)","BUILT",  "despawn builder"),
    0xF508F4C1: ("movement/AI burst",      "GRAMMAR", "#190"),
    0x28D0AC99: ("RegenIIR",               "BUILT",   "#223"),
    0xA36D3B74: ("ScryScriptIIR",          "CONTENT", "#191/#223/#80"),
    0x96B8DC59: ("StatUpdateIIR",          "BUILT",   "-"),
    0xF98E10B3: ("StatIIR",                "BUILT",   "-"),
    0x40749B8A: ("EnergyIIR",              "BUILT",   "-"),
    0x5D85BFC7: ("PlayfieldFIIR",          "BUILT",   "-"),
    0x4F57DC08: ("GameTimeIIR",            "BUILT",   "-"),
    0x737A6DF9: ("FullChar snapshot",      "BUILT",   "#186"),
    0xF096251E: ("LootBagFullUpdate",      "GRAMMAR", "loot codec"),
    0x66AEDD50: ("SimpleDynelFullUpdate",  "GRAMMAR", "world-object/dynel builder"),
    0x33A56FB0: ("DoorFullUpdate",         "GRAMMAR", "world-object builder"),
    0x4517AA81: ("CharDCMove",             "GRAMMAR", "movetask family"),
    0x504B7924: ("QuestLogListIIR",        "GRAMMAR", "WorldQuest.BuildQuestLogList"),
    0x63268258: ("QuestFullUpdateIIR",     "GRAMMAR", "quest"),
    0x79F8C7A1: ("QuestJournalIIR",        "GRAMMAR", "quest"),
    0xBA1FD222: ("FeedbackIIR",            "GRAMMAR", "FormatFeedback"),
    0x864CFEF8: ("CharInPlayIIR",          "TRIVIAL", "[C350][inst][u8 0]"),
    0x00017E03: ("dispatch (no-arg)",      "TRIVIAL", "4-byte 0"),
}


def main():
    dls = [p for p in json.load(open(CAP))["Packets"] if p["Source"]["Port"] == 7012 and p["IsDownload"]]
    tally = collections.Counter()
    by_status = collections.Counter()
    other_ops = collections.Counter()   # non-0x2000 carriers in the stream
    for p in dls:
        body = bytes.fromhex(p.get("BodyHex") or "")
        op = (p.get("Opcode") or "").lower()
        if op == "0x2000" and len(body) >= 8:
            sub = struct.unpack(">I", body[4:8])[0]
            tally[sub] += 1
        else:
            other_ops[op] += 1

    print(f"create-world burst: {len(dls)} s2c packets from :7012 in {os.path.basename(CAP)}\n")
    print(f"{'sub-opcode':12}{'x':>5}  {'status':8}{'name':28}ref")
    print("-" * 78)
    for sub, c in tally.most_common():
        name, status, ref = CATALOG.get(sub, (f"UNKNOWN 0x{sub:08X}", "UNKNOWN", "-"))
        by_status[status] += c
        print(f"0x{sub:08X}  {c:>5}  {status:8}{name:28}{ref}")
    if other_ops:
        print(f"\nnon-0x2000 carrier opcodes in the stream: {dict(other_ops)}")

    total = sum(tally.values())
    print(f"\n--- {total} IIR records, by construction status ---")
    for st in ("BUILT", "GRAMMAR", "TRIVIAL", "CONTENT", "UNKNOWN"):
        n = by_status.get(st, 0)
        if n:
            print(f"  {st:8} {n:4}  ({100*n//total}%)")
    unknown = by_status.get("UNKNOWN", 0)
    print(f"\nverdict: {'all records map to a decoded type' if not unknown else f'{unknown} records UNRESOLVED'}; "
          f"construction = wire the BUILT/GRAMMAR types to the create-world map's DB rows + the ScryScript (CONTENT) choreography.")


if __name__ == "__main__":
    main()
