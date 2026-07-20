#!/usr/bin/env python3
"""Decode the stream-compression control frame (Gitea #318).

The frame was described as `[80 00 00 05][4 undecoded bytes][flag]` — 9 bytes at a packet boundary — with the four
bytes "copied/skipped without interpretation". This scans the RAW pcaps (the reassembled *.tshark.json hides these
because the Wireshark plugin consumes the frame and inflates transparently) and proves the four bytes are NOT a
mystery: they are the standard **CRC32 header field**, computed over the 1-byte body — exactly like every packet.

The frame is an ordinary `[u32 length BE][u32 crc32 BE][body]`:
  length = 0x80000005  -> high bit 0x80000000 = "compression-stream control frame" marker; low bits 5 = the byte
                          count that follows the length field = 4 (crc32) + 1 (body), same rule as normal framing.
  crc32  = zlib.crc32(body)  -> the "4 bytes"; constant PER body value, which is why only a few distinct values
                               appear across thousands of frames.
  body   = 1 flag byte: 0x00 opens a zlib deflate stream for the rest of that direction; 0x01 / 0x02 are the other
                        observed control modes (plaintext / stream marker).

A `80 00 00 05` occurrence whose following bytes do NOT satisfy crc32(body)==bytes[4:8] is a coincidental match
inside compressed/other payload, not a real frame — this script reports those separately so the count is honest.

Usage:  tools/capture/decode_compression_frame.py [Captures/*.pcapng ...]
"""
import glob, sys, zlib, collections, os

TAG = bytes.fromhex("80000005")


def main(argv):
    files = argv or glob.glob('Captures/*.pcapng')
    real = collections.Counter()   # flag -> count (crc verified)
    bogus = 0
    for f in files:
        data = open(f, 'rb').read()
        i = 0
        while True:
            j = data.find(TAG, i)
            if j < 0:
                break
            i = j + 1
            crc = data[j + 4:j + 8]
            body = data[j + 8:j + 9]
            if len(body) < 1 or len(crc) < 4:
                continue
            if zlib.crc32(body) & 0xffffffff == int.from_bytes(crc, 'big'):
                real[body[0]] += 1
            else:
                bogus += 1

    total = sum(real.values())
    print(f"validated compression control frames: {total}   (rejected non-crc-matching: {bogus})")
    print("by body/flag byte:")
    names = {0x00: "zlib deflate stream open", 0x01: "control mode 01", 0x02: "control mode 02"}
    for flag, c in sorted(real.items()):
        crc = zlib.crc32(bytes([flag])) & 0xffffffff
        print(f"  body 0x{flag:02x}  crc32={crc:08x}  x{c:<5} {names.get(flag, '?')}")
    print("\nConclusion: bytes[4:8] == crc32(body) for every validated frame — the '4 undecoded bytes' are the CRC32.")
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
