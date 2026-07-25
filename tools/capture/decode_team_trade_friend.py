#!/usr/bin/env python3
"""Decode the team/group, player-trade and friend-list traffic (Gitea #101 / #99).

Source capture: login_2-clients_group-trade-friend - two retail clients on one host, so BOTH sides of every
exchange are on tape in one file (the dump's `Stream` field separates the ten connections). The players narrate
each step in chat, which is what labels the phases: "leave goup now", "kick", "trade", ";both friends added,
friends are one sided, no accept needed", "%set lootoptions, pass on loot to rare".

Validation rule, as everywhere in tools/capture: every packet must consume to EXACTLY its end. A short or long
read is a decode bug and is reported, never tolerated - a tolerant reader hides wire bugs.

Usage:  decode_team_trade_friend.py <capture.tshark.json>
"""

import json
import struct
import sys
from collections import Counter

# ProtoID32 class words seen here. The pair is [u32 class][u32 instance].
CLASS_CHARACTER = 0x0000C350
CLASS_TEAM = 0x0000DEA9

# A count-framed list is prefixed by (count + 1) * 0x3F1 - CLAUDE.md's ListFrame rule. NOT a constant: the
# member list's "0x00000BD3" IS 3 * 0x3F1, i.e. two members.
LIST_WORD = 0x3F1


class Reader:
    def __init__(self, data):
        self.d = data
        self.i = 0

    def u8(self):
        v = self.d[self.i]
        self.i += 1
        return v

    def u16(self):
        (v,) = struct.unpack_from(">H", self.d, self.i)
        self.i += 2
        return v

    def u32(self):
        (v,) = struct.unpack_from(">I", self.d, self.i)
        self.i += 4
        return v

    def u64(self):
        (v,) = struct.unpack_from(">Q", self.d, self.i)
        self.i += 8
        return v

    def string(self):
        n = self.u16()
        s = self.d[self.i:self.i + n].decode("latin1")
        self.i += n
        return s

    def id32(self):
        return (self.u32(), self.u32())

    def list_count(self):
        w = self.u32()
        if w % LIST_WORD:
            raise ValueError(f"0x{w:08X} is not a multiple of 0x3F1, so it is not a list frame")
        return w // LIST_WORD - 1

    def rest(self):
        return self.d[self.i:]

    def done(self):
        return self.i == len(self.d)


def read_member(r):
    """The TEAM MEMBER descriptor - the one repeating record almost every team message carries.

    102 bytes for a 10-character name: [ProtoID32 character][u16 len][name] then 82 fixed bytes. Level is
    confirmed independently by the chat server's friend reply (s2c 0x0028), whose trailing byte is the same
    character's level - 0x2C/44 and 0x29/41 for the two players here.
    """
    cls, cid = r.id32()
    if cls != CLASS_CHARACTER:
        raise ValueError(f"member class 0x{cls:08X} != character 0x{CLASS_CHARACTER:08X}")
    name = r.string()
    m = {"id": cid, "name": name, "level": r.u32()}
    m["field1"] = r.u32()      # 5 for both players; the friend reply carries the same 5
    m["mapId"] = r.u32()       # 4020 = City of Tortage, where the capture was taken
    m["field3"] = r.u32()      # 3 / 2 - differs per character (class or archetype)
    for k in range(4, 16):
        m[f"field{k}"] = r.u32()
    # A 9-byte per-character opaque. It appears NOWHERE else in the capture - not in login, not in the
    # snapshot - so it is CS-tier state we cannot derive; carried, not invented (see #101). Read as
    # [u32][u32][u8] because only that last BYTE varies between the two characters (0x54 / 0xEC): the three
    # bytes after it are zero in both, so a u32 there would be a field whose low 24 bits are always 0.
    m["opaqueA"] = r.u32()
    m["opaqueB"] = r.u32()
    m["opaqueC"] = r.u8()
    m["trailer"] = (r.d[r.i:r.i + 9].hex(),)  # nine zero bytes in every captured record
    r.i += 9
    return m


def decode(direction, opcode, method, body):
    """Returns (label, fields) or raises. Consumes the WHOLE body or raises."""
    r = Reader(body)
    op = int(opcode, 16)

    # ---- team, on the CsPlayerAgent tier -------------------------------------------------------------
    if method == "TeamInvite":                      # c2s 0x2003
        out = {"target": r.id32(), "tail": (r.u8(), r.u8(), r.u8())}
    elif method in ("RequestTeamInvite", "DispatchTeamInvite"):   # c2s 0x2091 / s2c 0x2001
        out = {"member": read_member(r), "tail": r.u16()}
    elif method == "DispatchReplyTeamInvite":       # s2c 0x209f
        out = {"member": read_member(r), "tail": (r.u16(), r.u8())}
    elif method in ("DispatchYouInvitedToTeam", "DispatchYouGotKickedFromTeam"):  # s2c 0x200a / 0x2022
        out = {"member": read_member(r)}
    elif method == "AcceptTeamInvite":              # c2s 0x2008 (CS tier)
        out = {"inviter": r.id32()}
    elif method == "DispatchYouJoinedTeam":         # s2c 0x200e - the whole roster
        team, leader = r.id32(), r.id32()
        count = r.list_count()
        out = {"team": team, "leader": leader, "members": [read_member(r) for _ in range(count)]}
        # The team's LOOT SETTINGS, and the same shape the client sends in TeamSetLootOption
        # ([u32][u8][u32]): [u32 0][u8 1][u32 0][u32 0][u32 0][u8 2]. Identical in all six captured rosters.
        out["loot"] = (r.u32(), r.u8(), r.u32(), r.u32(), r.u32(), r.u8())
    elif method in ("DispatchTeamJoin", "DispatchTeamLeave", "DispatchSetNewTeamLeader"):  # 0x2013/0x2012/0x2016
        out = {"team": r.id32(), "member": read_member(r)}
    elif method == "DispatchCharacterLookingForTeamStatus":       # s2c 0x2026
        out = {"a": r.u16(), "b": r.u8()}
    elif method == "TeamSetLootOption":             # c2s 0x2006
        out = {"a": r.u32(), "b": r.u8(), "option": r.u32()}
    elif method == "SetPassOnLootLevel":            # c2s 0x204d (game tier)
        out = {"level": r.u32()}                    # 0xFFFFFFFF = "no threshold"
    elif method == "LeaveTeam":                     # c2s 0x2008 (game tier) - no body
        out = {}
    elif method == "KickFromTeam":                  # c2s 0x2009 (game tier)
        out = {"target": r.id32()}
    elif method == "RequestNewJoinHasHostileInFriends":           # c2s 0x2092 - the invite safety check
        out = {"member": read_member(r)}

    # ---- player trade, on the game server ------------------------------------------------------------
    elif method in ("DispatchRequestTrade", "StartTrade", "DispatchStartTrade",
                    "DispatchPartnerAcceptedTrade"):              # 0x2068 s2c / 0x2065 / 0x2069 / 0x206c
        out = {"partner": r.id32()}
    elif method in ("SetTradeCash", "DispatchPartnerUpdatedCash"):  # c2s 0x2067 / s2c 0x206e
        out = {"cash": r.u64()}
    elif method == "AcceptTrade":                   # c2s 0x206b - no body
        out = {}
    elif method == "AddTradeItem":                  # c2s 0x2068
        out = {"container": r.u32(), "owner": r.u32(), "slot": r.u32(), "count": r.u32()}
    elif method in ("DispatchTradeAddedItem", "DispatchTradePartnerAddedItem"):  # s2c 0x2070 / 0x206f
        n = r.u32()
        out = {"itemSnapshotLen": n, "itemSnapshot": r.d[r.i:r.i + n].hex()}
        r.i += n
        out["count"] = r.u32()
    elif method in ("Reserved0x206B", "Reserved0x206D"):
        # Both empty. 0x206B acks YOUR accept; 0x206D lands on both sides as the trade COMMITS, and is
        # immediately followed by the TradeCompletedSuccessfully feedback overlay (IIR 0x8D5A8B6B).
        out = {}
    else:
        return None

    if not r.done():
        raise ValueError(f"{len(body) - r.i} byte(s) left over of {len(body)}")
    return out


def decode_chat(direction, opcode, body):
    """The friend list, which lives on the CHAT server - not the game server.

    Sweeping the whole capture for a game-tier friend opcode finds only RequestNewJoinHasHostileInFriends
    (a team-invite check), and the chat says it outright: friends are one-sided with no accept step.
    """
    r = Reader(body)
    if opcode in ("0x0028", "0x0029") and direction == "c2s":
        out = {"character": r.u32()}                # add / remove, by character id
    elif opcode == "0x0029":
        out = {"character": r.u32()}                # remove is echoed back verbatim
    elif opcode == "0x0028":                        # the add REPLY - the friend's current state
        out = {"character": r.u32(), "online": r.u8(), "field": r.u8(), "stamp": r.u32(), "level": r.u8()}
    elif opcode == "0x0025":                        # presence push, by name
        out = {"zero": r.u64(), "stamp": r.u32(), "a": r.u16(), "b": r.u8(), "name": r.string()}
    else:
        return None

    if not r.done():
        raise ValueError(f"{len(body) - r.i} byte(s) left over of {len(body)}")
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    packets = json.load(open(sys.argv[1]))
    packets = packets["Packets"] if isinstance(packets, dict) else packets

    ok, fail, skipped = Counter(), [], 0
    for p in packets:
        body = bytes.fromhex(p.get("BodyHex") or "")
        try:
            if p.get("Protocol") == "Conan":
                r = decode(p.get("Direction"), p["Opcode"], p.get("Method"), body)
                label = p.get("Method")
            else:
                r = decode_chat(p.get("Direction"), p["Opcode"], body)
                label = f"chat {p['Opcode']}"
            if r is None:
                skipped += 1
                continue
            ok[f"{p['Direction']} {label}"] += 1
        except Exception as e:
            fail.append(f"f{p['Frame']} {p['Direction']} {p['Opcode']} {p.get('Method')}: {e}")

    print(f"decoded {sum(ok.values())} packets, {len(fail)} failed, {skipped} not part of these subsystems\n")
    for k, v in sorted(ok.items()):
        print(f"  {v:4}x  {k}")
    if fail:
        print(f"\nFAILURES ({len(fail)}):")
        for f in fail[:20]:
            print(f"  {f}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
