# PvP minigames — the match loop

How our server runs a PvP minigame match, from the queue to the teleport back out. Everything on this page is
verified in-client (two retail clients, matches walked on The Call of Jhebbal Sag, Blood Ravine, Lost Temple and
Hallowed Vaults) except where marked. The wire reference — every message's body, the
client readers each shape was pinned against, and the `[V0]`s that remain — is
[`../re/minigame-massivepvp-messages.md`](../re/minigame-massivepvp-messages.md). A match does not model
the rest of the game inside it: capture zones and the score messages. Open work tracked in Gitea #146.

## The loop

```
c PutIntoMatchMaking(map, type, bringTeam, queueType)      the signup (capture-proven)
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
        │  the match runs; a kill across the side line scores (see PvP damage, below)
        ▼
   time limit up, or a side reaches the score limit
        ▼
s DispatchMinigameMatchEnded(winningSide, yourResult)  per member - the results page
        │  the page stands (its Quit/Done button is an ordinary LeaveMatch, which carries them home);
        ▼  after 60 s anyone still in it is swept out and the match dissolves
```

* **Queue capacities are the client's own**: its minigame window labels every queue `0/24` (Conquest on The
  Call of Jhebbal Sag) or `0/12` (the three Capture The Skull maps, Annihilate Opposing Team on Totem
  Torrent) — `StartingData.MinigameQueueCapacity`, with `.matchstart` as the force-with-fewer override. Where
  the client stores the number is not recovered, so the table records what its window displays.
* **Two "join first available" signups** resolve to Conquest on The Call of Jhebbal Sag — the pair the
  captured signups used most. A concrete signup names the minigame for everyone compatible with it.
* **The countdown move is server-initiated**: the client's own fifo says *"Entering minigame in
  about 10 seconds"*, passive, and it sends nothing when the countdown lapses. The c2s
  `PvPMatchMakingTeleportToMinigame` is the manual rejoin path, kept as such. The 10 s is ours (nothing
  recovered states retail's value).

## The end of a match

A started match ends on whichever comes first — its ruleset's **time limit**, or a side reaching its **score
limit**. Both numbers are the client's own: they are fields `F` and `G` of the `PvPMatchRules_c` the buildup
view displays (`TimeLimitView` / `ScoreLimitView`, and `GetTimeLeft` / `GetScoreLimit` in the Flash API), and
a matchmade ruleset states them truthfully rather than inventing a third source. The clock is measured from
the same instant the client's own is: `DispatchMinigameMatchStarted`'s `i_nSecondsSinceStarted` seeds the
client's match-start field, so the server's end lands where the player's counter reaches 00:00 — which is what
the client does not do for itself. It computes `GetTimeLeft = matchStart + timeLimit − now` and then nothing;
retail's server owns the end, and without it a match runs for ever with its members stranded on the map.

Every member is then sent **`DispatchMinigameMatchEnded(i_eWinningSide, i_eYourResult)`** — the winning side
and, per player, what it meant for them:

| `i_eYourResult` | client | when |
|---:|---|---|
| 0 | `MatchWon` | the player's side had the higher total |
| 1 | `MatchLost` | the other side did |
| 2 | `MatchTie` | neither — including a scoreless match, and every value the client does not recognise |
| 3 | — | the client's own reset value, "no result". Never sent |

The three come off the client's results page, not a capture: `FUN_01738490` branches on the stored word to
pick the LDB-100 header token. The winning side needs no interpretation — it is the side value the client
itself assigned and we only ever echoed (1 and 2, which the status view paints blue and red).

**Ours, and marked as such:** the 60-second grace before a finished match dissolves. The results page is
dismissed by its own Quit/Done button (an ordinary `LeaveMatch`, which takes the existing return trip), and
nothing recovered states a server-side deadline — but a member who just closes the window would otherwise stay
in a finished match on a minigame map for ever.

**`.matchstop [reason]`** is the GM force-end: every member gets `DispatchForceStopMinigame(message)` — the
client's own abort path, not the results page — and the match dissolves. The end half of the loop's test
bootstrap, the way `.matchstart` is the start half.

## PvP damage

Player-versus-player damage exists only between opponents of one started minigame match. The gate is
`ICsPlayerAgentProvider.ArePvpMatchOpponents`: both must hold membership of the same started, unfinished match,
on different sides. Everything else is refused with its own log line — a hit on a team-mate, on a bystander, or
anywhere outside a match.

The damage itself takes the ordinary funnel (`CombatService.ApplyDamage`), so a melee swing, a spell, a DoT
tick, a ground AoE and an area effect all land the same way; only melee cleave stays NPC-only (its target
filter excludes players). The victim gets the incoming floating number and their own vitals push — the same
pair the NPC-hits-player path in `WorldSimulation` sends — and the attacker gets the target's bar moving.

A PvP death is not a second death sequence: the release screen, the buff strip and the death penalty are
the world tick's `TickPlayerDeaths`, which fires off empty vitals whatever emptied them, so a PvP kill takes
exactly the route a mob kill, a fall or lava does. What the PvP path owns is the scoreboard — the career pair
the login snapshot carries as stats `0x290` / `0x292` (`Character.PvpKills` / `PvpDeaths`, persisted per kill),
and the match's own kill credit, which is what can end the match on its score limit.

**Not built: open-world PvP.** Retail also allows PvP by playfield, and the client ships that table — RDB
1000509/1000510/1000512 `PlayfieldPvPData_c`, decoded in
[`../rdb/client-data-tables.md`](../rdb/client-data-tables.md), where ruleset 7 is the all-zero default on 186
playfields and rulesets 1/8, 6, 2, 11, 12 partition the real PvP zone types. The ruleset columns are not named,
so nothing reads them and open-world PvP — with its murder-point / criminal half (open work tracked in Gitea
#503) — stays refused. A
minigame needs no such table: the sides are locked and the fight is the point.

## The match-owned instance

A started match owns one copy of its minigame map and every member — both sides — lands in it:
`WorldState.MatchInstance`, the minigame twin of the team instance, with the same empty-means-gone
lifetime. The world entry asks the CS tier through `ICsPlayerAgentProvider.PvpMatchEntryOf`, and the match
check outranks the stay-in rule — so a re-formed match on the same map still routes into the new match's fresh
copy (verified: back-to-back matches on one map produced two distinct instances, both sides together in each).

## Leaving, and every way out

* **Leave** (`LeaveMatch`, which is also the buildup view's Cancel Match — there is no separate cancel verb on
  the wire): the leaver gets `MatchRemoved` and, if standing in the minigame, a teleport back to where they
  stood when the countdown took them; the others get the record-shaped `PlayerLeft`. The lead passes to the
  longest-standing member (`NewMatchLeader`) — unless the leader leaves an unstarted lobby, which cancels the
  match for everyone (`MatchRemoved` broadcast). The started/unstarted split and the succession rule are
  ours; nothing recovered states retail's.
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
ListFrame of ID32 pairs — while anyone is sideless). This path is built against the recovered message set
but has not had an in-client walk of its own; the matchmade path exercises most of the same sends.

## Two things that look like bugs and are not

* **Dual-boxing one account shows "You created a match" on both clients** — the client keys the created/joined
  fifo on its local character identity, and two clients under one account resolve the same one. The server
  sends exactly one leader; see the wire page's chooser RE.
* **"Latency: ~4800 mS" right after zoning in** is the client's own request queue draining its per-NPC 0x2020
  polls, not network or tick — it settles within half a minute of standing still.

## Code map

`PvpMatchService` (CSPlayerAgent) — the match registry, formation, lifecycle, the countdown teleport, the
clock/score end and the PvP gate; `MinigameResult` — the three result words, off the client's results page;
`CombatService.ApplyDamage` / `ApplyPlayerDamage` — the PvP half of the damage funnel;
`PvpMatchMakingService` — the queue and its board (capture-matched); `PvpMatchRulesCodec` /
`PvpMatchCodec` — the ruleset element and the `i_cMatch` payload; `WorldState.MatchInstance` +
`WorldEntryService` — the instance routing, the match clock and the dead-match redirect;
`StartingData.MinigameQueueCapacity` — the client-displayed queue sizes.
