#!/usr/bin/env python3
"""Decode the Knubot-conversation + misc IIR ReadSubClass readers (Gitea #233 decode campaign, batch 3).

Readers recovered from AgeOfConan.exe via vtable-xref (Captures/ghidra/DumpVtableReaders.java, slot 8)
plus the shared helpers (the Knubot base header FUN_009a7e00, the PlayfieldProxy stream FUN_00a6a600,
the 0x3F1 list helpers FUN_0079eb70/FUN_0079ec30). Stream primitives as in decode_combat_world_iir.py
(ReadU32 = FUN_0048f900, ReadByte = FUN_00cba1b0, ReadU16 = the Knubot version word).

Two framings appear:
  * GameCharAgent dynel-style: [u32 len][u32 sub][u32 0xC350][u32 handle][u8 env][ reader ]
  * Knubot conversation:       ... [u8 env][u16 ver=1][u32 0xC350][u32 npcHandle][ reader ]
    (the KnubotBaseIIR_c header = version + the two conversation endpoints; the player handle is the
     carrier handle, the NPC handle rides in the header.)

Every reader below consumes EXACTLY to the declared packet length across every captured instance.
Run: python3 Captures/decode_iir_conversation_misc.py
"""
import json, struct, glob
from collections import Counter

U = 4
KNUBOT_HDR_END = 27           # env(1) @16 + u16 ver(2) + u32 c350(4) + u32 npcHandle(4) -> ends @27


def u32(b, o):
    return struct.unpack('>I', b[o:o + 4])[0]


def u16(b, o):
    return struct.unpack('>H', b[o:o + 2])[0]


def load():
    caps = {}
    for path in sorted(glob.glob('Captures/*.tshark.json')):
        try:
            d = json.load(open(path))
        except Exception:
            continue
        for p in d.get('Packets', []):
            b = bytes.fromhex(p.get('BodyHex', '') or '')
            if len(b) < 8:
                continue
            caps.setdefault(u32(b, 4), []).append(b)
    return caps


def report(caps, name, sub, fn):
    s = caps.get(sub, [])
    ok = 0
    for b in s:
        try:
            if fn(b) == len(b) == 4 + u32(b, 0):
                ok += 1
        except Exception:
            pass
    print(f"  {name:30} byte-exact {ok}/{len(s)}")
    return s and ok == len(s)


# ---- misc readers (dynel-style env byte) ----
def pet(b):                          # u32 mode + (mode in {0,1}: 2u32) + (mode==0: 3u32)
    o = 17
    m = u32(b, o); o += 4
    o += 2 * U if m in (0, 1) else 0
    o += 3 * U if m == 0 else 0
    return o


def feedback(b):                     # 3 u32 + bool byte
    return 17 + 3 * U + 1


def crime(b):                        # bool byte + (byte==1: u32)
    o = 17
    flag = b[o]; o += 1
    o += U if flag == 1 else 0
    return o


def killed_by(b):                    # version byte + (ver==1: 2 u32)
    o = 17
    v = b[o]; o += 1
    o += 2 * U if v == 1 else 0
    return o


def intended_teleport(b):            # PlayfieldProxy (ver byte 'b' + 8 u32 + bool) + bool
    return 17 + (1 + 8 * U + 1) + 1


def area_formula(b):                 # 0x3F1-framed pair-list + u32 + 0x3F1-framed triple-list + u32
    def lst(o, words):
        cnt = u32(b, o); o += 4
        assert cnt % 0x3F1 == 0, "not 0x3F1-framed"
        n = cnt // 0x3F1
        return o + (n - 1) * words * U
    o = 17
    o = lst(o, 2)
    o += U
    o = lst(o, 3)
    o += U
    return o


# ---- Knubot conversation readers ----
def knubot_answer(b):        return KNUBOT_HDR_END + U          # + u32
def knubot_openchat(b):      return KNUBOT_HDR_END + 1          # + bool byte
def knubot_finishtrade(b):   return KNUBOT_HDR_END + U + 8      # + u32 + u64


def knubot_appendtext(b):    # + u32 field + [u16 strlen][str = remoteformat XML] + bool byte
    o = KNUBOT_HDR_END + U
    slen = u16(b, o); o += 2 + slen
    return o + 1


def main():
    caps = load()
    results = []
    print("=== misc readers ===")
    results.append(report(caps, "PetIIR_c", 0x1F4CB0F7, pet))
    results.append(report(caps, "FeedbackIIR_t", 0xBA1FD222, feedback))
    results.append(report(caps, "CrimeIIR_c", 0xC7954504, crime))
    results.append(report(caps, "SimpleCharKilledByUpdateIIR_t", 0xB066AFC2, killed_by))
    results.append(report(caps, "IntendedTeleportIIR_c", 0x10D27BC0, intended_teleport))
    results.append(report(caps, "AreaFormulaIIR_t", 0xCE30BE4E, area_formula))
    print("=== Knubot conversation readers ===")
    results.append(report(caps, "KnubotAnswerIIR_c", 0x4D7D9123, knubot_answer))
    results.append(report(caps, "KnubotOpenChatWindowIIR_c", 0x045B0B0F, knubot_openchat))
    results.append(report(caps, "KnubotFinishTradeIIR_c", 0x7446F3FA, knubot_finishtrade))
    results.append(report(caps, "KnubotAppendTextIIR_c", 0x4A3D899A, knubot_appendtext))
    print()
    assert all(results), "a reader did not validate"
    print("OK: every reader consumes to the declared packet length for every captured instance.")


if __name__ == '__main__':
    main()
