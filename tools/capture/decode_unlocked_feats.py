#!/usr/bin/env python3
"""Decode a character's UNLOCKED FEAT SET out of the capture dumps (Gitea #251).

THE DUMPS DO CARRY THE FULL UNLOCKED FEAT SET. It rides the player spawn (FA015807), NOT the FullChar
snapshot (0x737A6DF9) that was previously suspected. It sits in the spawn's PRE-IIR region - the block
parse_spawn_scaffold.py deliberately keeps RAW ("its internal structure is analyzed separately"), which is
why earlier passes (decode_skill_scaffold.py reads the POST-IIR skill idList) never saw it and the subsystem was
believed blocked-on-a-missing-capture.

Grammar (pre-IIR, body-relative; a 0x07E2 landmark sits a fixed 9 bytes before the count marker):
    ... 07 E2  00*9  [u32 (count+1)*0x3F1]  [count x u32 featId BE]  00...
  count == 0 -> the bare 0x3F1 marker, no ids (fresh char / all feats untrained).

Proof it is the feat set (not a coincidence):
  - Training the Hellfire feat (id 112700) makes 112700 APPEAR in this list: the hakotep pre-training login
    carries 5 feats (Hellfire absent); the post-training gameplay spawn carries the same 5 + 112700.
  - Same id-space as FeatIIR (0x28DA68A0): Hoxxling's spawn list == its FeatIIR list == [110000,112700,410600].
  - The low 2 digits track RANK: hakotep trained feat base 1100 to rank 4 -> spawn shows 110004 while a
    rank-0 char shows 110000 (FeatIIR train REQUEST uses the base id). featId = featBase*100 + rank (hypothesis).

This is the ABILITY/talent FEAT tree, a DIFFERENT id-space and region from:
  - the POST-IIR skill scaffold (decode_skill_scaffold.py; ids 4300/7700/... -> CharacterSkill), and
  - the FullChar snapshot known-spell list (type-2 spell / type-5 ability records).

Usage:
  python3 tools/capture/decode_unlocked_feats.py                 # per-capture feat set (skips fresh/empty)
  python3 tools/capture/decode_unlocked_feats.py all             # include empty (fresh) spawns
  python3 tools/capture/decode_unlocked_feats.py perchar         # the largest feat set seen per character
  python3 tools/capture/decode_unlocked_feats.py dump <cap> <idx> # hex of the feat region for one spawn
"""
import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent  # repo root (this file is at tools/capture/)

sys.path.insert(0, str(Path(__file__).resolve().parent))
from captures import capture_files  # noqa: E402  (needs the sys.path line above)
_spec = importlib.util.spec_from_file_location('pss', ROOT / 'tools/capture/parse_spawn_scaffold.py')
pss = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(pss)


def u32(b, i):
    return int.from_bytes(b[i:i + 4], 'big')


def decode_feat_list(body):
    """Return (count, [featId...], markerOffset) or None. Anchored on the 0x07E2 landmark within the pre-IIR
    region (before the ProtoCoreCharacterIIR), followed by a u32 that is a positive multiple of 0x3F1."""
    inner = pss.find_character_iir(body)
    limit = inner[0] if inner else len(body)  # only scan the pre-IIR region
    lm = body.find(b'\x07\xe2')
    while lm != -1 and lm < limit:
        for off in range(lm + 2, min(lm + 14, len(body) - 3)):
            m = u32(body, off)
            if m and m % 0x3F1 == 0:
                count = m // 0x3F1 - 1
                if off + 4 + 4 * count > len(body):
                    continue
                ids = [u32(body, off + 4 + 4 * k) for k in range(count)]
                if count == 0 or all(1000 <= x <= 9_999_999 for x in ids):
                    return count, ids, off
        lm = body.find(b'\x07\xe2', lm + 1)
    return None


def spawns(cap):
    try:
        return list(enumerate(pss.spawns_in_capture(cap, player_only=True)))
    except Exception:
        return []


def cmd_list(include_empty=False):
    print(f'{"capture":58} {"idx":>3} {"cnt":>3}  feats')
    for cap in [Path(f) for f in capture_files()]:
        for idx, body in spawns(cap):
            r = decode_feat_list(body)
            if r is None:
                continue
            count, ids, _ = r
            if count == 0 and not include_empty:
                continue
            print(f'{cap.name[:58]:58} {idx:>3} {count:>3}  {ids if count else "(empty)"}')


def cmd_perchar():
    """The largest feat set observed per character (character = first token of the capture name)."""
    best = {}
    for cap in [Path(f) for f in capture_files()]:
        who = cap.name.split('_')[0].split('-')[0]
        for idx, body in spawns(cap):
            r = decode_feat_list(body)
            if r is None:
                continue
            count, ids, _ = r
            if who not in best or count > best[who][0]:
                best[who] = (count, ids, f'{cap.name}[{idx}]')
    print(f'{"character":16} {"cnt":>3}  source')
    for who in sorted(best):
        count, ids, src = best[who]
        print(f'{who:16} {count:>3}  {src}')
        if count:
            print(f'                     {ids}')


def cmd_dump(cap, idx):
    body = pss.spawns_in_capture(Path(cap), player_only=True)[int(idx)]
    inner = pss.find_character_iir(body)
    r = decode_feat_list(body)
    print(f'{cap}[{idx}] len={len(body)} iirOff={inner[0] if inner else "?"}')
    if r:
        count, ids, off = r
        print(f'  marker@{off}={u32(body, off):#x} count={count} feats={ids}')
        s = max(0, off - 12)
        for o in range(s, off + 4 + 4 * count + 8, 16):
            print(f'    {o:5} | {body[o:o+16].hex(" ")}')
    else:
        print('  no feat list found')


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'list'
    if cmd == 'all':
        cmd_list(include_empty=True)
    elif cmd == 'perchar':
        cmd_perchar()
    elif cmd == 'dump':
        cmd_dump(sys.argv[2], sys.argv[3])
    else:
        cmd_list()
