#!/usr/bin/env python3
"""Decode the guild RPC traffic on the CsPlayerAgent connection (port 7012) from a capture JSON.

Method dispatch (decoded 2026-06-12): the 16-bit opcode after the RpcHeader is a METHOD INDEX
(0x2000 + position) into the recovered RPC method tables (docs/aoc-client-rpc-methods.json):
  client->server = CSPlayerAgent.client_to_server   (alignment verified: 0x201C = AcceptGuildInvitation
                                                     fired exactly once in the invite-accept capture)
  server->client = CSPlayerInterface.server_to_client, but the live build has UNNAMED EXTRA METHODS
                   so indices drift upward vs the recovered table (observed 0x20C5/0x20D0 exceed it).
                   Downloads below are therefore identified by CONTENT, not by the name table.

Observed guild flow (Hoxxling_mage-heraldofxotli_login-guild_invite_accepted-...):
  DL 0x202A GuildInvite        [guild ID32][guildName s16][invitee ID32][inviterName s16]
  UL 0x201C AcceptGuildInvitation [guild ID32]
  DL 0x203F GuildFullUpdate    guild header + MOTD + renown/feat block + self member record + roster
  DL 0x202F GuildMemberJoined  [invitee ID32][member record]   (also fires when OTHERS join live)
  DL 0x2030 GuildMemberUpdate  [member record]                 (level-up / zone change / login-logout)
  DL 0x2042 GuildInfoSameGuild [charId][guild ID32][03][02][charId][01]      (reply to 0x201E for guildmate)
  DL 0x2043 GuildInfoOtherGuild[charId][guild ID32][03][guildInst][02][05][00][name s16][u32 x4]
  UL 0x201E ClientRequestGuildInfo [char ID32]
  UL 0x202F GuildRequestMemberList (no body) -> retail answers 0x203F + 0x20A5 summary
  GameServer sub 0x2550C99A GuildUpdateIIR_c: [char ID32][u16 0][guildName s16] (in-world guild tag, sent on join)
At login while in a guild (claim_login_reward dump): server pushes 0x203F right after the Authenticate ack.

ID32 = [u32 type][u32 instance]; guild type = 0x0000DEB7, character type = 0x0000C350.

Member record (validated across the full 165-member roster + 3 live joins):
  [u8 2][u8 3]                head (constant 03 03)
  [u32 charId]
  [s16 name]                  u16 BE length + latin1 bytes
  [u8 rank]                   0 = fresh member; inviter Kyloth had 5
  [u8 online]
  [u32 playfield]             0 when offline
  [u8 level]                  Hoxxling's ticked 4 -> 5 mid-capture (level-up)
  [u32 class]                 matches the Class enum (0x2B HoX, 0x29 Necro, 0x12 Barb, ...)
  [u32 unknown0]
  [u32 lastSeenUnix]          0 while online
  [48 bytes]                  sparse renown/pvp counters; zero for fresh members
  => 75 bytes + name

GuildFullUpdate 0x203F body:
  [u8 governmentForm=3][u32 guildId][u8 1][u64 cash][u8 3][u32 0][u8 0]
  [s16 name][s16 description][s16 motd]
  [u8 1][u32 renown][u16 feats? actually see below]... 277-byte renown/feat/stats block (constant-ish
      across snapshots: 13 ascending u32 feat ids 0x4FFB0..0x50334 + counters; template-able, zeroes OK
      for a fresh guild)
  [self member record][u32 selfContribution?][member records ... to end of packet, no count]
"""
import json, struct, sys
from collections import Counter

GUILD_TYPE = 0x0000DEB7
CHAR_TYPE = 0x0000C350

# CsPlayerAgent (guilds/teams) RPC endpoint class ids — the new .tshark.json dumps tag every Conan packet
# Protocol="Conan", so the guild conversation is selected by its class pair, not the old ProtocolName field.
CS_CLASSES = {'0xbbd560c8', '0x6dd55614'}

C2S = {0x201C: 'AcceptGuildInvitation', 0x201D: 'DeclineGuildInvitation',
       0x201E: 'ClientRequestGuildInfo', 0x201F: 'ClientRequestGuildName',
       0x2021: 'CreateGuild', 0x2022: 'GuildInviteByName', 0x2023: 'GuildInvite',
       0x2028: 'GuildLeave', 0x202E: 'GuildMessageOfTheDay', 0x202F: 'GuildRequestMemberList',
       0x2040: 'RequestRenownHistory', 0x2041: 'RequestRenownRankings',
       0x2077: 'MassivePvpInfoRequest', 0x2000: 'Authenticate'}
S2C = {0x202A: 'GuildInvite', 0x202F: 'GuildMemberJoined', 0x2030: 'GuildMemberUpdate',
       0x203A: 'GuildCountersUpdate', 0x203F: 'GuildFullUpdate',
       0x2042: 'GuildInfoSameGuild', 0x2043: 'GuildInfoOtherGuild',
       0x2038: 'RenownRankings', 0x2039: 'RenownHistory'}


class R:
    def __init__(self, b, off=0):
        self.b = b
        self.off = off

    def u8(self):
        v = self.b[self.off]; self.off += 1; return v

    def u16(self):
        v = struct.unpack('>H', self.b[self.off:self.off + 2])[0]; self.off += 2; return v

    def u32(self):
        v = struct.unpack('>I', self.b[self.off:self.off + 4])[0]; self.off += 4; return v

    def u64(self):
        v = struct.unpack('>Q', self.b[self.off:self.off + 8])[0]; self.off += 8; return v

    def s16(self):
        n = self.u16(); v = self.b[self.off:self.off + n].decode('latin1'); self.off += n; return v

    def take(self, n):
        v = self.b[self.off:self.off + n]; self.off += n; return v

    def remaining(self):
        return len(self.b) - self.off


def parse_member(r):
    head = (r.u8(), r.u8())
    assert head == (3, 3), f'member head {head} at {r.off - 2:#x}'
    cid = r.u32()
    name = r.s16()
    rank = r.u8()
    online = r.u8()
    pf = r.u32()
    level = r.u8()
    cls = r.u32()
    unk0 = r.u32()
    last_seen = r.u32()
    tail = r.take(48)
    return dict(id=cid, name=name, rank=rank, online=online, playfield=pf,
                level=level, cls=cls, unk0=unk0, lastSeen=last_seen, tail=tail)


def parse_full_update(body):
    r = R(body)
    g = dict(governmentForm=r.u8(), guildId=r.u32(), f2=r.u8(), cash=r.u64(),
             f4=r.u8(), f5=r.u32(), f6=r.u8(),
             name=r.s16(), description=r.s16(), motd=r.s16())
    g['renownBlock'] = r.take(277)
    g['self'] = parse_member(r)
    g['selfContribution'] = r.u32()
    members = []
    while r.remaining() > 0:
        members.append(parse_member(r))
    g['members'] = members
    return g


def main(path):
    d = json.load(open(path))
    pkts = [p for p in d['Packets']
            if (p.get('SenderClass') or '').lower() in CS_CLASSES
            or (p.get('ReceiverClass') or '').lower() in CS_CLASSES]
    pkts.sort(key=lambda p: p['Frame'])
    counts = Counter()
    for p in pkts:
        dl = p['IsDownload']
        method = int(p['Opcode'], 16)
        body = bytes.fromhex(p.get('BodyHex') or '')
        name = (S2C if dl else C2S).get(method, '?')
        counts[(dl, method, name)] += 1
        line = f"#{p['Frame']:6d} {'DL' if dl else 'UL'} {method:#06x} {name:24s}"
        try:
            if dl and method == 0x202A:
                r = R(body)
                gt, gi = r.u32(), r.u32()
                gname = r.s16()
                ct, ci = r.u32(), r.u32()
                inviter = r.s16()
                line += f' guild {gi:#x} "{gname}" -> char {ci:#x}, inviter "{inviter}"'
            elif not dl and method == 0x201C:
                r = R(body)
                gt, gi = r.u32(), r.u32()
                line += f' guild {gi:#x}'
            elif dl and method in (0x202F, 0x2030):
                r = R(body)
                if method == 0x202F:
                    r.u32(); r.u32()  # invitee ID32
                m = parse_member(r)
                line += f' {m["name"]} rank={m["rank"]} online={m["online"]} pf={m["playfield"]:#x} lvl={m["level"]} class={m["cls"]:#x}'
            elif dl and method == 0x203F:
                g = parse_full_update(body)
                online = sum(1 for m in g['members'] if m['online']) + g['self']['online']
                line += (f' "{g["name"]}" gov={g["governmentForm"]} cash={g["cash"]} motd={len(g["motd"])}b'
                         f' members={1 + len(g["members"])} ({online} online) [parsed to end]')
        except Exception as e:
            line += f'  !! parse error: {e}'
        print(line)
    print()
    for (dl, method, name), n in sorted(counts.items()):
        print(f"{'DL' if dl else 'UL'} {method:#06x} {name:24s} x{n}")


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else
         'Captures/Hoxxling_mage-heraldofxotli_login_guild_invite_accepted-random-gameplay-other_playersvisible.tshark.json')
