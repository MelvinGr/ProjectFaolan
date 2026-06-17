#!/usr/bin/env python3
"""NPC FA015807 (SimpleCharFullUpdate / entity Spawn) dissection workbench.

Companion to parse_spawn_scaffold.py (which nailed the PLAYER spawn). NPC spawns share the exact same
FA015807 grammar as the player; this tool splits an NPC body and decodes the part that carries NPC
identity/appearance/stats - the ProtoCoreCharacterIIR - so it can be sourced from the Npcs tables and the
`NpcSpawn.Data` blob retired (docs/spawn-dissection-plan.md Phase 4 / world-packets-construct-not-replay).

NPC body layout (offsets body-relative; the beach NPCs all share iirOff=297):
  [0:20]    header: bodyLen, subOp=FA015807, ack=0xC350, handle, fieldA
  [20:~66]  transform group 0x2C: 0A vec3(pos), 12 quat(rot), 18 playfield-varint
  [~66:293] pre-IIR block (273B fixed SIZE, per-NPC float CONTENT: extended transform / bounds - still raw)
  @297      ProtoCoreCharacterIIR (length-prefixed u32 @293) -> DECODED HERE:
              f1  name
              f4  CharacterStats   sex/side/race/monster_scale/monster_data/life/health/.../head_mesh/level/radius
              f5  NpcStats         f4=template_id (the NPC identity!), super_family, community, npc_flags, npc_type
              f7  CharacterBcc     idle/action anim ids
              f23 inventory_entry  worn equipment (humanoids only; absent on animals)
  [iirEnd:]  post-IIR scaffold (GRAMMAR CRACKED 2026-06-12): header [u32 0][u32 firstRecordLen=len-36][u32
             recordCount]; word0=0 on ALL 42 (player has 2), recordCount=1 on 41/42. The MINIMAL freshly-spawned
             form is a CONSTANT 41B (23/42 NPCs, single distinct value) = the 0x3F1-marked empty pair lists; it
             is what from-scratch construction emits (-> NpcSpawnScaffold.BuildMinimalPostIir in C#). The larger
             blocks concatenate RUNTIME-STATE records past the first (current movement / waypoint vec3 BE float
             triples + active buff/timer pairs) - session state, not template data, so NOT reproduced at spawn.

Usage:
  python3 Captures/scripts/dissect_npc_spawn.py                 # table of every NPC spawn in the Strie login burst
  python3 Captures/scripts/dissect_npc_spawn.py one <handle-hex> # full field dump of one NPC (e.g. 818f7)
  python3 Captures/scripts/dissect_npc_spawn.py <capture.tshark.json>   # table for another capture
"""
import importlib.util
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('c', ROOT / 'Captures/scripts/compare_log_to_dump.py')
cdt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cdt)
ss = importlib.util.spec_from_file_location('s', ROOT / 'Captures/scripts/parse_spawn_scaffold.py')
ps = importlib.util.module_from_spec(ss)
ss.loader.exec_module(ps)

DEFAULT_CAP = 'Captures/Strie_rogue-assassin_login.tshark.json'
INSTANCE_RANGE_START = 0x02000000  # >= this handle is the player's own spawn, not an NPC

# CharacterStats (IIR f4) field names, from ProtoCoreCharacterIIR.CharacterStats in AocProtocol.g.cs.
STATS = {1: 'sex', 2: 'side', 3: 'race', 4: 'monster_scale', 5: 'monster_data', 6: 'life', 7: 'health',
         8: 'max_stamina', 9: 'stamina', 10: 'max_mana', 11: 'mana', 12: 'looks_package', 13: 'mount_mesh',
         14: 'mount_scale', 15: 'mount_flags', 16: 'flags', 17: 'head_mesh', 18: 'level',
         19: 'apprentice_level', 20: 'radius(zz)', 21: 'f21', 22: 'generic_stats'}
NPCSTATS = {1: 'sight_skill', 2: 'hearing_skill', 3: 'smelling_skill', 4: 'template_id', 5: 'super_family',
            6: 'community', 7: 'los_height', 8: 'spawn_animation', 9: 'npc_flags', 10: 'normalset',
            11: 'profession_name', 12: 'profession_type', 13: 'faction_id', 14: 'npc_type'}


def u32(b, i):
    return int.from_bytes(b[i:i + 4], 'big')


def rv(b, i):
    v = s = 0
    while True:
        x = b[i]
        v |= (x & 0x7F) << s
        i += 1
        s += 7
        if not x & 0x80:
            return v, i


def fields(b):
    """Shallow protobuf walk -> {fieldNumber: (wiretype, value)}; messages/strings as raw bytes."""
    out = {}
    i = 0
    while i < len(b):
        tag, i = rv(b, i)
        f, wt = tag >> 3, tag & 7
        if wt == 0:
            v, i = rv(b, i)
        elif wt == 2:
            ln, i = rv(b, i)
            v = b[i:i + ln]
            i += ln
        elif wt == 5:
            v = b[i:i + 4]
            i += 4
        elif wt == 1:
            v = b[i:i + 8]
            i += 8
        else:
            break
        out[f] = (wt, v)
    return out


def npc_spawns(cap):
    spawns = [p[4] for p in cdt.from_dump(cap)
              if p[2] == 0x2000 and len(p[4]) >= 16 and p[4][4:8].hex() == 'fa015807']
    out = []
    for b in spawns:
        h = u32(b, 12)
        if h >= INSTANCE_RANGE_START:
            continue  # player's own spawn
        inner = ps.find_character_iir(b)
        if inner is None:
            continue
        off, ln = inner
        out.append((h, b, off, ln))
    return out


def stats_of(b, off, ln):
    iir = fields(b[off:off + ln])
    name = iir[1][1].decode('ascii', 'replace') if 1 in iir else '?'
    st = fields(iir[4][1]) if 4 in iir else {}
    ns = fields(iir[5][1]) if 5 in iir else {}
    template = int.from_bytes(ns[4][1], 'little') if 4 in ns else (ns.get(4, (0, 0))[1])
    return name, st, ns, template, (23 in iir)


def cmd_table(cap):
    rows = npc_spawns(cap)
    print(f'{len(rows)} NPC spawns in {Path(cap).name}\n')
    hdr = f'{"handle":>9} {"name":22} {"tmpl":>6} {"race":>4} {"sex":>3} {"scale":>5} {"mdata":>8} {"lvl":>3} {"head_mesh":>9} {"equip":>5} {"iirLen":>6} {"post":>4}'
    print(hdr)
    print('-' * len(hdr))
    for h, b, off, ln in rows:
        name, st, ns, tmpl, eq = stats_of(b, off, ln)
        def s(d, k):
            return d[k][1] if k in d else ''
        print(f'  0x{h:05X} {name:22} {tmpl:>6} {s(st,3):>4} {s(st,1):>3} {s(st,4):>5} {s(st,5):>8} {s(st,18):>3} {s(st,17):>9} {("yes" if eq else "no"):>5} {ln:>6} {len(b)-off-ln:>4}')


def cmd_one(cap, handle_hex):
    h = int(handle_hex, 16)
    rows = npc_spawns(cap)
    h, b, off, ln = next(r for r in rows if r[0] == h)
    name, st, ns, tmpl, eq = stats_of(b, off, ln)
    print(f'NPC 0x{h:05X} {name!r}  bodyLen={len(b)} iirOff={off} iirLen={ln} postIir={len(b)-off-ln}')
    print(f'  template_id={tmpl}  has_equipment={eq}')
    print('  CharacterStats (IIR f4):')
    for k in sorted(st):
        print(f'    f{k:<2} {STATS.get(k,"?"):16} = {st[k][1]}')
    print('  NpcStats (IIR f5):')
    for k in sorted(ns):
        v = int.from_bytes(ns[k][1], "little") if k == 4 else ns[k][1]
        print(f'    f{k:<2} {NPCSTATS.get(k,"?"):16} = {v}')
    print(f'  pre-IIR block [66:{off-4}] ({off-4-66}B, raw):')
    for o in range(66, off - 4, 16):
        print(f'    {o:4} {b[o:min(o+16,off-4)].hex(" ")}')
    print(f'  post-IIR scaffold ({len(b)-off-ln}B, raw):')
    post = b[off + ln:]
    for o in range(0, len(post), 16):
        print(f'    {o:4} {post[o:o+16].hex(" ")}')


if __name__ == '__main__':
    args = sys.argv[1:]
    cap = DEFAULT_CAP
    if args and args[0] == 'one':
        cmd_one(cap if len(args) < 3 else args[2], args[1])
    elif args and args[0].endswith('.json'):
        cmd_table(args[0])
    else:
        cmd_table(cap)
