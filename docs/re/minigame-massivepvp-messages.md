# Minigames + Massive PvP: the message surface (Gitea #146, #147)

AoC's instanced PvP **minigames**, the open-world **Massive PvP** (Border Kingdom battlekeep) scoring layer, and
the **guild city / siege** tier that shares its area-id space. None of them is implemented. This page is the
wire surface for all three: what the 120 capture dumps actually contain, and what the client's own code says
about everything they do not.

Two sources, kept apart on purpose:

* **Captured** — seen in `<data-repo>/Captures/*.tshark.json`. The login polls, **plus the whole minigame QUEUE**:
  a 2026-08-20 capture (`…_annotated_group-finder_pvp-matchmaking`) signs up for four different minigames, brings
  the board to life and dequeues again, with the player annotating each step as they take it. Every one is
  handled today.
* **Client-recovered** — the rest. Method names and parameter types come from
  [`aoc-client-rpc-methods.json`](aoc-client-rpc-methods.json); field widths come from each
  `<Class>InterfaceDispatch_t::<Method>` reader in `AgeOfConan.exe`, which bails out per field with a string
  naming the field it was about to read (`tools/ghidra/DumpStringXrefs.java`, `STR=Dispatch_t::DispatchMinigame`
  and friends). The stream primitives are the shared ones: `FUN_0048f900` u32 BE, `FUN_0048f840` string
  (`[u16 len][latin1]`), `FUN_0048f6d0` a one-byte bool, `FUN_0048f720` u64.

## What the captures contain

Swept over all 137 dumps, both directions, for every opcode in the two systems' ranges:

| dir | class | method | n | body |
|---|---|---|---|---|
| c2s | CSPlayerAgent `0x52` | `PvPMatchMakingPutIntoMatchMaking` | 8 | `[u32 map][u32 type][u8 bringTeam][u32 queueType]` |
| c2s | CSPlayerAgent `0x53` | `PvPMatchMakingRemoveFromMatchMaking` | 4 | empty |
| c2s | CSPlayerAgent `0x54` | `PvPMatchMakingRequestInfo` | 59 | `[u32 queueType]`, always 0 |
| c2s | CSPlayerAgent `0x55` | `PvPMatchMakingIsInMatchMaking` | 7 | empty |
| c2s | CSPlayerAgent `0x77` | `MassivePvpInfoRequest` | 150 | empty |
| c2s | GameCharAgent `0x8D` | `RequestMassivePvPScoreData` | 6 | empty |
| s2c | CSPlayerInterface `0x80` | `DispatchPvPMatchMakingYouAreInMatchMaking` | 8 | the signup echoed + two flags |
| s2c | CSPlayerInterface `0x81` | (no client-recovered name) | 4 | empty — the removal's ack |
| s2c | CSPlayerInterface `0x92` | `DispatchPvPMatchMakingInfoRequestReturn` | 59 | **the queue board, below** |
| s2c | CSPlayerInterface `0xA5` | `DispatchMassivePvpInfoRequestReturn` | 147 | the guild board, below |

**Nothing beyond the queue appears anywhere in the corpus** — no match creation, no side picking, no capture zone,
no score update. Queueing is now captured; a match forming out of the queue is not. Note also that retail answers
`PvPMatchMakingIsInMatchMaking` and `RequestMassivePvPScoreData` with **no packet at all** when there is nothing
to report — and it answers the former with silence *even in the capture where the player was queued*, which is
what says the silence is the answer rather than a gap.

## The minigame QUEUE (Gitea #882)

The four c2s messages and their two replies, walked end to end at t=1057…1150 of
`NecroL80_mage-necromancer_login_gameplay_annotated_group-finder_pvp-matchmaking` — seven signups, four removals,
43 board polls. Codec: `PvpMatchMakingSignupCodec` / `PvpMatchMakingStatusCodec` / `PvpMatchMakingInfoCodec` ·
service: `PvpMatchMakingService` · gate: `PvpMatchMakingTests`.

```
c PvPMatchMakingPutIntoMatchMaking [u32 i_nMap][u32 i_nType][u8 i_bBringTeam][u32 i_QueueType]      (13 B)
                             -> s  [u32 i_nMap][u32 i_nType][u8 i_ShowFifo][u8 i_AsTeamMember][u32 i_queueType]   (14 B)
c PvPMatchMakingRemoveFromMatchMaking  (empty)  -> s the empty 0x2081, ~0.3 s later, 1:1 with all four removals
c PvPMatchMakingRequestInfo [u32 i_QueueType]   -> s the board:

  [u32 (types + 1) * 0x3F1]
    types × [u32 minigameType] [u32 (maps + 1) * 0x3F1]
      maps × [u32 mapId] [u32 (queues + 1) * 0x3F1]
        queues × [u32 queueType] [u32 signups]
  [u32 i_nNumberOfAnyApplies]
```

⭐ **The board is LIVE state, and the capture proves it by moving.** Until this capture the message was on record
here as "captured empty" — `[0x3F1][u32 0]` in every login dump — and the server shipped that constant. It is the
EMPTY BOARD, not the message's shape: across the 43 bodies every field tracks the player's own signups second by
second. All 43 parse to the packet end under the grammar above, and the board *constructed* from a live registry
reproduces a captured body byte for byte (`Board_BuiltFromLiveSignupsIsTheCapturedBody`).

Three things fall out of watching it move:

* **A character holds exactly ONE signup.** Every `PutIntoMatchMaking` raises its own map's count by one *and*
  drops the previously chosen map's by one, in the same board — which is also why
  `PvPMatchMakingRemoveFromMatchMaking` needs no argument.
* **The registry is sticky.** A map appears the first time anyone queues for it and stays listed at 0 afterwards,
  so the board is a per-server registry seeded by activity, not a catalog — and an empty one is what a server
  nobody has queued on sends, which is exactly the four login captures.
* **`i_nNumberOfAnyApplies` is the "join first available" tally**, and the annotations name both edges: it goes
  0 → 1 on the `(map 0, type 0)` signup annotated *"selected join first avaialble instead"* and back on the
  removal annotated *"disabled join first avaialbl"*. Those signups are counted there and are deliberately **not**
  listed as a type-0 group.

⭐ **Both leading words of a signup resolve outside this message.** `i_nMap` is a PLAYFIELD id — the four captured
maps are 5006 / 5201 / 5012 / 5005, which are `The Call of Jhebbal Sag` / `Blood Ravine` / `Lost Temple` /
`Hallowed Vaults` in `Maps`, a table harvested from a file that knows nothing about this message. And `i_nType` is
the **LDB 802 objective id**: the player annotated *"signed up for conquest pvp"* over the type-4 signup and
*"signed up for capture the skull"* over the type-5 ones, and LDB 802 key 4 is `Conquest`, key 5 is
`Capture The Skull`. Same corroboration pattern as the Massive-PvP board's `areaId`.

⚠️ `0x7E2` here is the ONE-ELEMENT list frame, not a constant: the innermost frame is `0x7E2` in all 43 bodies
because every captured signup carries `i_QueueType` 0, so each map has exactly one queue. The innermost KEY is 0
throughout for the same reason, so reading it as `queueType` is inference from the two messages that carry that
field rather than measurement. `[V0: the innermost map key of the matchmaking board]`

⚠️ `i_bBringTeam` is 0 and `i_AsTeamMember` is 0 in every captured frame — the session was solo, so the corpus
shows only one side of each. `[V0: i_AsTeamMember with a team]`

⛔ **Matchmaking PROPER is still uncaptured.** Nothing pops out of the queue in this capture: no
`WantsToStart`, no `YouJoinedMatch`, no minigame instance. That needs enough players to fill one.

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
| s2c | `0x76` | `DispatchPvPMatchMakingPlayerJoined` | ⚠️ `[cCharacter i_cPlayer][u32 i_nSide]` — the WHOLE member record (its reader `FUN_01346780` calls the roster's own `FUN_005574d0`), not the ID32 this table first carved; a 12-byte form is discarded as stream-bad (found live, 2026-08-26) |
| s2c | `0x77` | `DispatchPvPMatchMakingPlayerChangedSide` | `[ID32 i_cPlayer][u32 i_nNewSide]` (reader `FUN_01346920`: three scalar words) |
| s2c | `0x78` | `DispatchPvPMatchMakingPlayerLeft` | ⚠️ `[cCharacter i_cCharacterID]` — the whole record (`FUN_01346aa0`) |
| s2c | `0x79` | `DispatchPvPMatchMakingPlayerKicked` | ⚠️ `[cCharacter i_cCharacterID][str i_cMessage]` — the whole record (`FUN_01346be0`) |
| s2c | `0x7A` | `DispatchPvPMatchMakingErrorCode` | `[u32 i_nErrorCode]` |
| s2c | `0x7F` | `DispatchPvPMatchMakingStartClicked` | `[u32 i_nTeleportInSeconds]` |
| s2c | `0x80` | `DispatchPvPMatchMakingYouAreInMatchMaking` | `[u32 i_nMap][u32 i_nType][u8 i_ShowFifo][u8 i_AsTeamMember][u32 i_queueType]` |
| s2c | `0x83` | `DispatchPvPMatchMakingMatchSay` | `[str i_cFrom][str i_cMessage][u8 i_bFifo]` |
| s2c | `0x84` | `DispatchPvPMatchMakingCollectingMorePlayers` | `[u32 i_nSecondsUntilForcedStart]` |
| s2c | `0x85` | `DispatchPvPMatchMakingNewMatchLeader` | `[ID32 i_cNewMatchLeader]` (reader `FUN_01347b50`: two words — an `i_c` name does NOT decide record-vs-ID32; check each reader) |
| s2c | `0x86` | `…TeamLeaderSignedUpForMatchMaking` | `[u32 i_nMap][u32 i_nType]` |
| s2c | `0x8B` | `…CantChangeSideYouHaveBeenLockedToSide` | `[u32 i_nSide]` |
| s2c | `0x8C` | `…CharacterNowLockedToSide` | `[ID32 i_cCharID][u32 i_nSide]` |
| s2c | `0x8D` | `…CharacterNoLongerLockedToSide` | `[ID32 i_cCharID]` |
| s2c | `0x92` | `DispatchPvPMatchMakingInfoRequestReturn` | `[list i_cList][u32 i_nNumberOfAnyApplies]` — captured empty |
| s2c | `0xA6` | `DispatchMassivePvPBattleQueueFeedback` | `[u32 i_nFeedbackType][u32 i_nFeedbackValue]` — the 18 types and their texts are tabled in `docs/subsystems/guild-cities-and-sieges.md` |
| s2c | `0xA7` | `DispatchMassivePvpStartCountDown` | `[u32 i_cEndTime]` |
| s2c | `0xA8` | `DispatchMassivePvpBattleQueueStatusUpdate` | `[u32 i_nArea][u8 i_inQueue]` |

`const ID32&` is two words (`[u32 class][u32 instance]`), per
[`../protocol/02-rpc.md`](../protocol/02-rpc.md).

### ⭐ `i_cMatch` — the whole-match payload, recovered field-by-field from the client

The three messages carrying a whole match — `YouJoinedMatch` / `MatchInfo` (`i_cMatch`) and `SearchResult`
(`i_cMatches`) — still have **zero captured instances**, because no captured queue ever filled. Their layout is
nevertheless pinned, because the client contains all three halves of it and they agree:

| what | function | what it establishes |
|---|---|---|
| the s2c **reader** | `FUN_01346ef0` (`YouJoinedMatch`), `FUN_013470d0` (`MatchInfo`) | field order + widths off the stream |
| the c2s **writer** | `FUN_00558880`, reached from `CSPlayerAgentProxy_t::PvPMatchMakingCreateMatch` | the same order, written — so the split is not a reading of one side only |
| the local **copy** | `FUN_00547070`, which stores the joined match on `CSClientIncoming_c` | the struct's own size and where its non-wire tail starts |

```
i_cMatch  =  [ListFrame(n)] { n × cCharacter }      # the member roster - FUN_0055bdb0, 0x7c stride
             [ListFrame(m)] { m × u32 }             # an id list - FUN_004d4ef0
             PvPMatchRules_c                        # below
             [u32 matchId]                          # tail0
             if (rules.FlagB == 0)  [ID32 leader]   # two words

PvPMatchRules_c  =  [u32 A]  [u32 B]  [u32 C]  [u32 D]  [u32 E]     # struct +0x00 .. +0x10
                    [u16 len][latin1]                               # +0x14, a std::string
                    [u32 F]  [u32 G]  [u32 H]                       # +0x30 +0x34 +0x38
                    [u8 FlagA] [u8 FlagB]                           # +0x3C +0x3D
                    [u32 I]                                         # +0x40
```

⭐ **The roster entries are `cCharacter` — the SAME record the team subsystem carries.** `FUN_0055bdb0` walks a
0x7c-stride vector calling `FUN_005574d0`, which is the reader
[`TeamMemberRecordCodec`](../../Faolan.Core.Network/Codecs/Team/TeamMemberRecordCodec.cs) was decoded against
(#669). So the match's player list needs no new record type; it is a `TeamMemberRecord` vector.

Three independent checks that the split above is right rather than a plausible carve:

* The element is **0x44 wire bytes**, and the reader's next field lands at struct `+0x64` = `0x20 + 0x44` in the
  `SearchResult` list reader (`FUN_0134f360`), whose stride is **0x70** — exactly
  `0x20` (roster + id list) + `0x44` (element) + three trailing words.
* `FUN_00547070` copies precisely those three trailing words (`+0x64/+0x68/+0x6c`) into the stored match, so the
  0x70 struct and the wire record are the same shape.
* The conditional pair is gated on the byte at element `+0x3D` — i.e. `FlagB` — in **both** the single-match
  reader and the list reader.

⭐ **Eight of the twelve fields are NAMED from the client — not from a capture (there is still none), but from the
code that DISPLAYS a match.** The reader takes the words straight into the struct with no per-field bail-out
string, so a search for names on the wire path finds nothing; the names are on the *consumer* side, in three
places that agree:

| field | struct | name | how it is pinned |
|---|---|---|---|
| `A` | `+0x00` | **match type** (the LDB 802 objective id, same space as `PutIntoMatchMaking`'s `i_nType`) | the Flash API `PvPMinigame.GetMatchType` (thunk `00b65600`) returns `PvPMatchMaking_c+0x98`, which is where `FUN_00547070` stores the joined match's element (`+0x98 = +0x78 roster + 0x20`); the score view `FUN_017393e0` branches on `+0x98 == 4` (Conquest) to make one side's kills reduce the other's score; the community-server debug view labels the same word **Ruleset** |
| `B` | `+0x04` | **map** (playfield id) | the debug view's **Map** column reads record `+0x24`; the debug default `5807` has the shape |
| `F` | `+0x30` | **time limit, seconds** | `GetTimeLeft` (`00b658b0`) and `PvPMinigameEndsInSeconds` (`FUN_01716cc0`) both compute `(matchStart@+0x1b8 − now) + this` off `+0xc8`; the match-status view formats it `%02d:%02d:%02d`; debug default `900` = 15 min |
| `G` | `+0x34` | **score limit** | `GetScoreLimit` (`00b65950`) returns `+0xcc` |
| `I` | `+0x40` | **queue type** | the debug view's **QueueType** column reads record `+0x60` |
| `tail0` | `+0x64` | **match id** | the debug view's **ID** column and its `"Match %d created."` line both print record `+0x64`; `PvPMatchMaking_c` compares its `+0xdc` against the empty match's id to know whether one is joined (`FUN_00545cc0`) |
| `FlagB` | `+0x3D` | *behaviour, not a name*: when **0**, the client announces the match with fifo text (`PvPMatchMakingYouCreatedMatch` / `YouJoinedMatch` in `FUN_00547070`, `PvPMatchMakingPlayerJoined` in `FUN_005471b0`) and the record carries the two extra tail words; when 1 it stays silent | the same byte gates both, in the wire reader and in the copier |
| roster count | — | **Players** | the debug view's second column is `(roster.end − roster.begin) / 0x7c` |

Sources, so the next reader does not re-find them: the **community-server debug view**
`Views/CommunityServerDebugGUI/pvpmatchmaking.xml` (`FUN_016b6020`; its match-list row filler `FUN_016b6bd0`
takes the 0x70 record and labels the columns ID / Players / Map / Ruleset / QueueType / Server), the **Flash API**
`com.GameInterface.PvPMinigame` (`FUN_00b64c30` registers `GetMatchType` / `GetSideScore` / `GetTotalCharacterScore`
/ `InMatchPlayfield` / `GetMinigameStatus` / `GetWinningSide` / `GetCaptureZoneName` / `GetTimeLeft` /
`GetToGameTimeLeft` / `GetScoreLimit` / `PlantCTFFlags`, each a one-line thunk off the `PvPMatchMaking_c` singleton
at `DAT_0297d358`), and the **match views** under `Views/TeamGUI/` (`PVPMatchStatusView.xml`,
`PvPMatchBuildupView.xml` with its `MatchTypeView` / `TimeLimitView` / `ScoreLimitView`). `tools/ghidra/DecompileXrefsTo.java`
(every function that touches the singleton) and `DecompileRange.java` (a whole GUI band) are the two scripts the
hunt needed.

⚠️ **The other four words and the string are still only INFERRED**, and the reason is a negative worth recording:
the one view that would have named them — `Views/TeamGUI/PvPSearchResultView.xml` (`PvPSearchResultView_c`,
`FUN_01733610`) — declares ten columns, **Leader / Match / Region / NumTeams / TeamSize / BCoins / Tier / Pwd /
Time / Applied**, and its row class `PvPSearchResultItem_c` has typed setters for them (`NumTeams` = `"%d(%d)"`,
`TeamSize` = `"%d-%d"` — a RANGE — `BCoins` = one integer, `Pwd` = a **bool** that opens the `PasswordPrompt`
before `JoinMatch`, `Time` = seconds as `mm:ss`, `Applied` = a 64-bit `time_t`). **The whole search feature is
dead in this build, on both ends of the wire** (Gitea #894, where the scans are listed):

* **No row is ever built.** Raw scans over every initialized byte of the image (`tools/ghidra/ScanRelCalls.java`
  `ALL=1`, E8 + E9; `tools/ghidra/FindAddrOperand.java`, absolute addresses) find no call, jump, immediate or
  pointer to the item's constructor `FUN_0174df40` or to any setter, and the item's vtable `020b261c` is referenced
  by that constructor alone — so there is no inlined copy either. The consumers are all present: the Apply button
  handler `FUN_01734020` dynamic_casts the selected row to `PvPSearchResultItem_c` (`FUN_017345b0`) and, when the
  row's `+0xac` (`Pwd`) byte is set, opens the password prompt. They simply never see an item.
* **The page is never opened.** The view is page 1 of the four-page PvP window whose page switch is
  `FUN_016652c0` (0 `PVPMatchmaking.xml`, 1 this view, 2 `PvPMatchBuildupView.xml`, 3 `PVPMatchStatusView.xml`).
  Pages 0, 2 and 3 are built by the window's live init `FUN_01664da0` (called from `FUN_0158cce0`) and by the
  slots it connects (`FUN_01665500`, `FUN_016656a0`, `FUN_01665a00`); page 1 is built only by `FUN_016655d0` and
  by the generic switch, and nothing in the image calls, jumps to or takes the address of either. ⚠️ A zero-caller
  count is not the proof on its own — two LIVE pages also have zero-caller `Show*` methods (`FUN_01665430`,
  `FUN_016658b0`); the proof is that page 1 has no live route at all.
* **The client cannot even ask.** `CSPlayerAgentProxy_t::PvPMatchMakingSearchForMatches` (`FUN_013647b0`, method
  `0x4f`) is virtual — slot 82 (`+0x148`) of the proxy vtable at `02072334` — so no direct-call scan can see its
  callers. Of every function in the image that invokes a `+0x148` slot (33, plus three sites outside any defined
  function), the only one acting on that proxy is the `GameClient` wrapper `FUN_00546180` (`s_pcClient` →
  `+0x288` → `FUN_0133c670`), and that wrapper has no caller of any kind. No client of this build ever sends the
  request — which is why `PvPMatchMakingSearchForMatches` appears in no capture, and why a `SearchResult` body
  cannot be captured by playing.
* **Nothing outside the exe fills it.** The GUI ships as XML; the client's `Data/Gui/PythonModules` directory is
  EMPTY; the view's XML declares only the `MultiColListView` and its two buttons, and none of the 41 shipped SWFs
  belongs to it. The embedded Lua (luabind 0.9.1, Funcom fork) registers exactly twelve classes — Vector,
  Quaternion, ID32, Engine, Playfield, Dynel, Character, Item, Spell, Camera, Callback and a resource-dependency
  namespace — none of them PvP; the Flash bridge (`com.GameInterface.PvPMinigame`, `FUN_00b64c30` / `FUN_00b64930`)
  exposes the minigame getters and signals and no search entry. The DX10 executable is the same `branch_v5.01.0`
  build.

The linker kept the class because the refresh slot the view connects, `FUN_017341d0` — itself a vestigial loop that
dynamic_casts each row and does nothing with the result — references its RTTI. So there is no filler mapping record
offsets onto those columns, and the remaining route to `C`/`D`/`E`/`H` is the element's OTHER carriers,
`YouJoinedMatch` / `MatchInfo`: a capture in which a queue fills. What the column TYPES and the debug command's
defaults support, without a consumer to prove it:

| field | struct | reading | evidence |
|---|---|---|---|
| `C` | `+0x08` | number of teams (`2`) | `NumTeams` is a column; the debug default is `2`; sides are only ever 1 or 2 (`"Invalid match making side (%d)"`) |
| `D`, `E` | `+0x0C`, `+0x10` | team size min / max (`3`–`6`) | `TeamSize` is rendered `"%d-%d"`, a range; the debug defaults are `3` and `6` |
| the string, `FlagA` | `+0x14`, `+0x3C` | password, has-password | the debug command sets both together from its one optional string argument; `Pwd` is a bool column and gates the password prompt |
| `H` | `+0x38` | unnamed | never set by the debug command, read by nothing found |
| ~~`tail1`/`tail2`~~ | `+0x68`, `+0x6C` | **the match LEADER's ID32** — named 2026-08-26 by the copier's fifo chooser (`00547117..2f`): the stored pair is compared against the LOCAL PLAYER's ID32, a match raising `PvPMatchMakingYouCreatedMatch` and a mismatch `PvPMatchMakingYouJoinedMatch`. Not the `Applied` time this row first guessed. | the chooser reads both words as one identity and the two fifo tokens are its two outcomes |

⚠️ **The chooser's "local player" is the client's OWN CHARACTER world ID32, so two clients on ONE account both read "You created a match".** The identity is `n3EngineClient` singleton slot `+0x28` (`FUN_00596160` = `*(engineClient+0xb0)+0x1b8`, the local player dynel's ID32) — per-character. The server broadcasts ONE `YouJoinedMatch` naming a single leader (byte-identical to every member), so exactly ONE client should match; two clients dual-boxed under one account resolve the same local identity and both show "created". A same-account CLIENT artifact, not a wire fault — the leader we send is correct and singular (verified in-client 2026-08-26; confirm with a second account).

`[V0: the PvPMatchRules_c C/D/E/H columns]`

⛔ **The search filter is NOT this element.** `PvPMatchMakingSearchForMatches(const PvPMatchMakingSearchFilter_c&)`
has a writer of its own — `FUN_00558f50`, reached from `FUN_013647b0` — and it writes just
**`[u32][u8]`**. (An earlier note here said the element reader was shared with the filter; it is not.)

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

The two `BattleKeepInfoRequestReturn` replies carry the **same keep record**, and phases 1+2 add a nested
per-keep list. Both list frames cap at 30000 elements (`count - 1 < 0x7531`), which is the client's own sanity
bound rather than a real limit.

```
Phase3And4:  [u32 (n + 1) * 0x3F1]  { n × [keepId][ID32 owner][ID32 attacker][nextPhaseUnix][phase] }   # 28 B, FUN_0134f280
Phase1And2:  [u32 (n + 1) * 0x3F1]  { n × keep }                                                       # FUN_0134f1f0 -> FUN_013b1680
keep      =  [keepId]  [u32 (m + 1) * 0x3F1] { m × [ID32 guild][kills][total] }  [ID32 a][ID32 owner][nextPhaseUnix][phase]
```

⭐ The columns are named from the keep window's filler (`FUN_01315130`, `MassivePvPBattleKeepView.xml`), which
reads the struct both readers fill — the whole derivation, the LDB 10032 phase names (War Declaration / Skirmish /
Silence before the storm / War / Aftermath) and the attacker-ranking columns (kills, total, minigame = total −
kills) are in [`../subsystems/guild-cities-and-sieges.md`](../subsystems/guild-cities-and-sieges.md). Phase3And4
is captured (four replies, `BattleKeepInfoCodec`); Phase1And2 is not — the frame once taken for its single sample
is a GameCharInterface loot reply wearing the same lossy `0x20A9` wire rendering.

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

## What is built, and what still blocks the rest

The **custom-match lifecycle is built** (2026-08-26, uncaptured end to end — it carries #146's needs-testing):
`PvpMatchRulesCodec` / `PvpMatchCodec` build the element and the `i_cMatch` payload above, `PvpMatchService`
runs create/join/side/say/kick/ban/force-side/start, and a started match owns ONE playfield instance both
sides land in (`WorldState.ResolveEntryInstance`, the minigame twin of #512's team instance), entered through
the ordinary change-map handoff when each member's client sends `PvPMatchMakingTeleportToMinigame`; the entry
answers with `DispatchMinigameMatchStarted` so a late zoner sees the running clock.

What still blocks the REST is content and systems, not the wire: the sensor-volume capture zones (#513) and
scoring for a playable minigame, the battlekeep areas behind `areaId`, and — for guild cities — a
building/object-persistence model and the Architect tradeskill that feeds it. Open work tracked in Gitea #146
(minigames / Massive PvP) and #147 (sieges / battlekeeps / guild cities); #498 covers the city plots the
captures DO carry, as destructibles.
