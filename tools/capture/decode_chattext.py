#!/usr/bin/env python3
"""ChatTextIIR (0xE0B51102) dissector — the world/system chat line the server pushes into the chat window (#103).

Grammar (big-endian), validated to the packet end on the demol80 randomActions capture:
  [u32 len]          byte length of everything after this field
  [u32 0xE0B51102]   ChatTextIIR sub-opcode
  [u32 0x0000C350]   IIR marker (Character class)
  [u32 instance]     target GameInterface object (= receiver endpoint instance / ClientInstance)
  [u8  0x00]         lead flag (constant in the sole sample)
  [u16 xmlLen][remoteformat XML]
  [u8 0x00][u32 0x40000001]   trailer (constant; carried verbatim until a 2nd sample names the halves)

Mirrors the C# ChatTextCodec (Faolan.Core.Network/Codecs/Text/ChatTextCodec.cs), golden-tested in ChatTextCodecTests.

Usage: python3 tools/capture/decode_chattext.py [capture.tshark.json]
"""
import glob
import json
import struct
import sys

SUBOP = 'e0b51102'


def decode(body: bytes):
    assert len(body) >= 19, 'too short'
    iir_len = int.from_bytes(body[0:4], 'big')
    assert body[4:8].hex() == SUBOP, 'not ChatTextIIR'
    marker = int.from_bytes(body[8:12], 'big')
    instance = int.from_bytes(body[12:16], 'big')
    flag = body[16]
    xml_len = int.from_bytes(body[17:19], 'big')
    xml = body[19:19 + xml_len]
    tail = body[19 + xml_len:]
    assert iir_len == len(body) - 4, f'len mismatch {iir_len} vs {len(body) - 4}'
    assert marker == 0x0000C350, f'marker {marker:#x}'
    assert len(tail) == 5, f'tail {tail.hex()}'
    return dict(instance=instance, flag=flag, xml_len=xml_len, xml=xml.decode('latin1'), tail=tail.hex())


def main():
    caps = [sys.argv[1]] if len(sys.argv) > 1 else glob.glob('Captures/*.tshark.json')
    seen = 0
    for cap in caps:
        try:
            pk = json.load(open(cap))['Packets']
        except Exception:
            continue
        for p in pk:
            b = p.get('BodyHex', '')
            if SUBOP not in b.lower():
                continue
            d = decode(bytes.fromhex(b))
            seen += 1
            print(f"{cap.split('/')[-1]}  frame {p['Frame']}: instance=0x{d['instance']:08x} "
                  f"flag=0x{d['flag']:02x} xmlLen={d['xml_len']} tail={d['tail']}")
            print(f"    XML: {d['xml'][:140]}")
    print(f"\nValidated {seen} ChatTextIIR packet(s).")


if __name__ == '__main__':
    main()
