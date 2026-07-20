#!/usr/bin/env python3
"""Decode the create-burst BehaviorControlCenterIIR (0x642CD3D6) "registration" form (#234).

Distinct from the COMBAT BccAck (the type-switch reader in docs/re/behaviorcontrolcenter-grammar.md): the
create-world cutscene registers behaviors on scene entities with this fixed-shape record:

  [u32 C350][u32 handle][u16 form][u32 slot][u32 msgLen][protobuf][optional tail]

  form     = 0x0004 (the common registration) | 0x0007 (a rarer subtype that adds protobuf field 4)
  protobuf = base varint fields  f1=behaviorId, f3=phase, f5=field5, f6=field6
             + OPTIONAL varints   f2, f11, f12   (wire order: 1,[2],3,5,6,[11],[12])
  tail     = 0 bytes | 20-byte "bound" tail [u32 boundA][u32 boundB][12x 00] | 88-byte item-bound long form

`CreateEditorRecordBuilder.BuildBehaviorControlCenterIir` builds the form-0x0004 records (base + optional
2/11/12 + 20-byte bound tail); `BuildBehaviorControlCenterIirLong` builds the 88-byte item-bound long form.

Usage: decode_behaviorcc.py [capture.tshark.json]   (defaults to the hoxxling create-world capture)
"""
import json, struct, sys, os, collections
import sys as _sys, pathlib as _pl
_sys.path.insert(0, str(_pl.Path(__file__).resolve().parent))
from captures import capture_files, capture  # corpus lives in the ProjectFaolan-Data repo

CAP = sys.argv[1] if len(sys.argv) > 1 else capture(
    "Hoxxling_mage-heraldofxotli_create_modified_all_sliders-very_long_loading_screen-chat_message_before_exit.tshark.json")


def rv(b, p):
    s = v = 0
    while True:
        c = b[p]; p += 1; v |= (c & 0x7f) << s
        if not c & 0x80:
            return v, p
        s += 7


def main():
    dls = [p for p in json.load(open(CAP))["Packets"] if p["Source"]["Port"] == 7012 and p["IsDownload"]]
    recs = [bytes.fromhex(p["BodyHex"])[8:] for p in dls
            if (p.get("Opcode") or "").lower() == "0x2000" and p.get("BodyHex")
            and len(bytes.fromhex(p["BodyHex"])) >= 8
            and struct.unpack(">I", bytes.fromhex(p["BodyHex"])[4:8])[0] == 0x642CD3D6]

    groups = collections.Counter()
    print(f"BehaviorControlCenterIIR records: {len(recs)} in {os.path.basename(CAP)}\n")
    for body in recs:
        form = struct.unpack(">H", body[8:10])[0]
        if form != 0x0004:
            groups[(f"form=0x{form:04X}", "(distinct subtype)")] += 1
            continue
        msglen = struct.unpack(">I", body[14:18])[0]
        pb = body[18:18 + msglen]
        tail = body[18 + msglen:]
        p = 0; fields = []
        while p < len(pb):
            tag, p = rv(pb, p); fields.append(tag >> 3)
            if tag & 7 == 0:
                _, p = rv(pb, p)
            else:
                break
        kind = "long(88)" if len(tail) == 88 else (f"bound({len(tail)})" if tail else "no-tail")
        groups[(f"fields={tuple(fields)}", kind)] += 1

    for (fsig, kind), n in groups.most_common():
        print(f"  [{n:2}x]  {fsig:28} {kind}")


if __name__ == "__main__":
    main()
