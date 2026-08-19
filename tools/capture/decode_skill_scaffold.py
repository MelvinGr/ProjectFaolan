#!/usr/bin/env python3
"""Read a character's ABILITY/SKILL SCAFFOLD out of the capture dumps (Gitea #251).

This reads the SKILL/ability scaffold, NOT the feat tree (this file was formerly mis-named `decode_feats.py`).
The scaffold is the player spawn's POST-IIR id-list; the FEAT tree is a different region + id-space (the spawn's
PRE-IIR feat list - see `decode_unlocked_feats.py` - plus the FeatIIR 0x28da68a0 train record). Feat ids
(112700/109488/410600/80004/...) are ABSENT here. Proof: unlocking the Hellfire feat left this scaffold
byte-identical (the pre-IIR feat list is what changed). See the feat_trees_decode_251 memory. This tool + the
scaffold still matter (the list gates spell usability, #298), it's just skills, not feats.

The ability/skill list a high-level character carries is the SKILL list of its own FA015807 player spawn.

!! CARVE CORRECTION (Gitea #466): the client reads this list as a ListFrame'd `[u32 id][u8 rank]` - the id FIRST -
opening with the archetype ability (100/200/300/400) and framed by the word the old carve called "the closer tag".
This tool still parses the OLD 8-bytes-off grouping (`[u8 rank][u32 id]` behind a trailing terminator), which walks
the same bytes but pairs each rank with the WRONG NEIGHBOUR (the id after it) and drops the first skill. The
per-id output below is therefore shifted by one position against the client's own pairing - re-measure before
trusting any (id, rank) association from here; the C# model (SpawnEntrySlot.Skills / CharacterSkill) carries the
client's pairing. The id SET (bar the archetype ability) and the byte-walk are unaffected.

This tool surfaces that list per character straight from the captures - the source the hand-transcribed
PlayerSpawnScaffoldRows.FullSkillIdsByClass came from, but WITH the rank byte that the hand list dropped.

The character's CLASS is self-identified: the FIRST feat id is the profession feat == (Class enum value) * 100
(Necromancer 0x29 -> 4100, Demonologist 0x2C -> 4400, Assassin 0x22 -> 3400, HeraldOfXotli 0x2B -> 4300, ...).

The 5-byte group's leading byte was measured as a per-FEAT TAG, not a per-character trained rank - but NOTE that
measurement used the old pairing (see the carve correction above), so "per feat id" really means "per the id one
position over": across all captures it is INVARIANT per feat id (153/156 distinct ids carry one value; only 3 vary, all
between 0x00 and their nonzero tag). It is 0x00 on a base-scaffold entry and a feat-specific nonzero tag once the
feat is trained (e.g. 326000 is always 0x49, 331300 always 0x0a). Bands seen: 0x03 (the 1.4M-range spell-grant
feats), 0x08, 0x0a, and a 0x41..0x4b band on the mid-tier feat-tree feats. Its exact meaning (tier / tree-tab /
points) still needs the wiki or a feat-window 0x207E capture; here it is surfaced verbatim as `tag`.

NB the scaffold feat ids are GDB ability ids (302303, 1445000, ...); they do NOT join the RDB 1000020 feat
CATALOG id-space (2353, 2964, ...) - the spell<->feat and id<->name maps live server-side and are never streamed
(see the feat_trees_decode_251 memory). So this reads the SKILL scaffold STATE (id + tag); resolving names/trees
is a later step. (For the actual unlocked FEAT set, use `decode_unlocked_feats.py`.)

Usage:
  python3 tools/capture/decode_skill_scaffold.py                 # per-class best (largest) scaffold across all captures
  python3 tools/capture/decode_skill_scaffold.py discover         # every player spawn: (capture, idx, class, #ids)
  python3 tools/capture/decode_skill_scaffold.py dump <capture> [spawnIdx=0]   # full (tag, id) list for one spawn
  python3 tools/capture/decode_skill_scaffold.py json [outfile]   # structured per-class catalog {class:[{id,tag}]}
"""
import importlib.util
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent  # repo root (this file is at tools/capture/)

sys.path.insert(0, str(Path(__file__).resolve().parent))
from captures import capture_files  # noqa: E402  (needs the sys.path line above)

# Reuse the proven scaffold parser (spawns_in_capture + parse_player_spawn) rather than re-deriving the grammar.
_spec = importlib.util.spec_from_file_location('pss', ROOT / 'tools/capture/parse_spawn_scaffold.py')
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
    for cap in [Path(f) for f in capture_files()]:
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
    for cap in [Path(f) for f in capture_files()]:
        for idx, body in player_spawns(cap):
            cls, feats = feats_of_spawn(body)
            if cls and (cls not in best or len(feats) > len(best[cls][2])):
                best[cls] = (cap.name, idx, feats)
    print(f'{"class":<14} {"#feats":>6} {"trained":>7}  source')
    for cls in sorted(best):
        name, idx, feats = best[cls]
        trained = sum(1 for _, r in feats if r != 0)
        print(f'{cls:<14} {len(feats):>6} {trained:>7}  {name}[{idx}]')


def cmd_json(outfile=None):
    # Per class, the most-progressed player spawn's feat list, as structured records (the seed for a DB feat model).
    best = {}
    for cap in [Path(f) for f in capture_files()]:
        for idx, body in player_spawns(cap):
            cls, feats = feats_of_spawn(body)
            if cls and (cls not in best or len(feats) > len(best[cls]['feats'])):
                best[cls] = {'source': f'{cap.name}[{idx}]', 'feats': feats}
    catalog = {
        cls: {
            'source': best[cls]['source'],
            'feats': [{'id': fid, 'tag': tag} for fid, tag in best[cls]['feats']],
        }
        for cls in sorted(best)
    }
    text = json.dumps(catalog, indent=2)
    if outfile:
        Path(outfile).write_text(text)
        print(f'wrote {outfile} ({len(catalog)} classes)')
    else:
        print(text)


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'perclass'
    if cmd == 'discover':
        cmd_discover()
    elif cmd == 'dump':
        cmd_dump(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else 0)
    elif cmd == 'json':
        cmd_json(sys.argv[2] if len(sys.argv) > 2 else None)
    else:
        cmd_perclass()
