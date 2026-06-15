#!/usr/bin/env python3
"""Dissect the Quest_c (List A) region at the START of the 0x737A6DF9 FullChar snapshot TAILS.

This is the front of the long-opaque `UnknownBeforeAccessWindowTable` blob. Grammar recovered from the
client reader `FullPlayerCharacterIIR::ReadSubClass` (FUN_0148c210 -> base FUN_00797440 -> List A driver
FUN_0091f5c0 -> per-record FUN_0091df50); see docs/fullplayer-snapshot-reader.md and the memory
`ghidra_vtable_xref_readers`. Stream primitives: FUN_0048f900=ReadU32 (BE), FUN_0048f840=ReadString
([u16 len][bytes]), FUN_0048f720=ReadU64, FUN_00cc0540=Read4xU32.

Tails layout (validated against BeachTailsTemplate.bin, 1 quest):
    u32 version == 0x18
    u32 listA_count == N*0x3F1   (records = N-1)   # 1009-framing invariant
    Quest_c x (N-1):
        u32 listKey = questId                       # read by the List A loop, before the record
        u32 ver == 1
        u32 questId
        u32 state
        u32 0
        u32 N                                       # journal goal/line count
        u32 0x412                                   # constant marker
        string x5  ([u16 len][bytes])               # <remoteformat>: 50301 title / 50302 summary /
                                                    #   50300 area / 52000 region / 52000 region
        u32 ; u32 areaId ; u32 questId ; u32 0
        u64
        u32
        4x objective-list: u32 count (cap 100) then count x ObjRec(16 u32)   # EMPTY for the beach starter
    --> lands at the objective-detail block region (decode_snapshot_quests.py), then the index, then
        the PFAccessWindow_ table.

Run:  python3 Captures/decode_questc.py [tails.bin ...]   (default: the beach template)
"""
import struct, sys

TEMPLATE = "Faolan/GameServer/Packets/FullCharSnapshot/BeachTailsTemplate.bin"
VERSION = 0x18
FRAME = 0x3F1          # 1009
MARKER_412 = 0x412


class R:
    def __init__(self, b): self.b, self.o = b, 0
    def u16(self): v = struct.unpack_from(">H", self.b, self.o)[0]; self.o += 2; return v
    def u32(self): v = struct.unpack_from(">I", self.b, self.o)[0]; self.o += 4; return v
    def u64(self): v = struct.unpack_from(">Q", self.b, self.o)[0]; self.o += 8; return v
    def s(self):
        n = self.u16(); v = self.b[self.o:self.o + n]; self.o += n; return v


def read_quest_c(r):
    """Read one Quest_c record (the List A loop already consumed the listKey)."""
    ver = r.u32(); assert ver == 1, f"Quest_c ver != 1 (got {ver:#x}) @{r.o-4}"
    qid = r.u32(); state = r.u32(); z0 = r.u32(); n = r.u32(); mk = r.u32()
    assert mk == MARKER_412, f"marker != 0x412 (got {mk:#x}) @{r.o-4}"
    strings = [r.s() for _ in range(5)]
    f_ec = r.u32(); area = r.u32(); qid2 = r.u32(); f_11c = r.u32()
    accept = r.u64(); f_28 = r.u32()
    objlists = []
    for _ in range(2):                       # lists 1,2
        objlists.append(read_objlist(r))
    f_f0 = r.u32(); f_f8 = r.u64()
    for _ in range(2):                       # lists 3,4
        objlists.append(read_objlist(r))
    return dict(questId=qid, state=state, n=n, area=area, qid2=qid2, accept=accept,
                f_ec=f_ec, f_11c=f_11c, f_28=f_28, f_f0=f_f0, f_f8=f_f8,
                strings=strings, objcounts=objlists)


def read_objlist(r):
    cnt = r.u32()
    eff = min(cnt, 100)                       # client caps at 100
    for _ in range(eff):
        for _ in range(16):                   # ObjRec = 16 u32 (incl. two Read4xU32 refs)
            r.u32()
    return cnt


def decode(path):
    b = open(path, "rb").read()
    print(f"\n=== {path}  ({len(b)} bytes) ===")
    r = R(b)
    ver = r.u32(); cnt = r.u32()
    assert ver == VERSION, f"version != 0x18 (got {ver:#x})"
    assert cnt % FRAME == 0 and cnt > 0, f"List A count not 0x3F1-framed: {cnt:#x}"
    nrec = cnt // FRAME - 1
    print(f"  version=0x{ver:x}  listA_count=0x{cnt:x} -> {nrec} Quest_c record(s)")
    for i in range(nrec):
        listKey = r.u32()                     # List A map key = questId (read by FUN_0091f5c0, before the record)
        q = read_quest_c(r)
        assert listKey == q["questId"], f"listKey {listKey} != questId {q['questId']}"
        print(f"  Quest_c[{i}] @end={r.o}: questId={q['questId']} state={q['state']} N={q['n']} "
              f"area={q['area']} objcounts={q['objcounts']}")
        for s in q["strings"]:
            t = s.decode("latin1")
            import re
            cat = re.search(r'category="(\d+)"', t); idm = re.search(r'id="(\d+)"', t)
            print(f"      str[{len(s):3}] id={idm.group(1) if idm else '?':>6} cat={cat.group(1) if cat else '?'}")
    print(f"  >> Quest_c region consumed to @{r.o} (next: objective-detail blocks / index / PFAccessWindow_)")
    # sanity: the next bytes should be the objective-block / index region, not mid-string
    nxt = struct.unpack_from(">I", b, r.o)[0]
    print(f"  next word @{r.o} = 0x{nxt:08x}")


if __name__ == "__main__":
    for a in (sys.argv[1:] or [TEMPLATE]):
        decode(a)
