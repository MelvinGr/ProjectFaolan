#!/usr/bin/env python3
"""Read a character's UNLOCKED FEAT LIST out of the capture dumps (Gitea #251, feat-tree subsystem).

The feat/ability list a high-level character carries is the tail ID-list of its own FA015807 player spawn:
5-byte `[u8 rank][u32 featId]` groups, ascending featId, terminated by id 0x3F1 (the same list the C# codec
PlayerSpawnScaffoldCodec parses into ScaffoldIdEntry(Rank, Id), and that PlayerSpawnScaffoldRows maps to
CharacterSkill(SkillId, Rank)). This tool surfaces that list per character straight from the captures - the
source the hand-transcribed PlayerSpawnScaffoldRows.FullSkillIdsByClass came from, but WITH the rank byte that
the hand list dropped.

The character's CLASS is self-identified: the FIRST feat id is the profession feat == (Class enum value) * 100
(Necromancer 0x29 -> 4100, Demonologist 0x2C -> 4400, Assassin 0x22 -> 3400, HeraldOfXotli 0x2B -> 4300, ...).

NB the scaffold feat ids are GDB ability ids (302303, 1445000, ...); they do NOT join the RDB 1000020 feat
CATALOG id-space (2353, 2964, ...) - the spell<->feat and id<->name maps live server-side and are never streamed
(see the feat_trees_decode_251 memory). So this reads the unlocked feat STATE (id + rank); resolving names/trees
is a later step (the wiki tree structure + a feat-window 0x207E capture).

Usage:
  python3 Captures/scripts/decode_feats.py                 # per-class best (largest) feat dump across all captures
  python3 Captures/scripts/decode_feats.py discover         # every player spawn: (capture, idx, class, #feats)
  python3 Captures/scripts/decode_feats.py dump <capture> [spawnIdx=0]   # full (rank, id) list for one spawn
"""
import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent  # repo root (this file is at Captures/scripts/)

# Reuse the proven scaffold parser (spawns_in_capture + parse_player_spawn) rather than re-deriving the grammar.
_spec = importlib.util.spec_from_file_location('pss', ROOT / 'Captures/scripts/parse_spawn_scaffold.py')
pss = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(pss)

# Class enum (Faolan.Core/Enums/Class.cs) keyed by its byte value == profession-feat-id // 100.
CLASS_BY_CODE = {
    0x18: 'PriestOfMitra', 0x1C: 'TempestOfSet', 0x1D: 'BearShaman', 0x16: 'Conqueror',
    0x1F: 'DarkTemplar', 0x14: 'Guardian', 0x12: 'Barbarian', 0x27: 'Ranger',
    0x22: 'Assassin', 0x2B: 'HeraldOfXotli', 0x2C: 'Demonologist', 0x29: 'Necromancer',
}


def feats_of_spawn(body):
    """Return (class_name, [(featId, rank), ...]) for one FA015807 player-spawn body, or (None, []) on parse fail."""
    try:
        p = pss.parse_player_spawn(body)
    except Exception:
        return None, []
    # parse_player_spawn stores idList as (rank_byte, featId) tuples.
    feats = [(ident, rank) for rank, ident in p['idList']]
    if not feats:
        return None, []
    cls = CLASS_BY_CODE.get(feats[0][0] // 100)
    return cls, feats


def player_spawns(cap):
    """Yield (idx, body) for each player spawn in a capture, tolerating captures with none."""
    try:
        bodies = pss.spawns_in_capture(cap, player_only=True)
    except Exception:
        return
    for idx, body in enumerate(bodies):
        yield idx, body


def cmd_discover():
    rows = []
    for cap in sorted((ROOT / 'Captures').glob('*.tshark.json')):
        for idx, body in player_spawns(cap):
            cls, feats = feats_of_spawn(body)
            if feats:
                trained = sum(1 for _, r in feats if r != 0)
                rows.append((len(feats), trained, cls or '?', cap.name, idx))
    rows.sort(reverse=True)
    print(f'{"#feats":>6} {"trained":>7}  {"class":<14} capture[idx]')
    for n, trained, cls, name, idx in rows:
        print(f'{n:>6} {trained:>7}  {cls:<14} {name}[{idx}]')


def cmd_dump(cap, idx=0):
    bodies = pss.spawns_in_capture(Path(cap), player_only=True)
    cls, feats = feats_of_spawn(bodies[int(idx)])
    print(f'{cap} [spawn {idx}] -> class={cls}  ({len(feats)} feats, {sum(1 for _, r in feats if r)} trained)')
    print(f'  {"featId":>8} {"hex":>9}  rank')
    for ident, rank in feats:
        mark = '' if rank == 0 else '  <- trained'
        print(f'  {ident:>8} {ident:>#9x}  0x{rank:02x}{mark}')


def cmd_perclass():
    # Pick, per class, the player spawn with the MOST feats (the most-progressed capture for that class).
    best = {}
    for cap in sorted((ROOT / 'Captures').glob('*.tshark.json')):
        for idx, body in player_spawns(cap):
            cls, feats = feats_of_spawn(body)
            if cls and (cls not in best or len(feats) > len(best[cls][2])):
                best[cls] = (cap.name, idx, feats)
    print(f'{"class":<14} {"#feats":>6} {"trained":>7}  source')
    for cls in sorted(best):
        name, idx, feats = best[cls]
        trained = sum(1 for _, r in feats if r != 0)
        print(f'{cls:<14} {len(feats):>6} {trained:>7}  {name}[{idx}]')


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'perclass'
    if cmd == 'discover':
        cmd_discover()
    elif cmd == 'dump':
        cmd_dump(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else 0)
    else:
        cmd_perclass()
