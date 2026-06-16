#!/usr/bin/env python3
"""Dissector for the remaining fixed-grammar create-editor IIR records (issue #189 item 4), validated against
the WorldPrep + CreationScript golden bursts. Each is the IIR-carrier body (after [u32 len][u32 opcode]).

  ComboBookIIR     0xA8BBEB7F  [u32 type][u32 handle][u16 0x0001][u32 comboId]                          (14)
  StatUpdateIIR    0x96B8DC59  [u32 type][u32 handle][u32 0][u8 count][count x (u32 statId, u32 value)]
  SetMorphValueIIR 0xBADF5A4B  [u32 type][u32 handle][u16 0][u8 1+len][0x2A][name][f32 weight][u8 p][u32 q]
  BehaviorControlCenterIIR 0x642CD3D6 long form (116): 28-byte short-form head + an 88-byte item-bound tail
                   head = [u32 type][u32 handle][u16 0x0004][u32 slot][u32 msgLen][protobuf]
                   tail = [u32 index][u32 someId][u32 itemHandle][u32 itemHandle][u32 0][u32 1][char[4] tag]
                          [byte[16] transform][byte[16] transform][8 ownerStamp][8 ownerStamp][12 zero]

Usage: python3 Captures/decode_create_iir_records.py
"""
import struct
import sys

CHAR_TYPE = 0x0000C350


def iter_records(stream, ophex):
    op = bytes.fromhex(ophex)
    pos = 0
    while True:
        j = stream.find(op, pos)
        if j < 0:
            return
        pos = j + 1
        if j < 4:
            continue
        (length,) = struct.unpack(">I", stream[j - 4:j])
        if length < 4 or length > 5000 or j + 4 + length - 4 > len(stream):
            continue
        yield stream[j + 4:j + 4 + length - 4]


def combo(b):
    assert len(b) == 14
    type_, handle = struct.unpack(">II", b[0:8])
    flag = struct.unpack(">H", b[8:10])[0]
    combo_id = struct.unpack(">I", b[10:14])[0]
    assert type_ == CHAR_TYPE and flag == 1
    return handle, combo_id


def statupdate(b):
    type_, handle, zero = struct.unpack(">III", b[0:12])
    count = b[12]
    assert type_ == CHAR_TYPE and zero == 0 and len(b) == 13 + 8 * count
    stats = [struct.unpack(">II", b[13 + 8 * i:21 + 8 * i]) for i in range(count)]
    return handle, stats


def setmorph(b):
    type_, handle = struct.unpack(">II", b[0:8])
    u16 = struct.unpack(">H", b[8:10])[0]
    blob_len = b[10]
    assert type_ == CHAR_TYPE and u16 == 0 and b[11] == 0x2A
    name = b[12:12 + blob_len - 1].decode("ascii")
    off = 12 + blob_len - 1
    weight = struct.unpack(">f", b[off:off + 4])[0]
    p = b[off + 4]
    q = struct.unpack(">I", b[off + 5:off + 9])[0]
    assert off + 9 == len(b)
    return handle, name, weight, p, q


OWNER_STAMP = bytes.fromhex("b290805c29e627ca")


def behavior_long(b):
    # The short and long forms share the iir type; skip the 28-byte short form, decode only the 116-byte long.
    if len(b) != 116:
        return None
    type_, handle = struct.unpack(">II", b[0:8])
    u16, slot, msglen = struct.unpack(">HII", b[8:18])
    pb = b[18:18 + msglen]
    tail = b[18 + msglen:]
    assert type_ == CHAR_TYPE and u16 == 0x0004 and slot == 9 and msglen == 10 and len(tail) == 88
    index, some_id, ih1, ih2, c0, c1 = struct.unpack(">IIIIII", tail[0:24])
    tag = tail[24:28]
    tr1, tr2 = tail[28:44], tail[44:60]
    fx1, fx2 = tail[60:68], tail[68:76]
    zeros = tail[76:88]
    assert ih1 == ih2 and c0 == 0 and c1 == 1 and tr1 == tr2 and fx1 == fx2 == OWNER_STAMP
    assert zeros == bytes(12)
    return handle, index, some_id, ih1, tag.decode("ascii"), tr1.hex()


def main():
    wp = open("Faolan.Tests/Golden/WorldPrepBurst.bin", "rb").read()
    cs = open("Faolan.Tests/Golden/CreationScriptBurst.bin", "rb").read()
    for name, ophex, data, fn in [
        ("ComboBookIIR", "A8BBEB7F", wp, combo),
        ("StatUpdateIIR", "96B8DC59", cs, statupdate),
        ("SetMorphValueIIR", "BADF5A4B", cs, setmorph),
        ("BehaviorControlCenterIIR(long)", "642CD3D6", wp, behavior_long),
    ]:
        recs = list(iter_records(data, ophex))
        ok = applicable = 0
        for b in recs:
            try:
                if fn(b) is None:
                    continue  # not applicable to this decoder (e.g. the behavior short form)
                applicable += 1
                ok += 1
            except Exception as e:  # noqa
                applicable += 1
                print(f"  {name} NON-CONFORMING: {b.hex()} ({e})")
        print(f"{name}: {ok}/{applicable} conform")


if __name__ == "__main__":
    main()
