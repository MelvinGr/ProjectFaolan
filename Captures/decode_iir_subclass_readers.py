#!/usr/bin/env python3
"""Validate the IIR ReadSubClass grammars recovered by VTABLE-xref (Gitea #187 / #53 / #190).

The readers for SimpleDynel / LootBag / MoveTask / GameActionList / BehaviorControlCenter lack the verbose
"bad stream" assert that anchored the FA015807 decode, so they were found via the vtable instead: ReadSubClass
is vtable slot 8 (proven against the known SimpleChar reader); reading slot 8 of each class's `::vftable` gives
that class's reader (Captures/ghidra/DumpVtableReaders.java). Grammars in docs/iir-subclass-readers.md.

This validates the one with a clean, checkable framing from captures: SimpleDynelFullUpdateIIR (0x66AEDD50).
Its ReadSubClass = ReadLengthPrefixedBlob (FUN_00cbfb80: [u32 len][bytes]) of the ProtoSimpleDynelIIR protobuf,
then the kd-tree/bounds struct (FUN_0080c620). On the wire, after the record header + the base-class transform:

  [u32 type][u32 handle][u32 0][varint tlen][ProtoGameCodeDynelIIR transform][u32 blobLen][ProtoSimpleDynelIIR]...

Run:  python3 Captures/decode_iir_subclass_readers.py [capture.tshark.json ...]
"""
import json, struct, sys, glob

DYNEL = 0x66AEDD50


def varint(b, o):
    v = s = 0
    while True:
        x = b[o]; o += 1
        v |= (x & 0x7f) << s
        if not x & 0x80:
            return v, o
        s += 7


def run(path):
    d = json.load(open(path))
    pk = d['Packets'] if isinstance(d, dict) else d
    tot = okframe = okproto = 0
    for p in pk:
        if not (p.get('IsDownload') and p.get('Opcode') == '0x2000'):
            continue
        b = bytes.fromhex(p['BodyHex'])
        if len(b) < 8:
            continue
        ln, it = struct.unpack('>II', b[0:8])
        if ln != len(b) - 4 or it != DYNEL:
            continue
        rec = b[8:]; tot += 1
        try:
            o = 12                       # [type][handle][u32 0]
            tlen, o = varint(rec, o); o += tlen   # base-class transform (ProtoGameCodeDynelIIR)
            blobLen = struct.unpack('>I', rec[o:o + 4])[0]; o += 4
            blob = rec[o:o + blobLen]
            if 0 < blobLen < len(rec) and blob and blob[0] == 0x08:  # protobuf field 1 = kdtree (varint)
                okframe += 1
                if b'SimpleDynel' in blob or 0x12 in blob[:8]:       # field 2 = name
                    okproto += 1
        except Exception:
            pass
    if tot:
        print(f'{path.split("/")[-1]}')
        print(f'  SimpleDynel 0x66AEDD50 records: {tot}   [u32 blobLen]+kdtree-field framing: {okframe}/{tot}   ProtoSimpleDynelIIR name: {okproto}/{tot}')


if __name__ == '__main__':
    paths = sys.argv[1:] or sorted(glob.glob('Captures/*login*.tshark.json'))[:8]
    for p in paths:
        try:
            run(p)
        except Exception as e:
            print(f'{p}: ERROR {e}')
