#!/usr/bin/env python3
"""Decode GameActionListIIR_t (0xF508F4C1) — the per-dynel ACTION list (one of the three movement/AI
records in Gitea #190). Server->client it pushes the action list bound to a dynel (NPC/object): emote
loops ("idle2_emote"), the creation-step template choices ("MaleTemplate=50000:19688;..."), and numeric
action records (the 53/88-byte forms). It is NOT packet-construction so much as the SIMULATION layer's
output (the per-zone world model decides which action a dynel runs); see docs/archive/server-architecture-roadmap.md.

Wire (GameServer 0x2000 IIR frame): [len4][subop F508F4C1][ack 0000C350][dynelHandle4] then the body:

    u8   flag        = 0
    u32  head        = 0x000007E2            (constant; the same 0x7E2 "category" head seen elsewhere)
    u32  dynelHandle                          (the entity the action list belongs to; 0xCFxx-0xD0xx range)
    u32  field3                               (usually 0; sometimes a small id - a prev/seq token, undecoded)
    u32  four        = 4                       (constant)
    u64  one         = 1                       (constant)
    --- then ACTION RECORDS (per-action-type; tail length clusters at 21 / 53 / 88 / 103 ... bytes) ---
    Common tails seen: [u32][u32 9][u16 len][ascii action string] (emote / template list), and a numeric
    record form (positions/timers). The exact per-action grammar is the remaining decode (needs the
    GameActionListIIR_t reader; the 0x3F1 list-framing invariant applies, per ghidra_vtable_xref_readers).

This script validates the fixed HEADER across every captured instance (the decoded part) and tallies the
action-tail shapes (the remaining part). Run: python3 Captures/decode_gameactionlist.py
"""
import json, glob, binascii, struct, collections, sys

SUBOP = "f508f4c1"


def u32(b, o): return struct.unpack_from(">I", b, o)[0]
def u16(b, o): return struct.unpack_from(">H", b, o)[0]
def u64(b, o): return struct.unpack_from(">Q", b, o)[0]


def instances():
    for f in sorted(glob.glob("Captures/*.tshark.json")):
        try:
            d = json.load(open(f))["Packets"]
        except Exception:
            continue
        for p in d:
            bh = (p.get("BodyHex") or "").lower()
            if bh[8:16] == SUBOP:
                yield f.split("/")[-1][:28], p.get("Frame"), binascii.unhexlify(bh)


def parse_header(b):
    o = 16
    flag = b[o]; o += 1
    head = u32(b, o); o += 4
    dyn = u32(b, o); o += 4
    field3 = u32(b, o); o += 4
    four = u32(b, o); o += 4
    one = u64(b, o); o += 8
    ok = flag == 0 and head == 0x7E2 and four == 4 and one == 1
    return ok, dict(flag=flag, head=head, dyn=dyn, field3=field3, four=four, one=one), o


def main():
    total = ok = 0
    tails = collections.Counter()
    samples = []
    for fn, fr, b in instances():
        total += 1
        good, h, o = parse_header(b)
        if good:
            ok += 1
            tail = b[o:]
            tails[len(tail)] += 1
            if len(samples) < 8 and len(set(s[0] for s in samples)) < 6:
                samples.append((len(tail), tail.hex()))
    print(f"GameActionListIIR instances: {total}")
    print(f"fixed header [00][7E2][dyn][field3][4][u64=1] holds: {ok}/{total}")
    print(f"action-tail length histogram: {tails.most_common(10)}")
    print("sample tails (len, hex):")
    for ln, hx in sorted(set(samples))[:8]:
        print(f"  {ln:3d}  {hx}")
    assert ok == total, "header grammar did not hold for every instance!"
    print("OK: header validated for every captured GameActionList.")


if __name__ == "__main__":
    main()
