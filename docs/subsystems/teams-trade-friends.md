# Teams, player trade and the friend list

All three decoded from one capture: `login_2-clients_group-trade-friend`, two retail clients on the same host, so
**both sides of every exchange are on tape in one file** — the dump's `Stream` field separates the ten connections
(five per client). The players narrate each step in chat, which is what labels the phases: *"leave goup now"*,
*"kick"*, *"trade"*, *";both friends added, friends are one sided, no accept needed"*, *"%set lootoptions, pass on
loot to rare"*.

`TeamTradeFriendCodecTests` gates every message we build against retail's bytes, with the captured hex pinned inline —
so the whole subsystem is re-proven on every test run rather than by a tool someone has to remember to point at the
corpus. [V3: TeamTradeFriendCodecTests.MemberRecord_RoundTripsCapturedBytes]

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

102 bytes for a 10-character name. The first trailing u32 is the **class**: the two captured members carry `0x29`
and `0x2C`, which are exactly `Class.Necromancer` and `Class.Demonologist`, and the chat server's friend-add reply
ends with the same byte for the same character — that is how the two corroborate each other. Then the **map id**
(twice; the client greys out members in another playfield), one more **per-character word** (2 and 3 in the capture —
not identifiable from two characters; sex, race and level are all candidates), and a 9-byte per-character
**opaque**. [V0: the team member record's level] The rest are identical across both characters and every message and are carried as captured.

⚠️ That leading field was first read as the LEVEL, because `0x29`/`0x2C` are plausible levels *and* the friend reply
appeared to confirm it — two independent sightings of the same wrong field. The enum match settles it, and the client
agrees: fed a level, it accepted the invite record, answered it with `RequestTeamInvite`, and refused to raise the
invite window, because it cannot build one for a class that does not exist. Nothing in the capture identifies which
field carries the level, since both captured members share it.

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

The capture only exercises the happy path, but the client's recovered method table completes the **c2s** set, and its
indices line up exactly with the captured ones (`0x2065`/`0x2067`/`0x2068`/`0x206B` all match), so it can be trusted
here:

```
c2s 0x2064 RequestTrade(ID32 target)              a dedicated request, alongside the BCC path the capture uses
c2s 0x2066 DeclineTrade(ID32 target)              refuse before the window opens
c2s 0x2069 RemoveTradeItem(int destPos)           take an item back off the table
c2s 0x206A MoveTradeItemInternal(pos1, pos2)      drag an offered item within the window
c2s 0x206C AbortTrade()                           CANCEL
```

⚠️ `AddTradeItem`'s signature is `(ID32 inventory, int itemPos, int destPos)` — **the last field is the trade-window
slot, not a quantity.** There is no count in the message at all: the whole stack moves. Read as a count it happened to
work only because the capture's one add carried destPos 1 for a stack of 1; a destPos of 0 would have moved nothing.

The matching s2c halves are NOT inferable: the client's catalog names `DispatchTradeRemovedItem` /
`DispatchTradePartnerRemovedItem` but its s2c numbering is offset against the capture (it puts `DispatchRequestTrade`
at `0x205F` where retail sends `0x2068`), so their opcodes need a capture. Open work in Gitea #427.

## Friend list

On the **ChatServer** (`FriendStateCodec`, `ChatSenderInfoCodec`, `FriendPresenceCodec`, `FriendRefCodec`). Sweeping the whole capture for a game-tier friend opcode finds only
`c2s 0x2092 RequestNewJoinHasHostileInFriends`, a team-invite safety check — the list itself is chat-tier:

```
c2s 0x0028  [u32 characterId]                                        add
s2c 0x0028  [u32 characterId][u8 online][u8 5][u32 stamp][u8 class]   the added friend's state
c2s 0x0029  [u32 characterId]                                        remove
s2c 0x0029  [u32 characterId]                                        echoed back
s2c 0x0025  [u64 0][u32 stamp][u16][u8][u16 len][name]               presence push, BY NAME
```

**One-sided, with no accept handshake** — stated outright in the capture's chat — so there is no invite state
machine: an add is a write plus a presence subscription. Note the presence push is addressed by NAME while
everything else uses the character id.

The chat framing is `[u16 opcode][u16 bodyLen][body]`, read from the raw pcap (`0025 001b …`, `0028 000b …`).

An **add is four packets**, and the third is the one that matters:

```
c2s 0x0028  [u32 characterId]                                          add
s2c 0x0025  [u64 0][u32 stamp][u16 len][u8 0x53][u16 len][name]         presence, BY NAME
s2c 0x0014  [u32 characterId][u32 0][u16 len][name]                     the id -> NAME binding
s2c 0x0028  [u32 characterId][u8 online][u8 level][u32 stamp][u8 class]  the friend's state
```

At **login** the stored list arrives as `0x0014` + `0x0028` **pairs**, one per friend, in the same burst as the channel
subscriptions and the welcome message — and with **no** presence push. Both message SHAPES are byte-gated
[V3: TeamTradeFriendCodecTests.Friend_NameBinding_IsByteExact]; the burst PLACEMENT is not, and cannot be read out of
the two-client capture — that account had no friends before it added them, so the restore-on-login timing comes from a
later login-with-friends session (see the note on it in `ChatServerListener`).

⚠️ **Omitting the `0x0014` renders nothing at all** — no entry, no error. The client is left holding an id and a state
with no name attached. That was the whole reason the list stayed empty while the other two messages were byte-perfect,
and it cost several rounds of looking at the wrong things.

**Level and class are both in the state**, which is what the client's hover tooltip shows. Six friends of six different
levels and classes settled a reading that one capture of two characters could not: every trailing byte is a valid
`Class` id (`HeraldOfXotli`, `BearShaman`, `Demonologist`, `TempestOfSet`, `Demonologist`, `Barbarian`) while the level
byte reads 7/10/5/8/10/5. Before that the level had been read as a constant `0x05` and the class byte's identity was
genuinely ambiguous against levels 41/44.

**The presence push's u16 is a length**, not a constant: it is `nameLen + 3` (the tag byte plus the length-prefixed
name), verified across five names — 6→9, 10→13, 7→10, 6→9, 8→11. Read off a single 10-character name it looked like a
fixed `0x000D`, which left every push for any other name malformed.

**Presence must be PUSHED on login and logout.** After restoring its own list, a character's entry is re-sent to every
online character who has it on theirs (`0x0014` + `0x0028`), and again with `online = 0` when it disconnects. Without
this a friend's status is frozen at whatever it was when the WATCHER logged in - whoever connects first sees the other
as offline forever, because the restore can only report who is already connected - and **the client hides offline friends**
(confirmed in-client), so the panel looks EMPTY even though the entry exists - while the right-click menu still offers
"remove from friends list" for the friend it will not display. That gap between "the client knows the entry" and "the
client shows it" is the tell. The reverse lookup this needs is a genuinely different query from the forward one,
because the list is one-sided. ⚠️ The MESSAGE is the captured one; the TRIGGER is inferred - no capture covers a friend
logging in or out while someone watches. Verified in-client 2026-07-25.

**The state's stamp is a union on the online flag.** Offline is a unix **last-seen** — the captured offline friend's
`0x6A64AD51` is 2h06 before that capture was taken — and we send `Character.LastConnection` for it, the same column and
union `GuildService`'s member record already uses. The online value is **not a time** (`0x01EDA8B2` reads as 1971) and
not a plain constant either (one online friend carries `0x01EDAF93`); its magnitude sits beside the character ids, so an
id/handle is the better guess. [V0: the friend state's online stamp]

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

Three requests the client sends around a join get **no reply from retail either** — `SetPassOnLootLevel` (`0x204D`)
and `RequestNewJoinHasHostileInFriends` (`0x2092`) on the game tier, `TeamSetLootOption` (`0x2006`) on the CS tier.
Checked against the capture: nothing follows them on the same stream. They are fire-and-forget, so leaving them
unanswered is retail-faithful; what remains is to APPLY the loot settings rather than only accept them.

Still to do: the trade and friend HANDLERS (the codecs and their byte-exact gates are in place; open work in Gitea
#101 / #99).

### Addressing and the header, which is what actually made the invite work

Verified in-client 2026-07-25: invite, accept, leave and kick all work.

**The reply carries a trailing header byte.** `DispatchReplyTeamInvite` (`0x209F`) must be sent with `headerData`
`0x01`, the same RpcHeader tail `KeepAliveService`'s `DispatchPong` puts on this interface. Retail carries it on both
the request and the reply (`f682`, `f683`) while the CS-tier invite dispatches carry none — which is why the invite
itself was always accepted. Without it the client **discards the reply**, and since the reply is what raises the
window, the symptom is: the client takes the invite, echoes it back in `RequestTeamInvite`, and nothing happens.
Replaying retail's own captured bytes for both messages changed nothing, which is what finally pointed at the header
rather than the body. Across the capture the `0x01` tail appears on `0x2082`, `0x2092`, `0x209F`, `0x20AC`, `0x20AD`,
`0x20C5` and `0x20D0` — all ≥ `0x2082`, none below.


Every CsPlayerAgent s2c packet is addressed to the **recipient's own** `CSPlayerInterface` instance — its character
id — and sent from the server object (`CSPlayerAgent`, instance `0x04`). Retail does this on every one: `f678` carries
the inviter's id, `f680` the target's. A push addressed to instance `0` arrives and is dropped, and the failure is
quiet in exactly the way that costs time: the invited client received `DispatchTeamInvite`, answered it with
`RequestTeamInvite`, and simply never showed a window.

The game tier's `DispatchReplyTeamInvite` likewise replies from the `GameCharAgent` object (instance `0x10`), not from
the world sender endpoint the rest of that listener uses — a reply belongs to the interface pair its request arrived
on.
