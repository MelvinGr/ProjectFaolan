# Teams, player trade and the friend list

All three decoded from one capture: `login_2-clients_group-trade-friend`, two retail clients on the same host, so
**both sides of every exchange are on tape in one file** — the dump's `Stream` field separates the ten connections
(five per client). The players narrate each step in chat, which is what labels the phases: *"leave goup now"*,
*"kick"*, *"trade"*, *";both friends added, friends are one sided, no accept needed"*, *"%set lootoptions, pass on
loot to rare"*.

`tools/capture/decode_team_trade_friend.py` reads all **97** of the capture's team/trade/friend packets to their
exact end — no short or long reads — and `TeamTradeFriendCodecTests` gates every message we build against retail's
bytes. [V1: tools/capture/decode_team_trade_friend.py]

Each subsystem's three tiers matter, because they are not the same tier:

| subsystem | tier | why it matters |
| --- | --- | --- |
| team roster | CsPlayerAgent | invite/accept/join/leave/kick/loot options |
| group membership | GameServer (world) | two IIRs mirroring the roster onto the world connection |
| player trade | GameServer | the whole flow, one connection each side |
| friend list | **ChatServer** | not a game-server subsystem at all |

## The team member descriptor

One repeating record carries almost every team message, which is why the whole subsystem is a small amount of code
once it exists (`TeamMemberRecord`):

```
[u32 0xC350][u32 characterId][u16 len][name LATIN1]   then twenty u32 and a u16
```

102 bytes for a 10-character name. Four trailing fields carry meaning one capture can pin — **level**, the **map
id** (twice; the client greys out members in another playfield), the per-character **archetype** word, and a 9-byte
per-character **opaque**. The rest are identical across both characters and every message and are carried as
captured.

Level is confirmed *independently*: the chat server's friend-add reply ends with the same character's level, and
those bytes match the member records exactly — `0x2C`/44 for the one player, `0x29`/41 for the other.

The 9-byte opaque appears **nowhere else in the capture** — not in login, not in the snapshot — so it is CS-tier
state that cannot be derived from anything we send, and computing it would be invention. It is read as
`[u32][u32][u8]` because only that last byte differs between the two characters (`0x54` / `0xEC`) while the three
bytes after it are zero in both. [V0: the team member record's 9-byte per-character opaque]

## Team

```
c2s 0x2091 RequestTeamInvite            [member][u16 0]
c2s 0x2003 TeamInvite                   [ProtoID32 target][0][0][1]
s2c 0x2001 DispatchTeamInvite           [member][u16 0]
s2c 0x200A DispatchYouInvitedToTeam     [member]
s2c 0x209F DispatchReplyTeamInvite      [member][u16 0][u8 0]
c2s 0x2008 AcceptTeamInvite             [ProtoID32 inviter]        (CS tier)
s2c 0x200E DispatchYouJoinedTeam        [ProtoID32 team][ProtoID32 leader][ListFrame(n)][member × n][loot 18 B]
s2c 0x2013 DispatchTeamJoin             [ProtoID32 team][member]
s2c 0x2012 DispatchTeamLeave            [ProtoID32 team][member]
s2c 0x2016 DispatchSetNewTeamLeader     [ProtoID32 team][member]
s2c 0x2022 DispatchYouGotKickedFromTeam [member]
c2s 0x2006 TeamSetLootOption            [u32 0][u8 1][u32 option]
c2s 0x2008 LeaveTeam                    (empty)                    (GAME tier)
c2s 0x2009 KickFromTeam                 [ProtoID32 target]         (GAME tier)
c2s 0x204D SetPassOnLootLevel           [u32 level]                (GAME tier; 0xFFFFFFFF = no threshold)
```

`DispatchTeamJoin` / `TeamLeave` / `SetNewTeamLeader` share one layout — the **opcode carries the verb**.

The roster's member list is **count-framed**: retail's `0x00000BD3` is `3 × 0x3F1`, i.e. two members, not a
constant. This is the `ListFrame` rule, and this message is a fresh instance of exactly the trap that rule
documents. The leader's own record comes first in all six captured rosters, and the trailing 18 bytes are the loot
settings — the same shape the client sends in `TeamSetLootOption`.

Team ids are minted sequentially by the CS tier under class `0x0000DEA9`: the capture's three groupings are
`0x0051DD9A`, `0x0051DD9B`, `0x0051DD9C`.

⚠️ Two opcode numbers are reused across tiers: `0x2008` is `AcceptTeamInvite` on the CS tier and `LeaveTeam` on the
game tier; `0x2009` is `KickFromTeam` on the game tier. That is how 16-bit method indices work — per interface — so
they must not be unified.

### Group membership on the world connection

A join *also* pushes an IIR to every member's world connection, and a leave pushes its complement
(`GroupMembershipCodec`). Both were name-only entries in `Opcodes.cs` until this capture put them on the wire.

```
AddToGroupIIR      0x0DF44B73   [ProtoID32 member][u8 0][ProtoID32 team][ProtoID32 leader]   29-byte body
RemoveFromGroupIIR 0xE4FF0C85   [ProtoID32 member][u8 0][ProtoID32 team]                     21-byte body
```

One record per member — the joiner *and* the members already in the team — to every world connection in the team.

The chat server independently gains a `~Team` channel on both clients the moment the group forms, and drops it on
leave, which makes a cheap end-to-end check.

## Player trade

All on the game server, `PlayerTradeCodec`:

```
s2c 0x2068 DispatchRequestTrade          [ProtoID32 requester]     "X wants to trade"
c2s 0x2065 StartTrade                    [ProtoID32 partner]       the target opens the window
s2c 0x2069 DispatchStartTrade            [ProtoID32 partner]       to BOTH - each carrying the OTHER party
c2s 0x2067 SetTradeCash                  [u64 tin]
s2c 0x206E DispatchPartnerUpdatedCash    [u64 tin]                 to the partner
c2s 0x2068 AddTradeItem                  [u32 container][u32 owner][u32 slot][u32 count]
s2c 0x2070 DispatchTradeAddedItem        [u32 len][item snapshot][u32 count]   to the adder
s2c 0x206F DispatchTradePartnerAddedItem  (same body)                          to the partner
c2s 0x206B AcceptTrade                   (empty)
s2c 0x206C DispatchPartnerAcceptedTrade  [ProtoID32 partner]       to the OTHER side
s2c 0x206B (unnamed)                     (empty)                   acks YOUR accept
s2c 0x206D (unnamed)                     (empty)                   to BOTH: the trade COMMITS
```

then the `TradeCompletedSuccessfully` feedback overlay (`0x8D5A8B6B`) to both clients.

⚠️ **c2s `0x2068` is `AddTradeItem` while s2c `0x2068` is `DispatchRequestTrade`** — the same method index on the
two interfaces. The captured container kind is 104 (the backpack) and the owner field is the sender's own character
id. [V0: the two unnamed trade replies 0x206B and 0x206D]

## Friend list

On the **ChatServer** (`FriendListCodec`). Sweeping the whole capture for a game-tier friend opcode finds only
`c2s 0x2092 RequestNewJoinHasHostileInFriends`, a team-invite safety check — the list itself is chat-tier:

```
c2s 0x0028  [u32 characterId]                                        add
s2c 0x0028  [u32 characterId][u8 online][u8 5][u32 stamp][u8 level]   the added friend's state
c2s 0x0029  [u32 characterId]                                        remove
s2c 0x0029  [u32 characterId]                                        echoed back
s2c 0x0025  [u64 0][u32 stamp][u16][u8][u16 len][name]               presence push, BY NAME
```

**One-sided, with no accept handshake** — stated outright in the capture's chat — so there is no invite state
machine: an add is a write plus a presence subscription. Note the presence push is addressed by NAME while
everything else uses the character id.

## How ours is wired

`TeamService` is a composed collaborator of the **CsPlayerAgent listener**, like `GuildService`: the CS tier owns the
roster and every roster dispatch, and takes the cross-connection client lookup as a delegate seam. A team is
**ephemeral** — nothing is persisted, deliberately, because a retail team does not survive a logout, so a table would
be state with no owner (and there is no migration to carry).

`LeaveTeam` / `KickFromTeam` arrive on the player's **world** connection, so the world listener reaches the roster
through `ICsPlayerAgentDispatcher` — the same seam shape as `IGameServerDispatcher` and `IChatServerDispatcher`, and
for the same reason: the relationship is mutual (the roster pushes group-membership IIRs *back* onto world
connections via `IGameServerDispatcher.SendGroupMembership`), and a listener only exists once the host starts it.

A logout leaves the team too, or the roster keeps a member nobody can reach and the next broadcast pushes at a dead
connection.

`TeamServiceTests` gates the roster transitions (accept/leave/kick/promote/dissolve, and that an accept with no
pending invite does nothing — the inviter is taken from the server's pending-invite table, not the client's body).

Still to do: the trade and friend HANDLERS (the codecs and their byte-exact gates are in place; open work in Gitea
#101 / #99).
