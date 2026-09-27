# Raids

A raid is the team system one level up: a set of teams rather than a set of characters. Everything follows from
that — its leader is a team, its roster messages carry whole teams, and the two messages that have no team
counterpart (move a member between teams, swap two members) exist precisely because a team has no sub-teams.

**Nothing on this page was observed.** A sweep of all 120 dumps finds zero raid traffic of any kind — no
captured session ever formed a raid. Every method, parameter list and
body layout here comes from the client: `docs/re/aoc-client-rpc-methods.json` for the signatures and the
`CSPlayerInterfaceDispatch_t` readers for the bodies. That is why the page keeps saying which reader; a raid message
is only as good as the function that reads it. The gaps are registered in
[`../protocol/gaps.md`](../protocol/gaps.md), not smoothed over. Open work is tracked in Gitea #669.

## Why the bodies are not guesses

Every raid body is built out of records retail did send, byte-exactly, in
[`teams-trade-friends.md`](teams-trade-friends.md):

| client type | what it is | pinned by |
| --- | --- | --- |
| `cTeam` | a whole team — id, leader, members, settings | `DispatchYouJoinedTeam` f693, byte-exact |
| `cCharacter` | one member record | `DispatchYouInvitedToTeam` f678 / `DispatchTeamInvite` f680, byte-exact |
| `ID32` | `[u32 class][u32 instance]` | every captured team and guild message |
| string | `[u16 len][latin1]` | the same |
| list frame | `(count + 1) × 0x3F1` | the same |

The load-bearing check is that the client reads a raid's team through the **same function** as a team's:
`FUN_00557960` serves `DispatchYouJoinedTeam`, `DispatchRaidTeamJoined` and `DispatchRaidTeamUpdate` alike, and
`FUN_005574d0` serves the member vector and every raid `i_cWho`. So `TeamRosterCodec` and `TeamMemberRecordCodec`
declare the raid methods rather than being copied; `RaidCodecTests` asserts the raid announcement equals the
captured team bytes.

## Messages

### c2s — `CSPlayerAgent`

```
0x68 RaidCreate()                                            (empty)
0x69 RaidLeave()                                             (empty)
0x6A RaidInviteTeam(ID32 teamID)
0x6B RaidInviteTeamAccept(ID32 teamID)
0x6C RaidInviteTeamDecline(ID32 teamID)
0x6D RaidKickTeam(ID32 teamID)
0x6E RaidKickCharacter(ID32 characterID)
0x6F RaidMoveTeamMember(ID32 characterID, ID32 destinationTeamID)
0x70 RaidSwapCharacters(ID32 characterID, ID32 characterID2)
0x71 RaidInviteCharacter(ID32 charID, string message)
0x72 RaidInviteCharacterByName(string charName, string message)
0x73 RaidAcceptInvite(ID32 charID)
0x74 RaidDeclineInvite(ID32 charID)
0x75 RaidSetLootOption(int lootMode, bool useNeedGreed, int lootThreshold)
0x76 RaidSetMasterLooter(ID32 characterID)
```

`RaidSetLootOption` is `TeamSetLootOption` field for field, and that one IS captured — the same
`[u32 mode][u8 needGreed][u32 threshold]`, so `TeamLootOptionCodec` serves both.

### s2c — `CSPlayerInterface`

```
0x93 DispatchYouJoinedRaid(cMatch)                       RaidMatchCodec
0x94 DispatchRaidTeamJoined(cTeam)                       TeamRosterCodec
0x95 DispatchRaidTeamLeft(ID32 teamID)                   EntityIdCodec
0x96 DispatchRaidTeamUpdate(cTeam)                       TeamRosterCodec
0x97 DispatchRaidCharacterUpdate(cCharacter)             TeamMemberRecordCodec
0x98 DispatchRaidNewRaidLeader(ID32 teamID)              EntityIdCodec
0x99 DispatchRaidLootOptionChanged(mode, needGreed, threshold)   TeamLootOptionCodec
0x9A DispatchRaidNewMasterLooter(cCharacter)             TeamMemberRecordCodec
0x9B DispatchRaidInvite(cCharacter who, string message)  TeamInviteCodec
0x9C DispatchRaidTargetIsBusy(cCharacter)                TeamMemberRecordCodec
0x9D DispatchRaidIsAlreadyInRaid(cCharacter)             TeamMemberRecordCodec
0x9E DispatchRaidInvitationTimedOut(cCharacter)          TeamMemberRecordCodec
0x9F DispatchRaidTargetDeclinedRaidInvite(cCharacter)    TeamMemberRecordCodec
0xA0 DispatchRaidYouAreNoLongerInvited(cCharacter)       TeamMemberRecordCodec
0xA2 DispatchRaidCantInviteInvalidSubscriptionFlags(cCharacter)
0xA4 DispatchRaidCantCreateInvalidMemberSubscriptionFlags(cCharacter)
```

**`i_cWho` is the whole member record, not an id.** Nine of the sixteen carry a `cCharacter`, which reads like an
ID32 from the name and is not — the client calls `FUN_005574d0` on every one of them. A raid message answered with
eight bytes would desync the stream on the tenth field.

`0xA2` / `0xA4` are the subscription-flag refusals — a live-service entitlement check we model nothing of, so they
are never sent.

## `cMatch`

`DispatchYouJoinedRaid`'s parameter is called `i_cMatch` and read by `FUN_005584c0`, which is that reader's only
caller in the exe. Despite the name it is the raid record and nothing else.

```
[ cTeam ]                                     the raid: id, leader, member list, settings
[ u32 (count+1)*0x3F1 ][ cTeam × count ]      the member TEAMS
[ u32 (count+1)*0x3F1 ][ ID32, bool, bool ]   one entry per character, stored in a map keyed by the ID32
[ ID32 ][ u32 ][ bool ][ ID32 ][ u32 ][ u32 ][ u32 ]     33-byte tail
```

We send the header `cTeam` with the raid's id, the raid leader and an **empty member list** — the people are in the
sub-teams — plus the raid's settings block. The flag map goes out empty and the tail zero.
`[V0: the cMatch header team]` `[V0: the cMatch tail]`

## What the server does

`RaidService` (`Faolan/CSPlayerAgent/RaidService.cs`) holds the raid as an ordered list of team ids and asks
`TeamService` for each team's `cTeam` when it needs one on the wire. There is no raid roster of its own, so raid
membership cannot drift out of step with team membership: a raider is someone in one of the raid's teams.

| action | what happens |
| --- | --- |
| `RaidCreate` | the caller's team becomes a one-team raid; a solo player gets a team first |
| `RaidInviteCharacter` / `…ByName` / `RaidInviteTeam` | a pending invite, and `DispatchRaidInvite` to the target — a team invite is shown to that team's LEADER |
| `RaidAcceptInvite` | the accepter's **whole team** joins; they get the raid, the raid gets the team |
| `RaidLeave` / `RaidKickCharacter` | the raider leaves their team too — a raid slot is a seat in one of its teams. Their team leaves the raid only when it is gone |
| `RaidKickTeam` | the team leaves the raid and survives as a team |
| `RaidMoveTeamMember` / `RaidSwapCharacters` | re-seat raiders between the raid's own teams; both teams are re-announced |

Two rules mirror the team tier deliberately: the inviter is taken from **our** pending-invite table rather than the
echoed body (a forged accept must not pull anyone into a raid), and a raid down to **one team** dissolves, exactly
as a team down to one member does.

Two rules are the raid's own:

- **The leader is a TEAM.** `DispatchRaidNewRaidLeader` carries an `i_cTeamID`, so when the leading team leaves,
  the next team takes the lead — not the next character.
- **A one-member team inside a raid is legitimate.** `TeamService.Remove` dissolves at one member, which is right
  for a standalone group and wrong for a raid slot, so the move/swap path goes through `TeamService.MoveMember`,
  which does not dissolve.

There is no `~Raid` chat channel. Only `~Guild` and `~Team` appear anywhere in the corpus, so adding one
would be inventing a channel retail does not have.

## Gates

`RaidCodecTests` pins each raid body against the captured team bytes it is made of, plus round-trips for `cMatch`
and the three c2s shapes with no team twin. `RaidServiceTests` pins the state machine — including that a real
raid's `cMatch` round-trips through its own codec, which is what would catch a list frame or a field width going
wrong.
