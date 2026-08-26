# PvP minigames — the match loop

How our server runs a PvP minigame MATCH, from the queue to the teleport back out. Everything on this page is
**verified in-client** (2026-08-26, two retail clients, matches walked on The Call of Jhebbal Sag, Blood
Ravine, Lost Temple and Hallowed Vaults) except where marked. The wire reference — every message's body, the
client readers each shape was pinned against, and the `[V0]`s that remain — is
[`../re/minigame-massivepvp-messages.md`](../re/minigame-massivepvp-messages.md). What a match still LACKS is
the game inside it: capture zones (#513), scoring, and a match end (open work: Gitea #146).

## The loop

```
c PutIntoMatchMaking(map, type, bringTeam, queueType)      the signup (capture-proven, #882)
        │  each signup runs a formation pass; the queue is FIFO
        ▼
   queue FULL for the resolved minigame?  ──no──▶  waits (the board keeps counting)
        │ yes (or GM `.matchstart` force-forms with whatever is queued, down to one member)
        ▼
s YouJoinedMatch(i_cMatch)                to every member: roster + rules + match id + leader ID32
s PlayerJoined(cCharacter, side)          every member announced to every member (the client dedups by id)
s CharacterNowLockedToSide(ID32, side)    a matchmade match's sides are the queue's, locked, alternating 1/2
s StartClicked(10)                        the countdown - the client displays it and WAITS
        │  10 s, server-timed
        ▼
   change-map handoff per member          the ordinary teleport; the entry routes to the MATCH's instance
s DispatchMinigameMatchStarted(secondsSinceStarted)   so a late zoner sees the running clock
```

* **Queue capacities are the client's own**: its minigame window labels every queue `0/24` (Conquest on The
  Call of Jhebbal Sag) or `0/12` (the three Capture The Skull maps, Annihilate Opposing Team on Totem
  Torrent) — `StartingData.MinigameQueueCapacity`, with `.matchstart` as the force-with-fewer override. Where
  the client stores the number is not yet recovered; until it is, the table records what its window displays.
* **Two "join first available" signups** resolve to Conquest on The Call of Jhebbal Sag — the pair the
  captured signups used most. A concrete signup names the minigame for everyone compatible with it.
* **The countdown move is SERVER-initiated** — settled live: the client's own fifo says *"Entering minigame in
  about 10 seconds"*, passive, and it sends nothing when the countdown lapses. The c2s
  `PvPMatchMakingTeleportToMinigame` is the manual rejoin path, kept as such. The 10 s is ours (nothing
  recovered states retail's value).

## The match-owned instance

A started match owns ONE copy of its minigame map and every member — both sides — lands in it:
`WorldState.MatchInstance`, the minigame twin of the team instance (#512), with the same empty-means-gone
lifetime. The world entry asks the CS tier through `ICsPlayerAgentProvider.PvpMatchEntryOf`, and the match
check outranks the stay-in rule — so a re-formed match on the SAME map still routes into the new match's fresh
copy (verified: back-to-back matches on one map produced two distinct instances, both sides together in each).

## Leaving, and every way out

* **Leave** (`LeaveMatch`, which is also the buildup view's Cancel Match — there is no separate cancel verb on
  the wire): the leaver gets `MatchRemoved` and, if standing in the minigame, a teleport back to where they
  stood when the countdown took them; the others get the record-shaped `PlayerLeft`. The lead passes to the
  longest-standing member (`NewMatchLeader`) — unless the leader leaves an UNSTARTED lobby, which cancels the
  match for everyone (`MatchRemoved` broadcast). ⚠️ The started/unstarted split and the succession rule are
  OURS; nothing recovered states retail's.
* **Relog out of a dead match**: a character whose last save was inside a match that no longer runs (a
  mid-match disconnect ends their membership; a restart ends the match) does not strand on the PvP map. The
  world entry redirects to the match's persisted return location (`Character.PvpReturnMapId/X/Y/Z`, written at
  the countdown teleport), falling back — for a member who was already ON the minigame map when the match
  formed — to the bound respawn point, else the Tortage Beach default.
* **Stale client state**: a client still showing the buildup view for a match this process never knew (a
  pre-restart match survives the client's relog) recovers the moment it sends anything about it —
  `LeaveMatch`, `StartMatch` or `GetMatchInfo` about an unknown match all answer `MatchRemoved`, which resets
  the window (verified live).

## Custom matches

The premade path is wired end to end — `CreateMatch` (the client-built `PvPMatchRules_c` element) makes the
creator the leader, `JoinMatch` honors the password and the ban list and brings the team when asked,
`SetSide`/`MatchSay`/`KickPlayer`/`BanPlayer`/`MakePlayerMatchOwner`/`ForceSide` (which locks, so a later
`SetSide` gets the named refusal) and `StartMatch` (refused with `CantStartMatchNotAllPlayersHaveSide` — a
ListFrame of ID32 pairs — while anyone is sideless). ⚠️ This path is built against the recovered message set
but has NOT had an in-client walk of its own; the matchmade path exercises most of the same sends.

## Two things the live runs taught that are not bugs

* **Dual-boxing one account shows "You created a match" on BOTH clients** — the client keys the created/joined
  fifo on its local character identity, and two clients under one account resolve the same one. The server
  sends exactly one leader; see the wire page's chooser RE.
* **"Latency: ~4800 mS" right after zoning in** is the client's own request queue draining its per-NPC 0x2020
  polls, not network or tick — it settles within half a minute of standing still.

## Code map

`PvpMatchService` (CSPlayerAgent) — the match registry, formation, lifecycle and the countdown teleport;
`PvpMatchMakingService` — the queue and its board (#882, capture-matched); `PvpMatchRulesCodec` /
`PvpMatchCodec` — the ruleset element and the `i_cMatch` payload; `WorldState.MatchInstance` +
`GameServerListener.WorldEntry` — the instance routing, the match clock and the dead-match redirect;
`StartingData.MinigameQueueCapacity` — the client-displayed queue sizes.
