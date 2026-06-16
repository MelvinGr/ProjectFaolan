#!/usr/bin/env python3
"""Dissector for the remaining fixed-grammar create-editor IIR records (issue #189 item 4), validated against
the WorldPrep + CreationScript golden bursts. Each is the IIR-carrier body (after [u32 len][u32 opcode]).

  ComboBookIIR     0xA8BBEB7F  [u32 type][u32 handle][u16 0x0001][u32 comboId]                          (14)
  StatUpdateIIR    0x96B8DC59  [u32 type][u32 handle][u32 0][u8 count][count x (u32 statId, u32 value)]
  SetMorphValueIIR 0xBADF5A4B  [u32 type][u32 handle][u16 0][u8 1+len][0x2A][name][f32 weight][u8 p][u32 q]

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


def main():
    wp = open("Faolan.Tests/Golden/WorldPrepBurst.bin", "rb").read()
    cs = open("Faolan.Tests/Golden/CreationScriptBurst.bin", "rb").read()
    for name, ophex, data, fn in [
        ("ComboBookIIR", "A8BBEB7F", wp, combo),
        ("StatUpdateIIR", "96B8DC59", cs, statupdate),
        ("SetMorphValueIIR", "BADF5A4B", cs, setmorph),
    ]:
        recs = list(iter_records(data, ophex))
        ok = 0
        for b in recs:
            try:
                fn(b)
                ok += 1
            except Exception as e:  # noqa
                print(f"  {name} NON-CONFORMING: {b.hex()} ({e})")
        print(f"{name}: {ok}/{len(recs)} conform")


if __name__ == "__main__":
    main()
