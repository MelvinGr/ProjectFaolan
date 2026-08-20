# Minigames + Massive PvP: the message surface (Gitea #146, #147)

AoC's instanced PvP **minigames**, the open-world **Massive PvP** (Border Kingdom battlekeep) scoring layer, and
the **guild city / siege** tier that shares its area-id space. None of them is implemented. This page is the
wire surface for all three: what the 120 capture dumps actually contain, and what the client's own code says
about everything they do not.

Two sources, kept apart on purpose:

* **Captured** — seen in `<data-repo>/Captures/*.tshark.json`. Four c2s messages and two s2c replies, all of them
  the "is anything going on?" polls a client sends on login. Every one is handled today.
* **Client-recovered** — the rest. Method names and parameter types come from
  [`aoc-client-rpc-methods.json`](aoc-client-rpc-methods.json); field widths come from each
  `<Class>InterfaceDispatch_t::<Method>` reader in `AgeOfConan.exe`, which bails out per field with a string
  naming the field it was about to read (`tools/ghidra/DumpStringXrefs.java`, `STR=Dispatch_t::DispatchMinigame`
  and friends). The stream primitives are the shared ones: `FUN_0048f900` u32 BE, `FUN_0048f840` string
  (`[u16 len][latin1]`), `FUN_0048f6d0` a one-byte bool, `FUN_0048f720` u64.

## What the captures contain

Swept over all 120 dumps, both directions, for every opcode in the two systems' ranges:

| dir | class | method | n | body |
|---|---|---|---|---|
| c2s | CSPlayerAgent `0x54` | `PvPMatchMakingRequestInfo` | 16 | `[u32 queueType]`, always 0 |
| c2s | CSPlayerAgent `0x55` | `PvPMatchMakingIsInMatchMaking` | 6 | empty |
| c2s | CSPlayerAgent `0x77` | `MassivePvpInfoRequest` | 150 | empty |
| c2s | GameCharAgent `0x8D` | `RequestMassivePvPScoreData` | 6 | empty |
| s2c | CSPlayerInterface `0x92` | `DispatchPvPMatchMakingInfoRequestReturn` | 16 | `[0x3F1][u32 0]` — an empty list, "not queued" |
| s2c | CSPlayerInterface `0xA5` | `DispatchMassivePvpInfoRequestReturn` | 147 | the board, below |

**Nothing else in either system appears anywhere in the corpus** — no match creation, no side picking, no capture
zone, no score update. That is not a gap in the captures so much as a fact about them: no session ever queued for
a minigame. Note also that retail answers `PvPMatchMakingIsInMatchMaking` and `RequestMassivePvPScoreData` with
**no packet at all** when there is nothing to report, which is why both are consumed silently rather than replied
to.

## `DispatchMassivePvpInfoRequestReturn` (0x20A5) — the battlekeep board

The only message of either system with real captured content: 147 packets, 125 distinct bodies, every one an
8-guild board. Codec: `MassivePvpInfoCodec` · gate: `MassivePvpInfoCodecTests`.

```
[u32 lastLoadTime][u32 (count + 1) * 0x3F1]  { count × entry }  [u32 timeRegionOffset]

entry = [u32 key][u32 rank][u32 areaId][u32 7200][u32 900][u32 scoreA][u32 scoreB]
        [u32 lastBattleUnix][u32 guildId][u16 len][guildName][u16 len][otherName][u32 tail][u8 tailFlag]
```

The three top-level fields are the client's own names for them. **The list is a `std::map`, not a vector**: the
reader (`FUN_0134f090`) takes a u32 KEY off the stream before each element, which is why every captured entry
carries its rank twice.

Two readings that a corpus alone cannot separate, and one it can:

* `timeRegionOffset` equals `count + 1` in every captured body — but every captured body has exactly 8 entries,
  so "the trailing count" and "a small constant offset" are the same number here. The client names it, so the
  name wins; the value we send for the empty board (1) satisfies both readings.
* `otherName` is **empty in 990 of the 1000 captured rows**, and an empty `[u16 0]` followed by the `[u32][u8]`
  tail is seven zero bytes — indistinguishable from padding. The grammar carried in the server until #146 read
  exactly that ("[u16 len][name][7 zero bytes]") and therefore parsed 122 of the 125 bodies. The other three
  carry `"Fury (Dead)"` / `"Yellow Lotus (RP)"` there and are 17–19 bytes longer. This is the 0x3F1 trap's string
  cousin: **a length prefix that is zero in almost every capture reads as padding.**
* `areaId` takes three values (11040 / 12070 / 13050) in contiguous runs over the eight rows, and the 7200 / 900
  columns are constant in every entry of every body — 2 hours and 15 minutes in seconds.
* ⭐ **`areaId` is a PLAYFIELD id, corroborated from a file that knows nothing about this message.** All three
  are in `Maps` — `Border Kingdoms, Cimmerian End` (11040), `Border Kingdoms, Aquilonian End` (12070) and
  `Border to Kush` (13050) — and RDB **1000509** (`PlayfieldPvPData_c`, [client-data-tables.md](../rdb/client-data-tables.md))
  files exactly those three, plus `Massive Pvp Test` and `Minigames (General)`, under **PvP ruleset 6** and
  nothing else under it. So the wire's area id space and the client's own massive-PvP ruleset pick out the same
  set, from a capture and from an asset file respectively. Gitea #146 / #147.

`otherName` and the `tail`/`tailFlag` pair vary **together**: the ten rows with a non-empty `otherName` are
exactly the ten with a non-zero tail. What either means is not recoverable from the corpus.
`[V0: the Massive-PvP board's otherName / tail columns]`

## Client-recovered: the minigame messages (`GameCharInterface`, s2c)

Widths from the per-field readers; `FUN_004ca…`–`FUN_004cc…`.

| opcode | method | body |
|---|---|---|
| `0x87` | `DispatchMinigameScoreChanged` | `i_cUpdatelist` — a list element read (`FUN_004d54c0`), not scalar-decoded |
| `0x88` | `DispatchMinigameFullScore` | `i_cScore` — one score element (`FUN_00559350`/`FUN_00559940`) |
| `0x8A` | `DispatchMinigameMatchStarted` | `[u32 i_nSecondsSinceStarted]` |
| `0x8B` | `DispatchMinigameMatchEnded` | `[u32 i_eWinningSide][u32 i_eYourResult]` |
| `0x8C` | `DispatchMinigameCaptureZoneUpdate` | `[u32 i_nSensorVolumeID][u32 i_vCaptureState][u32 i_nCurrentOwnerSide][u32 i_nCurrentOwner][u8 i_bInsideCaptureZone]` (17 b) |
| `0x8D` | `DispatchMinigameCaptureZoneList` | three parallel lists: `i_cSensorVolumeIDs`, `i_cSensorVolumeNames`, `i_cOwnerVec` |
| `0x8E` | `DispatchMinigameCaptureZoneLeaveEnter` | `[u32 i_nSensorVolumeID][u8 i_bLeave]` |
| `0x8F` | `DispatchForceStopMinigame` | `[str i_cMessage]` |
| `0x90` | `DispatchSetMinigameTime` | `[u32 i_nNewTimeLimit]` |
| `0x91` | `DispatchMinigameStartsInXSeconds` | `[u32 i_nSeconds]` |
| `0x93` | `DispatchMassivePvPFullScoreUpdate` | `i_cMassivePvPScore` — a composite (`FUN_006d5800`) |
| `0x94` | `DispatchMassivePvPGuildScoreUpdate` | `i_cScoreList` — a list (`FUN_004d5640`) |
| `0x95` | `DispatchMassivePvPPlayerScoreUpdate` | `i_cScoreList` — the same list reader |
| `0x96` | `DispatchMassivePvPScoreNames` | `i_cNameMap` — a map (`FUN_0048a370`/`FUN_00488510`) |

A minigame's live state is therefore: a set of named **sensor volumes** (capture zones) with a per-zone owner and
capture progress, a match clock, and two score lists (per guild and per player). The zones are the client's own
sensor-volume ids — the same trigger-volume family as RDB 1000053 (see
[`../rdb/bounded-areas-1000053.md`](../rdb/bounded-areas-1000053.md)), which is where a real implementation would have to source
them.

## Client-recovered: matchmaking (`CSPlayerAgent` c2s / `CSPlayerInterface` s2c)

Scalar bodies, complete:

| dir | opcode | method | body |
|---|---|---|---|
| c2s | `0x4A` | `PvPMatchMakingCreateMatch` | `PvPMatchRules_c` element |
| c2s | `0x4B` | `PvPMatchMakingJoinMatch` | `[u32 matchId][u8 bringTeam][str password]` |
| c2s | `0x4C` | `PvPMatchMakingSetSide` | `[u32 side]` |
| c2s | `0x4D` | `PvPMatchMakingMatchSay` | `[str message]` |
| c2s | `0x4E` / `0x51` / `0x53` / `0x55` | `LeaveMatch` / `TeleportToMinigame` / `RemoveFromMatchMaking` / `IsInMatchMaking` | empty |
| c2s | `0x52` | `PvPMatchMakingPutIntoMatchMaking` | `[u32 map][u32 type][u8 bringTeam][u32 queueType]` |
| c2s | `0x54` | `PvPMatchMakingRequestInfo` | `[u32 queueType]` |
| c2s | `0x56` / `0x57` | `KickPlayer` / `BanPlayer` | `[ID32 character][str message]` |
| c2s | `0x59` | `PvPMatchMakingForceSide` | `[ID32 character][u32 side]` |
| c2s | `0x46` / `0x47` | `Apply/WithdrawFromMassivePVPBattleQueue` | `[ID32 character][ID32 guild][u32 area]` |
| s2c | `0x75` | `DispatchPvPMatchMakingWantsToStart` | `[u32 i_nKickedInSeconds]` |
| s2c | `0x76` | `DispatchPvPMatchMakingPlayerJoined` | `[ID32 i_cPlayer][u32 i_nSide]` |
| s2c | `0x77` | `DispatchPvPMatchMakingPlayerChangedSide` | `[ID32 i_cPlayer][u32 i_nNewSide]` |
| s2c | `0x78` | `DispatchPvPMatchMakingPlayerLeft` | `[ID32 i_cCharacterID]` |
| s2c | `0x79` | `DispatchPvPMatchMakingPlayerKicked` | `[ID32 i_cCharacterID][str i_cMessage]` |
| s2c | `0x7A` | `DispatchPvPMatchMakingErrorCode` | `[u32 i_nErrorCode]` |
| s2c | `0x7F` | `DispatchPvPMatchMakingStartClicked` | `[u32 i_nTeleportInSeconds]` |
| s2c | `0x80` | `DispatchPvPMatchMakingYouAreInMatchMaking` | `[u32 i_nMap][u32 i_nType][u8 i_ShowFifo][u8 i_AsTeamMember][u32 i_queueType]` |
| s2c | `0x83` | `DispatchPvPMatchMakingMatchSay` | `[str i_cFrom][str i_cMessage][u8 i_bFifo]` |
| s2c | `0x84` | `DispatchPvPMatchMakingCollectingMorePlayers` | `[u32 i_nSecondsUntilForcedStart]` |
| s2c | `0x85` | `DispatchPvPMatchMakingNewMatchLeader` | `[ID32 i_cNewMatchLeader]` |
| s2c | `0x86` | `…TeamLeaderSignedUpForMatchMaking` | `[u32 i_nMap][u32 i_nType]` |
| s2c | `0x8B` | `…CantChangeSideYouHaveBeenLockedToSide` | `[u32 i_nSide]` |
| s2c | `0x8C` | `…CharacterNowLockedToSide` | `[ID32 i_cCharID][u32 i_nSide]` |
| s2c | `0x8D` | `…CharacterNoLongerLockedToSide` | `[ID32 i_cCharID]` |
| s2c | `0x92` | `DispatchPvPMatchMakingInfoRequestReturn` | `[list i_cList][u32 i_nNumberOfAnyApplies]` — captured empty |
| s2c | `0xA6` | `DispatchMassivePvPBattleQueueFeedback` | `[u32 i_nFeedbackType][u32 i_nFeedbackValue]` |
| s2c | `0xA7` | `DispatchMassivePvpStartCountDown` | `[u32 i_cEndTime]` |
| s2c | `0xA8` | `DispatchMassivePvpBattleQueueStatusUpdate` | `[u32 i_nArea][u8 i_inQueue]` |

`const ID32&` is two words (`[u32 class][u32 instance]`), per
[`../protocol/02-rpc.md`](../protocol/02-rpc.md).

The three messages carrying a whole match — `YouJoinedMatch` / `MatchInfo` (`i_cMatch`) and `SearchResult`
(`i_cMatches`) — read a `PvPMatchRules_c` element (`FUN_0055bdb0` → `FUN_00558b70`, three u32 and a string
group, then `FUN_0053b020`). Its column layout is not pinned here: with zero captured instances there is nothing
to check a field split against, and the element reader is shared with the search filter.
`[V0: the PvPMatchRules_c element layout]`

## Client-recovered: guild cities + battlekeeps (Gitea #147)

The siege layer sits on the same tier and shares the board's area-id space — `RequestBattleKeepInformation`
takes a vector of area ids, and the board's `areaId` column is where those ids show up with values
(11040 / 12070 / 13050). **Not one of these eleven messages appears in any of the 120 dumps**: no captured
session ever opened the guild-city or battlekeep UI.

| dir | opcode | method | body |
|---|---|---|---|
| c2s | CSPlayerAgent `0x42` | `RequestBattleKeepInformation` | `[list of u32 areaId]` |
| c2s | CSPlayerAgent `0x45` | `RequestGuildCitySpot` | `[ID32 character][ID32 guild][ID32 playfieldModel]` |
| c2s | GameCharAgent `0x14` | `GuildBookedCity` | `[u32 subgroup][u32 modelInstance][str cityName][u32 cityNameId][list of ID32 members]` |
| c2s | GameCharAgent `0x15` | `GuildCityOwnershipChange` | `[list of ID32 members][u8 newValue]` |
| s2c | CSPlayerInterface `0x6B` | `DispatchGuildCityBooked` | `[u32 i_nCitySpotId][u32 i_nCityPlayfieldModelInstance][u32 i_nCitySubgroup]` |
| s2c | CSPlayerInterface `0x6C` | `DispatchGuildCityBookingFailed` | `[u32 i_nErrorCode]` |
| s2c | CSPlayerInterface `0x6F` | `DispatchGuildIsMassivePvPCityOwner` | `[u8 i_bNewValue]` |
| s2c | CSPlayerInterface `0x70` | `DispatchGuildIsMassivePvPCityAttacker` | `[u8 i_bNewValue]` |
| s2c | CSPlayerInterface `0x72` | `DispatchRequestBattleKeepInformation` | `areaIDVec` |
| s2c | CSPlayerInterface `0xA9` | `DispatchBattleKeepInfoRequestReturnPhase1And2` | keep list, below |
| s2c | CSPlayerInterface `0xAA` | `DispatchBattleKeepInfoRequestReturnPhase3And4` | keep list, below |

The two `BattleKeepInfoRequestReturn` replies carry the **same 7-word keep record**, and phases 1+2 add a
nested per-keep list. Both list frames cap at 30000 elements (`count - 1 < 0x7531`), which is the client's own
sanity bound rather than a real limit.

```
Phase3And4:  [u32 (n + 1) * 0x3F1]  { n × [u32 × 7] }              # 28-byte stride, FUN_0134f280
Phase1And2:  [u32 (n + 1) * 0x3F1]  { n × keep }                   # FUN_0134f1f0 -> FUN_013b1680
keep      =  [u32]  [u32 (m + 1) * 0x3F1] { m × [u32 × 4] }  [u32 × 6]
```

The two readers scatter the seven words into the same struct in the same order (slots 0, 1, 2, 5, 6, 4, 3), so
the record is one type read two ways — a per-phase view of one keep, not two record types. Field meanings are
not recoverable: these readers take the words straight into the struct with no per-field bail-out string, so
unlike the scalar dispatch bodies above there is nothing in the binary that names them, and no capture to
correlate against. `[V0: the battlekeep info record's seven columns]`

## ⭐ The minigame ROSTER, from the LDB

The message catalogue above was recovered from the client's method tables; what it could not say is *which
minigames exist and what each one's objective is*. The LDB says both, in two small enumerable catalogs:

| key | LDB 800 — the minigame | LDB 802 — its objective |
|---:|---|---|
| 1 | Annihilation | Annihilate Opposing Team |
| 2 | Capture the Skull | Conquest |
| 3 | Bossonian Marches | Drunken Brawling |
| 4 | Cragsfell | Conquest |
| 5 | Colosseum | Capture The Skull |
| 6 | Drunken Brawling | — |
| 8 | — | Training Annihilate Opposing Team |
| 9 | — | Training Capture The Skull |
| 13 | Annihilation | Annihilate Opposing Team |

⚠️ **The two are separate id spaces, not a pair** — key 2 is *"Capture the Skull"* in 800 and *"Conquest"* in
802 — so 800 reads as the minigame/arena NAME and 802 as the RULE SET, each keyed by its own index. Do not join
them on the key. The `Training …` variants in 802 are their own entries, which is what a practice queue looks
like.

⭐ **And one map's whole ruleset is written out.** LDB 10015 carries per-arena rules text; `500200` is Totem
Torrent (playfield 5002, one of the five RDB 1000512 minigame maps):

> Team annihilation: The goal of the game is kill all members of the opposing force. Join one of two teams, red
> or blue. Players will resurrect **as long as their totem is not destroyed**. On either side of the map are two
> **control points** — capture these for your team by standing near them; once captured, these **increase your
> totem's defense**. There is a **heal-zone** in the middle of the play area that heals all characters. If both
> teams are alive when the time is up, **the team with the most kills wins**.

So Totem Torrent is a respawn-anchor mode with capture points that harden the anchor, plus a tiebreak on kills —
a complete, implementable ruleset with no invention. `10015/5201` likewise pins **Blood Ravine** to *Capture the
Skull*. The other three 1000512 maps have no 10015 entry.

## The per-minigame CAREER is a stat block, and we already carry it

Stat ids `0x290..0x29F` are one contiguous PvP/minigame career, and the client names every member:

| id | stat | | id | stat |
|---|---|---|---|---|
| `0x290` | `PvPTotalKills` | | `0x298` | `NPCKills` |
| `0x291` | `PvPFinishingBlows` | | `0x299` | `NPCDeaths` |
| `0x292` | `PvPTotalDeaths` | | `0x29A`/`0x29B` | `PvPAnnihilationMatchesWon` / `…Lost` |
| `0x293`/`0x294` | `PvPMatchesWon` / `…Lost` | | `0x29C`/`0x29D` | `PvPCTFMatchesWon` / `…Lost` (Capture the Skull) |
| `0x295` | `PvPMatchesPlayed` | | `0x29E`/`0x29F` | `PvPSiegeMatchesWon` / `…Lost` |
| `0x296`/`0x297` | `PvPFlagsCaptured` / `PvPFlagsReturned` | | | |

⭐ Every one of the win/loss pairs is **already in the player spawn scaffold's word list**
(`SpawnPreIir.WordStatIds`), so the career counters ride the wire on every player spawn today — at zero, because
nothing moves them. The two immediately after the block, `0x2A0 GuildWarID` and `0x2A1 DefendingGuildWarID`, are
the siege side's pairing (#147).

⚠️ `NPCKills`/`NPCDeaths` are the only two ids of the run that retail does **not** populate, and they are
deliberately not sent — see `Character`'s remark and `SnapshotStatSheet`.

## What blocks implementing these

Not the wire. All three message sets are enumerated and the scalar bodies are exact. What is missing is
**content and systems**: the minigame playfields and their sensor-volume capture zones, the battlekeep areas
behind `areaId`, the scoring rules, and — for guild cities — a building/object-persistence model and the
Architect tradeskill that feeds it. Open work tracked in Gitea #146 (minigames / Massive PvP) and #147 (sieges /
battlekeeps / guild cities); #498 covers the city plots the captures DO carry, as destructibles.
