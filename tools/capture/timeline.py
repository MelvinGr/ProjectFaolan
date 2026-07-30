#!/usr/bin/env python3
"""Build a readable, chronological TIMELINE of a capture from its *.tshark.json dump.

The dump is a flat list of packets; this turns it into the story of the session - login handoff,
world entry, quest chain, combat, loot, chat, logout - with every id resolved to a name from our own
data (Opcodes.cs for the wire types, faolan.db for quests/items/abilities/NPCs).

    tools/capture/tshark_dump.sh <data-repo>/Captures/<name>.pcapng     # if not dumped yet
    python3 tools/capture/timeline.py <data-repo>/Captures/<name>.tshark.json

Options:
    --db PATH        faolan.db to resolve ids against (default: ./faolan.db; --db "" to skip)
    --section NAME   only this section (summary/handoff/timeline/detailed/quests/dialogue/chat/combat/coverage)
    --detailed       one flat chronological stream, one line per event, each with its raw body
    --all-packets    every packet, including the per-frame movement/heartbeat traffic (very long)

It deliberately resolves rather than dumps hex: an unresolved id in the output is a REAL gap in our
data, which is the point - the timeline doubles as a coverage check on the catalogs. `coverage`
prints that as a number.
"""
import argparse
import binascii
import collections
import json
import os
import re
import sqlite3
import struct
import sys
from datetime import datetime

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OPCODES = os.path.join(REPO, "Faolan.Core", "Enums", "Opcodes.cs")

# The service tiers, by the port the GAME side of the connection sits on. 7010 is the only fixed one
# (UniverseAgent); the rest are handed to the client in the preceding tier's payload, so these are the
# ports our own faolan.json/Realm rows use and what retail happened to use in the corpus.
TIERS = {7010: "universe", 7011: "playeragent", 7012: "cs", 7001: "chat", 7017: "game"}

# Events worth a timeline line. Everything else (CharDCMove, Ping, aggro polling, ...) is per-frame
# noise that would bury the story - use --all-packets when you actually want it.
MARKERS = {
    "InitiateAuthentication", "AnswerChallenge", "Authenticate", "Authenticate2", "LoginCharacter",
    "GetStartupData", "FinishGetStartupData", "CharInPlayIIR", "PlayfieldFIIR",
    "FullPlayerCharacterIIR", "GameTimeIIR", "CinematicTransition", "CharacterActionIIR",
    "QuestIIR", "QuestGoalIIR", "QuestFullUpdateIIR", "QuestJournalIIR", "QuestLogListIIR",
    "KnubotOpenChatWindowIIR", "KnubotCloseChatWindowIIR", "KnubotStartTradeIIR",
    "KnubotFinishTradeIIR", "ScryScriptIIR",
    "DispatchOpenLootBag", "DispatchCloseLootBag", "TryLootAll", "TryLootCash", "DispatchAddItem",
    "DispatchRemoveItem", "DispatchUpdateCash", "DispatchDisplayItemLootChatMessage",
    "LootBagFullUpdateIIR", "TombStoneFullUpdateIIR",
    "ChainKillIIR", "DamageResultIIR", "PerformSpecialAction", "DispatchAddSpecialAbility",
    "CombatShieldIIR", "PetIIR", "N3ToClientFadeIIR", "SetPosIIR",
    "RequestBindLocation", "DispatchUpdateBindLocation", "DispatchValidResurrectionSpots",
    "CoreClaimIIR", "FormatFeedbackIIR", "SystemMessage", "ChannelChat", "CharacterChat",
    "ChatVicinity", "DispatchShortcutBarReply", "GameActionListIIR", "LookAtIIR",
}


def load_names():
    """(value -> enum member name) for every id in Opcodes.cs.

    NB the regex must NOT require a trailing comma: the LAST member of an enum has none, and requiring
    one silently leaves that id unresolved. N3ToClientQuitIIR is exactly that member, and an
    'unnamed 0x04b82b2d, 283 packets!' finding is what a comma-anchored regex produces.
    """
    names = {}
    if not os.path.exists(OPCODES):
        return names
    for m in re.finditer(r"^\s*(\w+)\s*=\s*(0x[0-9A-Fa-f]+)", open(OPCODES).read(), re.M):
        names.setdefault(int(m.group(2), 16), m.group(1))
    return names


class Db:
    """Optional id -> name resolution out of faolan.db. Every lookup degrades to None."""

    def __init__(self, path):
        self.con = None
        if path and os.path.exists(path):
            try:
                self.con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
            except sqlite3.Error:
                self.con = None

    def one(self, sql, args):
        if not self.con:
            return None
        try:
            row = self.con.execute(sql, args).fetchone()
        except sqlite3.Error:
            return None
        return row[0] if row else None

    def quest(self, qid):
        return self.one("SELECT QuestId FROM QuestDefinitions WHERE QuestId=?", (qid,))

    def goal(self, gid):
        return self.one("SELECT GoalId FROM QuestGoalDefinitions WHERE GoalId=?", (gid,))

    def item(self, tid):
        return self.one("SELECT Name FROM Items WHERE Id=?", (tid,))

    def ability(self, aid):
        # ⚠️ Abilities, NOT Spells - DispatchAddSpecialAbility/PerformSpecialAction carry Abilities.Id.
        # Looking these up in Spells returns nothing and reads like a missing catalog row.
        return self.one("SELECT Name FROM Abilities WHERE Id=?", (aid,))

    def ability_by_slot(self, slot):
        return self.one("SELECT Name FROM Abilities WHERE Slot=?", (slot,))

    def playfield(self, pid):
        return self.one("SELECT Name FROM Maps WHERE Id=?", (pid,))

    def rows(self, sql, args):
        if not self.con:
            return []
        try:
            return self.con.execute(sql, args).fetchall()
        except sqlite3.Error:
            return []

    def npc_spawns(self, map_id):
        return self.rows(
            "SELECT s.PositionX, s.PositionY, s.PositionZ, t.Name FROM NpcSpawns s "
            "LEFT JOIN NpcTemplates t ON t.TemplateId = s.NpcTemplateId WHERE s.MapId = ?", (map_id,))

    def world_objects(self, map_id):
        return self.rows(
            "SELECT PositionX, PositionY, PositionZ, Name FROM WorldObjects WHERE MapId = ?", (map_id,))


def be(raw, off, n=4):
    return int.from_bytes(raw[off:off + n], "big") if len(raw) >= off + n else 0


def spawn_position(raw):
    """The (x, y, z) a *FullUpdate spawn body opens with: protobuf field 1, three little-endian floats."""
    for f, wt, v in protobuf(raw[17:]):
        if f == 1 and wt == 2 and isinstance(v, list):
            xs = [struct.unpack("<f", sv.to_bytes(4, "little"))[0] for sf, swt, sv in v if swt == 5]
            if len(xs) >= 3:
                return tuple(xs[:3])
    return None


def resolve_entities(P, db):
    """handle -> label, so combat/despawn lines read as names instead of raw handles.

    Entity handles are SESSION-VOLATILE (no_session_volatile_columns), so they cannot be looked up -
    they have to be resolved through what the capture itself says. A spawn body carries a POSITION, and
    position is the one thing that IS stable between a capture and our NpcSpawns/WorldObjects rows, so
    the match is nearest-spawn-on-this-playfield.

    ⚠️ Two classes must be excluded rather than matched, or they silently take the name of whatever
    static spawn they happened to be standing near: the PLAYER, and summoned PETS (which move with the
    player and matched 'Brutal Pirate' from 22 units away). Distance is printed whenever the match is
    not near-exact, because an AMBUSH spawn appears where it was triggered rather than at its home
    point - the quarry Bat Demon spawns 10 units off its NpcSpawns row, and that gap is a finding, not
    noise to hide.
    """
    # A character id is orders of magnitude larger than an entity handle (player_handle_equals_character_id):
    # 6 of 67 spawn handles in the necromancer capture sit >= 0x1000000 and all six are real players. They
    # must never be position-matched - one stood inside 2u of a vendor and came out labelled "Trader".
    CHARACTER_ID_FLOOR = 0x1000000
    label, pets, player = {}, set(), None
    map_id = 0
    for p in P:
        if p["_kind"] == "PlayfieldFIIR":
            map_id = be(p["_raw"], 34)
        elif p["_kind"] == "FullPlayerCharacterIIR":
            player = be(p["_raw"], 12)
        elif p["_kind"] == "PetIIR":
            pets.add(be(p["_raw"], 25))
    if player:
        label[player] = "the player"
    for h in pets:
        label[h] = "pet"

    npcs, objs = db.npc_spawns(map_id), db.world_objects(map_id)
    for p in P:
        if p["_kind"] not in ("SimpleCharFullUpdateIIR", "SimpleDynelFullUpdateIIR"):
            continue
        h = be(p["_raw"], 12)
        if h in label:
            continue
        if h >= CHARACTER_ID_FLOOR:
            label[h] = "another player"
            continue
        pos = spawn_position(p["_raw"])
        table = npcs if p["_kind"] == "SimpleCharFullUpdateIIR" else objs
        if not pos or not table:
            continue
        x, y, z = pos
        best = min(table, key=lambda r: (r[0] - x) ** 2 + (r[1] - y) ** 2 + (r[2] - z) ** 2)
        d = ((best[0] - x) ** 2 + (best[1] - y) ** 2 + (best[2] - z) ** 2) ** 0.5
        if best[3] and d <= 25:
            label[h] = best[3] if d < 2 else f"{best[3]} (+{d:.0f}u)"
    return label


def others_in(raw, label):
    """Every KNOWN entity handle appearing anywhere in a body, at any 4-byte aligned-ish offset.

    Relational packets (hate list, threat, look-at) each place their second entity at a different offset,
    so a fixed offset produces confident nonsense - `0x500003b2` is what reading HateList at LookAt's
    offset yields. Matching against handles the capture has already introduced is layout-independent.
    """
    found = []
    for i in range(0, max(0, len(raw) - 3)):
        v = int.from_bytes(raw[i:i + 4], "big")
        if v in label and v not in found:
            found.append(v)
    return found


def ent(label, h):
    return f"{label.get(h, '?')} 0x{h:x}" if h in label else f"0x{h:x}"


def ascii_of(raw):
    return "".join(chr(c) if 32 <= c < 127 else "." for c in raw)


def load(path, names):
    packets = json.load(open(path))["Packets"]
    if not packets:
        sys.exit("timeline: the dump has no packets")
    t0 = datetime.fromisoformat(packets[0]["TimeUtc"].replace("Z", "+00:00"))
    for p in packets:
        # A dump can carry a packet the dissector could not attribute a direction/frame to (one chat
        # packet in Strie_rogue-assassin_login), so normalise here rather than letting every consumer
        # trip over a None.
        p["Direction"] = p.get("Direction") or "?"
        p["Frame"] = p.get("Frame") or 0
        p["_t"] = (datetime.fromisoformat(p["TimeUtc"].replace("Z", "+00:00")) - t0).total_seconds()
        port = p["Destination"]["Port"] if p["Direction"] == "c2s" else p["Source"]["Port"]
        p["_tier"] = TIERS.get(port, str(port))
        p["_iir"] = names.get(int(p["Iir"], 16), p["Iir"]) if p["Iir"] else None
        p["_kind"] = p["_iir"] or p["Method"] or "?"
        p["_raw"] = binascii.unhexlify(p["BodyHex"]) if p["BodyHex"] else b""
    return packets, t0


def describe(p, db, label=None):
    """The human-readable tail of a timeline line: what this packet actually says."""
    k, raw = p["_kind"], p["_raw"]
    label = label or {}

    if k == "InitiateAuthentication" and len(raw) > 2:
        return f'account "{raw[2:2 + be(raw, 0, 2)].decode("latin-1", "replace")}"'
    if k == "PlayfieldFIIR":
        # [17..29] is the spawn position (3 BE floats); the playfield id follows it.
        pf = be(raw, 34)
        return f"playfield {pf}" + (f' "{db.playfield(pf)}"' if db.playfield(pf) else "")
    if k == "FullPlayerCharacterIIR":
        return f"login snapshot, {len(raw)} B"
    if k in ("DispatchUpdateBindLocation",):
        return f"bind playfield {be(raw, 0)}"
    if k == "DispatchAddSpecialAbility":
        slot, aid = be(raw, 0), be(raw, 4)
        return f"ability {aid} slot {slot}" + (f" = {db.ability(aid)}" if db.ability(aid) else "")
    if k == "PerformSpecialAction":
        aid = be(raw, 0)
        # The c2s body is the ability SLOT, not its id (the client sends what the action bar holds).
        return f"use ability slot {aid}" + (f" = {db.ability_by_slot(aid)}" if db.ability_by_slot(aid) else "")
    if k == "QuestIIR":
        # [16..20]=0, [20]=0x01 marker, then state, then questId. 2 = active/updated, 4 = completed.
        state = be(raw, 21)
        return f"quest {be(raw, 25)} state {state}" + {2: " (active)", 4: " (completed)"}.get(state, "")
    if k == "QuestGoalIIR":
        # [.. 17]=questId [21]=goalId then the update triple - one KIND, not a count (#525).
        kind = {(1, 0, 0): "Satisfied", (2, 0, 1): "Progress", (3, 2, 0): "Advanced"}.get(
            (be(raw, 25), be(raw, 29), be(raw, 33)), f"?{be(raw, 25)},{be(raw, 29)},{be(raw, 33)}")
        return f"quest {be(raw, 17)} goal {be(raw, 21)} -> {kind}"
    if k in ("QuestFullUpdateIIR", "QuestJournalIIR"):
        # 50301 records are quest TITLES, 50304 are per-GOAL objective lines - one per goal, which is
        # what a full-update carries for a multi-goal quest (#525 / the QuestFullUpdate goal harvest).
        refs = re.findall(r'id="(\d+)" category="(\d+)"', ascii_of(raw))
        quests = sorted({int(i) for i, c in refs if c == "50301"})
        goals = sorted({int(i) for i, c in refs if c == "50304"})
        parts = []
        if quests:
            parts.append("quests " + ", ".join(map(str, quests)))
        if goals:
            parts.append("goals " + ", ".join(map(str, goals)))
        return "; ".join(parts)
    if k == "DispatchAddItem":
        slot, tid = be(raw, 8), item_template(raw)
        name = db.item(tid) if tid else None
        return f"slot {slot}, template {tid}" + (f' = "{name}"' if name else "")
    if k == "DispatchUpdateCash":
        return f"cash {be(raw, 4)} (+{be(raw, 8)})"
    if k == "DamageResultIIR":
        # Wire damage is DISPLAY-scaled (x100): 945 on the wire renders as 9.45.
        return (f"{ent(label, be(raw, 12))} <- {ent(label, be(raw, 21))}, "
                f"{be(raw, 25)} ({be(raw, 25) / 100:.2f} shown), school {be(raw, 29)}")
    if k == "ChainKillIIR":
        return f"chain {be(raw, 17)}"
    if k == "PetIIR":
        # [20] is the verb (0 summon / 1 dismiss); [21..25] is the pet's CLASS and [25..29] its handle.
        return f"pet 0x{be(raw, 25):x} " + ("dismissed" if raw[20:21] == b"\x01" else "summoned")
    if k in ("N3ToClientFadeIIR",):
        return f"despawn {ent(label, be(raw, 12))}"
    if k == "N3ToClientQuitIIR":
        return f"quit {ent(label, be(raw, 12))}"
    if k in ("SimpleCharFullUpdateIIR", "SimpleDynelFullUpdateIIR", "DoorFullUpdateIIR",
             "LootBagFullUpdateIIR", "TombStoneFullUpdateIIR", "ClimbingDynelFullUpdateIIR"):
        pos = spawn_position(raw)
        at = f" at ({pos[0]:.1f}, {pos[1]:.1f}, {pos[2]:.1f})" if pos else ""
        return f"spawn {ent(label, be(raw, 12))}{at}"
    if k == "SimpleDynelVisualChangeIIR":
        # The state byte drives a harvest/use animation on a world object. ⚠️ These keep firing for
        # OTHER players in a retail capture - check the player's own position before crediting one.
        return f"{ent(label, be(raw, 12))} visual state 0x{raw[20]:02x}" if len(raw) > 20 else ""
    if k == "LookAtIIR":
        # TARGET SELECTION - the client tells the server what the player has selected. Target is a fixed
        # [21..25], and ZERO means the player cleared their target rather than picked a new one.
        target = be(raw, 21)
        return f"{ent(label, be(raw, 12))} " + (f"selects {ent(label, target)}" if target else "deselects")
    if k in ("HateListUpdateIIR", "ThreatLevelIIR"):
        # These do NOT share LookAt's or DamageResult's layout, so scan the body for a handle we already
        # know rather than trusting an offset - reading HateList at LookAt's offset yields "0x500003b2".
        subject = be(raw, 12)
        others = [h for h in others_in(raw, label) if h != subject]
        return f"{ent(label, subject)} -> {ent(label, others[0])}" if others else ent(label, subject)
    if k in ("DispatchOpenLootBag", "DispatchCloseLootBag", "TryLootAll", "TryLootCash"):
        return f"bag {ent(label, be(raw, 4))}"
    if k in ("ChannelChat", "CharacterChat", "SystemMessage", "ChatVicinity", "FormatFeedbackIIR",
             "KnubotCloseChatWindowIIR", "DispatchDisplayItemLootChatMessage"):
        return chat_text(raw)
    return ""


def _varint(b, i):
    r = s = 0
    while i < len(b):
        c = b[i]
        i += 1
        r |= (c & 0x7F) << s
        s += 7
        if not c & 0x80:
            return r, i
    raise ValueError("truncated varint")


def protobuf(b, depth=0):
    """Minimal protobuf field walk -> [(field, wire, value)]; length-delimited values recurse."""
    out, i = [], 0
    while i < len(b):
        try:
            key, i = _varint(b, i)
        except ValueError:
            break
        f, wt = key >> 3, key & 7
        if wt == 0:
            try:
                v, i = _varint(b, i)
            except ValueError:
                break
            out.append((f, 0, v))
        elif wt == 2:
            try:
                n, i = _varint(b, i)
            except ValueError:
                break
            if i + n > len(b):
                break
            sub = b[i:i + n]
            i += n
            out.append((f, 2, protobuf(sub, depth + 1) if depth < 3 else sub))
        elif wt == 5:
            out.append((f, 5, int.from_bytes(b[i:i + 4], "little")))
            i += 4
        elif wt == 1:
            out.append((f, 1, int.from_bytes(b[i:i + 8], "little")))
            i += 8
        else:
            break
    return out


def item_template(raw):
    """The item TEMPLATE id out of a DispatchAddItem body.

    Body = [u32 ..][u32 charId][u32 slot][u32 pbLen][protobuf]. The stat list (field 2) carries only
    instance-ish stats (0/8/23/26/46) - NOT the template - and stat 23 is a Static Instance id, which
    is the tempting wrong answer. The template lives in field 3's sub-message as field 4 (fixed32).
    It has to be walked STRUCTURALLY: fields 1-3 of that same sub-message are the LDB name-key quads,
    which are themselves fixed32 field-4s, so scanning for the tag byte finds a key word instead.
    """
    for f, wt, v in protobuf(raw[16:]):
        if f == 3 and wt == 2 and isinstance(v, list):
            for sf, swt, sv in v:
                if sf == 4 and swt == 5:
                    return sv
    return 0


def chat_text(raw):
    """Readable text out of a chat/feedback body: the plain string, or the remoteformat it references."""
    txt = "".join(chr(c) if 32 <= c < 127 else "\x00" for c in raw)
    # Attribute ORDER varies and the values are not always quoted (`category=20000 token="..."`), so
    # match each attribute independently inside the tag rather than assuming a layout.
    tag = re.search(r"<remoteformat([^>]*)>", txt)
    if tag:
        attrs = tag.group(1)
        cat = re.search(r'category="?(\d+)"?', attrs)
        tok = re.search(r'token="([^"]+)"', attrs)
        rid = re.search(r'id="(\d+)"', attrs)
        if tok:
            return f'token "{tok.group(1)}"' + (f" (cat {cat.group(1)})" if cat else "")
        if rid and cat:
            return f"LDB[{cat.group(1)}][{rid.group(1)}] (encrypted - see --report ldb-dialogue)"
    # Take the longest printable RUN rather than everything printable: a chat body opens with binary
    # routing words whose bytes are often incidentally printable, and they read as text noise glued to
    # the front of the message ('#( 0 any raids or dungeons?').
    # A wire string is [u16 BE length][latin-1 bytes] (wire_strings_are_latin1_u16). Find that framing
    # instead of scraping printable bytes: the length's low byte is itself often printable and arrives
    # glued to the front of the message ("TKUTH 15+..." for a 0x54-long "KUTH 15+...").
    best = ""
    for i in range(len(raw) - 2):
        n = int.from_bytes(raw[i:i + 2], "big")
        if not 2 <= n <= len(raw) - i - 2:
            continue
        cand = raw[i + 2:i + 2 + n]
        if all(32 <= c < 127 or c in (9, 10, 13) for c in cand) and len(cand) > len(best):
            best = cand
    plain = re.sub(r"<[^>]+>", "", best.decode("latin-1")).strip() if best else ""
    return f'"{plain}"' if len(plain) > 1 else ""


def section_summary(P, t0, db, names):
    print(f"=== session ===\nstart {t0.isoformat()}   duration {P[-1]['_t']:.1f}s   {len(P)} packets")
    tiers = collections.Counter(p["_tier"] for p in P)
    print("tiers: " + ", ".join(f"{k} {v}" for k, v in tiers.most_common()))
    top = collections.Counter(f"{p['_tier']} {p['Direction']} {p['_kind']}" for p in P)
    print("\ntop traffic:")
    for k, v in top.most_common(12):
        print(f"  {v:6d}  {k}")


def section_handoff(P, db, label=None):
    print("\n=== login handoff ===")
    for p in P:
        if p["_tier"] in ("universe", "playeragent", "cs") or p["_kind"] in ("Authenticate2", "PlayfieldFIIR", "CharInPlayIIR"):
            d = describe(p, db, label)
            print(f"  {p['_t']:7.1f} {p['_tier']:11s} {p['Direction']} {p['_kind']:28s} {d}")


def section_timeline(P, db, everything, label=None):
    print("\n=== timeline ===")
    for p in P:
        if not everything and p["_kind"] not in MARKERS:
            continue
        d = describe(p, db, label)
        print(f"  {p['_t']:7.1f} {p['_tier']:6s} {p['Direction']} {p['_kind']:30s} {d}")


# Per-FRAME traffic: movement, heartbeats, aggro polling and the stat/energy stream. These are the bulk
# of a capture (5 of every 6 packets) and carry no event, so --detailed suppresses them and SAYS SO
# rather than quietly shortening its own output. --all-packets keeps them.
DETAIL_NOISE = {
    "CharDCMoveIIR", "MoveTaskIIR", "RequestAggroStatus", "DispatchUpdateAggroStatus",
    "SetSprintInputState", "DispatchSetIsSprinting", "DispatchSyncCanSprint", "Ping", "DispatchPong",
    "UpdateGameServerStats", "DispatchGameServerStats", "StatIIR", "EnergyIIR", "RegenIIR",
    "DispatchSyncRegenDelayed", "UpdateQuestMarkerNPC", "DispatchSetQuestMarker",
}


def section_detailed(P, db, everything, label=None):
    """One line per EVENT, in one flat chronological stream, with everything decodable on the line.

    The sectioned view splits a session across eight headings, which is the right shape for answering a
    question but the wrong one for reading the session as it happened - an event and its consequence
    land under different headings. This is the same data as one stream, ordered, with the resolved
    description AND the raw body tail, so nothing is hidden behind the decoder's own coverage: a packet
    this script cannot describe still shows its bytes rather than an empty column.
    """
    print("\n=== detailed ===")
    print(f"  {'time':>7}  {'frame':>6}  {'tier':11} {'d':1} {'event':32} details")
    suppressed = collections.Counter()
    for p in P:
        if not everything and p["_kind"] in DETAIL_NOISE:
            suppressed[p["_kind"]] += 1
            continue
        d = describe(p, db, label)
        h = p["BodyHex"]
        extra = f"   [{h[:72]}{'…' if len(h) > 72 else ''}]" if h else ""
        print(f"  {p['_t']:7.1f}  {p['Frame']:6d}  {p['_tier']:11} {p['Direction'][0]} {p['_kind']:32} {d}{extra}")
    if suppressed:
        total = sum(suppressed.values())
        top = ", ".join(f"{k} x{v}" for k, v in suppressed.most_common(6))
        print(f"\n  ({total} per-frame packets suppressed: {top}{', …' if len(suppressed) > 6 else ''}"
              f" - pass --all-packets to include them)")


def section_quests(P, db, label=None):
    print("\n=== quests ===")
    for p in P:
        if p["_kind"] not in ("QuestIIR", "QuestGoalIIR", "QuestFullUpdateIIR"):
            continue
        print(f"  {p['_t']:7.1f} {p['_kind']:22s} {describe(p, db, label)}")
    print("\n  Names: dotnet run --project Faolan.Importer -- --report ldb-quest-text \\\n"
          "           --report-arg <quest ids> --RdbDir <client>/RDB")


def section_dialogue(P, db):
    """The conversation trail as (category, screen id) pairs - the input to --report ldb-dialogue."""
    print("\n=== dialogue ===")
    cur, total, seen_any = None, [], False
    for p in P:
        k = p["_kind"]
        if k == "KnubotOpenChatWindowIIR":
            cur = []
            total.append((p["_t"], cur))
            seen_any = True
        if k in ("KnubotAppendTextIIR", "KnubotAnswerListIIR") and cur is not None:
            for i, c in re.findall(r'id="(\d+)" category="(\d+)"', ascii_of(p["_raw"])):
                cur.append((int(c), int(i)))
        if k == "KnubotCloseChatWindowIIR":
            cur = None
    for t, lines in total:
        print(f"\n  -- conversation @ {t:.1f}")
        for cat, sid in dict.fromkeys(lines):
            who = "NPC" if cat == 50002 else "PLR" if cat == 50001 else f"c{cat}"
            print(f"     {who} [{sid}]")
    if seen_any:
        print("\n  Text: dotnet run --project Faolan.Importer -- --report ldb-dialogue \\\n"
              "          --report-arg <NpcName> --RdbDir <client>/RDB   (match its 'id N' to the ids above)")
    else:
        print("  (no conversations in this capture)")


def section_chat(P, db, label=None):
    print("\n=== chat ===")
    for p in P:
        if p["_kind"] in ("ChannelChat", "CharacterChat", "SystemMessage", "ChatVicinity"):
            print(f"  {p['_t']:7.1f} {p['_kind']:16s} {describe(p, db, label)}")


def section_combat(P, db, label=None):
    print("\n=== combat ===")
    for p in P:
        if p["_kind"] in ("DamageResultIIR", "ChainKillIIR", "PerformSpecialAction", "PetIIR", "CombatShieldIIR"):
            print(f"  {p['_t']:7.1f} {p['_kind']:22s} {describe(p, db, label)}")


def section_coverage(P, db, names):
    """What this capture contains that our own data cannot name. Unresolved here = a real gap."""
    print("\n=== coverage ===")
    unresolved_iir = sorted({p["_iir"] for p in P if p["_iir"] and p["_iir"].startswith("0x")})
    unnamed = sorted({p["Method"] for p in P if not p["Iir"] and p["Method"] and p["Method"].startswith("Ox")})
    print(f"  IIR types unresolved by Opcodes.cs : {unresolved_iir or 'none'}")
    print(f"  methods still Ox-placeholder       : {unnamed or 'none'}")

    items = {item_template(p["_raw"]) for p in P if p["_kind"] == "DispatchAddItem"}
    items.discard(0)
    missing = [i for i in sorted(items) if not db.item(i)]
    print(f"  item templates granted             : {len(items)}, unnamed in Items: {missing or 'none'}")

    abil = {be(p["_raw"], 4) for p in P if p["_kind"] == "DispatchAddSpecialAbility"}
    missing_a = [a for a in sorted(abil) if not db.ability(a)]
    print(f"  abilities granted                  : {len(abil)}, missing from Abilities: {missing_a or 'none'}")

    quests = {be(p["_raw"], 17) for p in P if p["_kind"] == "QuestGoalIIR"}
    goals = {be(p["_raw"], 21) for p in P if p["_kind"] == "QuestGoalIIR"}
    print(f"  quests touched                     : {sorted(quests) or 'none'}"
          f"  missing: {[q for q in sorted(quests) if not db.quest(q)] or 'none'}")
    print(f"  goals touched                      : {sorted(goals) or 'none'}"
          f"  missing: {[g for g in sorted(goals) if not db.goal(g)] or 'none'}")


SECTIONS = {
    "summary": section_summary, "handoff": section_handoff, "timeline": section_timeline,
    "detailed": section_detailed,
    "quests": section_quests, "dialogue": section_dialogue, "chat": section_chat,
    "combat": section_combat, "coverage": section_coverage,
}


def main():
    ap = argparse.ArgumentParser(description="Build a timeline of a *.tshark.json capture dump.")
    ap.add_argument("dump")
    ap.add_argument("--db", default=os.path.join(REPO, "faolan.db"))
    ap.add_argument("--section", choices=sorted(SECTIONS), action="append")
    ap.add_argument("--all-packets", action="store_true")
    ap.add_argument("--detailed", action="store_true",
                    help="one flat chronological stream, one line per event, with the raw body on each line")
    a = ap.parse_args()

    if a.dump.endswith(".pcapng"):
        sys.exit("timeline: pass the *.tshark.json, not the pcapng - dump it first with "
                 "tools/capture/tshark_dump.sh")

    names = load_names()
    db = Db(a.db)
    if a.db and not db.con:
        print(f"timeline: no readable DB at {a.db} - ids will not resolve to names\n", file=sys.stderr)
    P, t0 = load(a.dump, names)
    label = resolve_entities(P, db)

    default = ["summary", "detailed", "quests", "dialogue", "coverage"] if a.detailed \
        else ["summary", "handoff", "timeline", "quests", "dialogue", "chat", "combat", "coverage"]
    for name in (a.section or default):
        fn = SECTIONS[name]
        if fn is section_summary:
            fn(P, t0, db, names)
        elif fn in (section_timeline, section_detailed):
            fn(P, db, a.all_packets, label)
        elif fn is section_coverage:
            fn(P, db, names)
        elif fn is section_dialogue:
            fn(P, db)
        else:
            fn(P, db, label)


if __name__ == "__main__":
    main()
