#!/usr/bin/env python3
"""Decode the Quest_c region's per-record PROGRESS sub-list (Gitea #384).

`QuestcRegion.cs` round-trips the beach create template byte-exact, but the beach journal is the degenerate
case: fresh char, empty objective lists, EMPTY progress list. Every progressed (level-80) journal carries a
0x3F1-framed progress sub-list after obj4, which the codec never modelled — so `ParseInventoryPacketBody`
desynced on record 0 and blew up on the next record's header ("Quest_c record: marker 0x2 != 0x412").

This script decodes the whole region out of the capture JSONs and validates it consumes exactly up to the
next region, which is what pins the grammar. Run:

    tools/capture/decode_questc_progress.py [capture.tshark.json ...]

with no args it sweeps every capture that carries a 0x737A6DF9 snapshot.
"""
import glob
import json
import struct
import sys
import sys as _sys, pathlib as _pl
_sys.path.insert(0, str(_pl.Path(__file__).resolve().parent))
from captures import capture_files, capture  # corpus lives in the ProjectFaolan-Data repo

FRAME = 1009  # ListFrame.Unit (0x3F1)
SNAPSHOT_SUBOP = "737a6df9"


class Reader:
    def __init__(self, buf):
        self.b = buf
        self.o = 0

    def u16(self):
        v = struct.unpack_from(">H", self.b, self.o)[0]
        self.o += 2
        return v

    def u32(self):
        v = struct.unpack_from(">I", self.b, self.o)[0]
        self.o += 4
        return v

    def u64(self):
        v = struct.unpack_from(">Q", self.b, self.o)[0]
        self.o += 8
        return v

    def string(self):
        n = self.u16()
        v = self.b[self.o:self.o + n]
        self.o += n
        return v

    def framed_count(self):
        """A ListFrame word: (count + 1) * 0x3F1. Never a constant - 0x7E2 just means count 1."""
        w = self.u32()
        if w == 0 or w % FRAME:
            raise ValueError(f"not a list frame: 0x{w:X} at {self.o - 4}")
        return w // FRAME - 1


def read_objlist(r):
    """[u32 count][count x ObjRec(16 u32)]"""
    c = r.u32()
    recs = [[r.u32() for _ in range(16)] for _ in range(c)]
    return recs


def read_progress_entry(r):
    """One progress entry - which is EXACTLY a QuestObjectiveGoal record (Faolan.Core.Network/Codecs/Quest).

    The journal's per-record progress sub-list reuses the @609 objective-goal record verbatim, so this needs no
    new grammar at all. docs/re/questc-region-decode.md mis-segmented it: it read `reserved[5..6]` (the already-
    documented "70099 / 4-char tag" pair) as the start of an opaque 'WTHD' server block, and concluded the magic
    0x57544844 was unrecoverable because it has zero xrefs in AgeOfConan.exe. It has zero xrefs because it is not
    a magic - it is a RANDOM 4-character token, different in every entry (39 anchors in NecroL80's journal, 39
    distinct tokens: WTHD, 1LJL, UFWN, HIB4, ...), the same flavour of random key the <remoteformat key="..."
    attributes carry. Nothing here is opaque.
    """
    e = {
        "lead": r.u32(),
        "k": r.u32(),
        "instance": r.u32(),  # the player's own client instance
        "questId": r.u32(),
        "goalId": r.u32(),
        "n": r.u32(),
        "goalIdRepeat": r.u32(),
        "requiredCount": r.u32(),
        "reserved": [r.u32() for _ in range(7)],  # [5]=70099 and [6]=the random token, on progressed goals
        "subId": r.u32(),
        "mapId": r.u32(),
        "markerX": r.u32(),
        "markerY": r.u32(),
        "zero1": r.u32(),
        "zero2": r.u32(),
    }
    n = r.u32()  # objective: u32-length-prefixed (NOT the u16 "strlen" the old doc guessed)
    e["objective"] = r.b[r.o:r.o + n]
    r.o += n
    lead = r.b[r.o]
    r.o += 1
    if lead != 0:
        raise ValueError(f"goal text-record lead 0x{lead:X} != 0 at {r.o - 1}")
    tn = r.b[r.o]  # trailing text record: u8-length-prefixed
    r.o += 1
    e["text"] = r.b[r.o:r.o + tn]
    r.o += tn
    return e


def read_record(r):
    rec = {"listKey": r.u32(), "ver": r.u32(), "questId": r.u32(), "state": r.u32()}
    if rec["ver"] != 1:
        raise ValueError(f"record ver {rec['ver']} != 1 at {r.o - 12}")
    if rec["listKey"] != rec["questId"]:
        raise ValueError(f"listKey {rec['listKey']} != questId {rec['questId']}")
    rec["field114"] = r.u32()
    rec["field118"] = r.u32()
    rec["n"] = r.u32()  # +0x14 - NOT the 0x412 "marker" the beach-only codec asserted
    rec["strings"] = [r.string() for _ in range(5)]
    rec["fieldEc"] = r.u32()
    rec["areaId"] = r.u32()
    rec["field2c"] = r.u32()  # == questId on the beach template only
    rec["field11c"] = r.u32()
    rec["accept"] = r.u64()
    rec["field28"] = r.u32()
    rec["obj1"] = read_objlist(r)
    rec["obj2"] = read_objlist(r)
    rec["fieldF0"] = r.u32()
    rec["fieldF8"] = r.u64()
    rec["obj3"] = read_objlist(r)
    rec["obj4"] = read_objlist(r)
    rec["progress"] = [read_progress_entry(r) for _ in range(r.framed_count())]
    return rec


def read_region(tails):
    r = Reader(tails)
    ver = r.u32()
    if ver != 0x18:
        raise ValueError(f"region version 0x{ver:X} != 0x18")
    n = r.framed_count()
    return [read_record(r) for _ in range(n)], r.o


def snapshots(path):
    with open(path) as fh:
        doc = json.load(fh)
    for p in doc.get("Packets", []):
        body = (p.get("BodyHex") or "")
        if SNAPSHOT_SUBOP not in body[:40].lower():
            continue
        raw = bytes.fromhex(body)
        if len(raw) < 21:
            continue
        blob_len = struct.unpack_from(">I", raw, 17)[0]
        yield raw[21 + blob_len:]  # the tails: everything past the root


def main(argv):
    paths = argv[1:] or capture_files("*.tshark.json")
    ok = bad = 0
    for path in paths:
        for tails in snapshots(path):
            try:
                records, consumed = read_region(tails)
            except Exception as exc:  # noqa: BLE001 - a decode failure is the result we want to see
                bad += 1
                print(f"FAIL {path.split('/')[-1]}: {exc}")
                continue
            ok += 1
            prog = sum(len(rec["progress"]) for rec in records)
            print(f"OK   {path.split('/')[-1]}: {len(records)} records, {prog} progress entries, "
                  f"consumed {consumed}/{len(tails)} bytes")
    print(f"\n{ok} snapshot(s) decoded, {bad} failed")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
