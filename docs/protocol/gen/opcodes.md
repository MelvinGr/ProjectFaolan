# Opcode catalog (generated)

> **GENERATED — do not edit.** Source: the compiled enums in
> `Faolan.Core/Enums/Opcodes.cs`, read by reflection.
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.

16 enums, 826 entries. The 32-bit `GameServer0X2000` IIR sub-opcodes are catalogued separately in [`iir-types.md`](iir-types.md).

## What is (and is not) verified here [V1]

- **Names + values** are the server's curated method catalogs — the same tables that drive the
  Wireshark dissector, recovered from the client's RPC method registration (16-bit opcode =
  `0x2000 + registration index`). This is a **V1** fact: the opcode set is complete and client-verified.
- **A named opcode does NOT mean the message BODY is decoded.** Body layouts are documented
  per-message (and gated by golden tests) elsewhere; see [`coverage.md`](coverage.md) for which have one.
- **Direction** is the RpcHeader receiver's role: `*Agent` classes receive client→server calls (C2S),
  `*Interface` classes receive server→client dispatches (S2C). ChatServer chat carries no RpcHeader.
- **Class id** = BKDR-31 hash of the client class name, recomputed + asserted here.

## CSPlayerAgentOpcodes

RPC class **CSPlayerAgent** (id `0x6DD55614`) · C2S (client→server call)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `Authenticate` |  |
| `0x2001` | `CharacterAuthenticated` |  |
| `0x2002` | `CharacterDisconnected` |  |
| `0x2003` | `TeamInvite` |  |
| `0x2004` | `TeamInviteByName` |  |
| `0x2005` | `TeamApply` |  |
| `0x2006` | `TeamSetLootOption` |  |
| `0x2007` | `TeamSetMasterLooter` |  |
| `0x2008` | `AcceptTeamInvite` |  |
| `0x2009` | `DeclineTeamInvite` |  |
| `0x200A` | `CharacterSearch` |  |
| `0x200B` | `SearchForTeam` |  |
| `0x200C` | `SetNewTeamLeader` |  |
| `0x200D` | `SummonCharacters` |  |
| `0x200E` | `AcceptSummon` |  |
| `0x200F` | `DeclineSummon` |  |
| `0x2010` | `GetExtendedCharacterInfo` |  |
| `0x2011` | `GetExtendedCharacterInfoByName` |  |
| `0x2012` | `SetLookingForTeamStatus` |  |
| `0x2013` | `SetLookingForMemberStatus` |  |
| `0x2014` | `GetLookingForTeamStatus` |  |
| `0x2015` | `GetLookingForMemberStatus` |  |
| `0x2016` | `SetCommunityServerOptions` |  |
| `0x2017` | `StartVoteKick` |  |
| `0x2018` | `VoteKickAnswer` |  |
| `0x2019` | `StartVoteRetreat` | observed body [u32 0xC350][u32 clientInstance] at login - semantics unconfirmed |
| `0x201A` | `VoteRetreatAccept` |  |
| `0x201B` | `VoteRetreatReject` |  |
| `0x201C` | `AcceptGuildInvitation` |  |
| `0x201D` | `DeclineGuildInvitation` |  |
| `0x201E` | `ClientRequestGuildInfo` |  |
| `0x201F` | `ClientRequestGuildName` |  |
| `0x2020` | `ClientRequestCharIDByName` |  |
| `0x2021` | `CreateGuild` |  |
| `0x2022` | `GuildInviteByName` |  |
| `0x2023` | `GuildInvite` |  |
| `0x2024` | `RemoveGuildInvitationCooldown` |  |
| `0x2025` | `GuildPromote` |  |
| `0x2026` | `GuildDemote` |  |
| `0x2027` | `GuildKick` |  |
| `0x2028` | `GuildLeave` |  |
| `0x2029` | `GuildTax` |  |
| `0x202A` | `GuildGovernmentForm` |  |
| `0x202B` | `GuildRecruitmentMode` |  |
| `0x202C` | `GuildName` |  |
| `0x202D` | `GuildDescription` |  |
| `0x202E` | `GuildMessageOfTheDay` |  |
| `0x202F` | `GuildRequestMemberList` |  |
| `0x2030` | `GuildApplication` |  |
| `0x2031` | `GuildRejectApplication` |  |
| `0x2032` | `SetLookingForGuild` |  |
| `0x2033` | `GuildMergeRequest` |  |
| `0x2034` | `GuildMergeReply` |  |
| `0x2035` | `GuildWithdrawMoney` |  |
| `0x2036` | `GetPvpPointsInfo` |  |
| `0x2037` | `AddPvpPoints` |  |
| `0x2038` | `RemovePvpPoints` |  |
| `0x2039` | `ClearPvpPoints` |  |
| `0x203A` | `GetGuildRenownInfo` |  |
| `0x203B` | `AddGuildRenown` |  |
| `0x203C` | `RemoveGuildRenown` |  |
| `0x203D` | `SetGuildRenown` |  |
| `0x203E` | `CalculateRankings` |  |
| `0x203F` | `UnlockGuildRenown` |  |
| `0x2040` | `RequestRenownHistory` |  |
| `0x2041` | `RequestRenownRankings` |  |
| `0x2042` | `RequestBattleKeepInformation` |  |
| `0x2043` | `SearchForGuilds` |  |
| `0x2044` | `GMGuildName` |  |
| `0x2045` | `RequestGuildCitySpot` |  |
| `0x2046` | `ApplyForMassivePVPBattleQueue` |  |
| `0x2047` | `WithdrawFromMassivePVPBattleQueue` |  |
| `0x2048` | `MassivePvPBattleQueueGMCommand` |  |
| `0x2049` | `MassivePvpBattleQueueStatusRequest` |  |
| `0x204A` | `PvPMatchMakingCreateMatch` |  |
| `0x204B` | `PvPMatchMakingJoinMatch` |  |
| `0x204C` | `PvPMatchMakingSetSide` |  |
| `0x204D` | `PvPMatchMakingMatchSay` |  |
| `0x204E` | `PvPMatchMakingLeaveMatch` |  |
| `0x204F` | `PvPMatchMakingSearchForMatches` |  |
| `0x2050` | `PvPMatchMakingGetMatchInfo` |  |
| `0x2051` | `PvPMatchMakingTeleportToMinigame` |  |
| `0x2052` | `PvPMatchMakingPutIntoMatchMaking` |  |
| `0x2053` | `PvPMatchMakingRemoveFromMatchMaking` |  |
| `0x2054` | `PvPMatchMakingRequestInfo` |  |
| `0x2055` | `PvPMatchMakingIsInMatchMaking` |  |
| `0x2056` | `PvPMatchMakingKickPlayer` |  |
| `0x2057` | `PvPMatchMakingBanPlayer` |  |
| `0x2058` | `PvPMatchMakingMakePlayerMatchOwner` |  |
| `0x2059` | `PvPMatchMakingForceSide` |  |
| `0x205A` | `PvPMatchMakingStartMatch` |  |
| `0x205B` | `DebugPvPMatchMakingShowDebugInfo` |  |
| `0x205C` | `DebugPvPMatchMakingStopAllMatches` |  |
| `0x205D` | `DebugPvPMatchMakingStopCurrentMatch` |  |
| `0x205E` | `DebugPvPMatchMakingSetNewTimeLimit` |  |
| `0x205F` | `DebugPvPMatchMakingRestart` |  |
| `0x2060` | `DebugPvPMatchForcePlayerIntoCurrentMatch` |  |
| `0x2061` | `DebugPvPMatchMakingDoMatchMaking` |  |
| `0x2062` | `DebugPvPMatchForceStartMatch` |  |
| `0x2063` | `DebugPvPMatchForceEnterAll` |  |
| `0x2064` | `DebugCommunityServerModuleStart` |  |
| `0x2065` | `DebugCommunityServerModuleStop` |  |
| `0x2066` | `SetAsyncEnabled` |  |
| `0x2067` | `SetNumWorkerThreads` |  |
| `0x2068` | `RaidCreate` |  |
| `0x2069` | `RaidLeave` |  |
| `0x206A` | `RaidInviteTeam` |  |
| `0x206B` | `RaidInviteTeamAccept` |  |
| `0x206C` | `RaidInviteTeamDecline` |  |
| `0x206D` | `RaidKickTeam` |  |
| `0x206E` | `RaidKickCharacter` |  |
| `0x206F` | `RaidMoveTeamMember` |  |
| `0x2070` | `RaidSwapCharacters` |  |
| `0x2071` | `RaidInviteCharacter` |  |
| `0x2072` | `RaidInviteCharacterByName` |  |
| `0x2073` | `RaidAcceptInvite` |  |
| `0x2074` | `RaidDeclineInvite` |  |
| `0x2075` | `RaidSetLootOption` |  |
| `0x2076` | `RaidSetMasterLooter` |  |
| `0x2077` | `MassivePvpInfoRequest` |  |
| `0x2078` | `SendTeamInfo` |  |
| `0x2079` | `GMRequestGuildInfoFromChar` |  |
| `0x207A` | `QueueGroupFinder` |  |
| `0x207B` | `SelectGroupFinderRoles` |  |
| `0x207C` | `LeaveGroupFinder` |  |
| `0x207D` | `ReadyGroupFinder` |  |
| `0x207E` | `RequestRaidQueueInfo` |  |
| `0x207F` | `UpdateDefaultRaid` |  |
| `0x2080` | `SetFeaturedRaid` |  |
| `0x2081` | `RequestFeaturedRaidNeededRole` |  |
| `0x2082` | `RequestNeededRole` |  |
| `0x2083` | `RequestRotatingActivityChildInfo` |  |
| `0x2084` | `GetWorldStat` |  |
| `0x2085` | `RequestItemSalesBalance` |  |
| `0x2086` | `RequestItemSalesCoupons` |  |
| `0x20C5` | `EchoGroupFinderRemoved` | s2c DispatchGroupFinderRemoved (args: reason, silent) |
| `0x20D0` | `EchoGroupFinderUpdateFeaturedActivities` | s2c DispatchGroupFinderUpdateFeaturedActivities (args: activities list) |

## CSPlayerInterfaceOpcodes

RPC class **CSPlayerInterface** (id `0xBBD560C8`) · S2C (server→client dispatch)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `DispatchAuthenticateAck` |  |
| `0x2001` | `DispatchTeamInvite` |  |
| `0x2002` | `DispatchTargetIsBusy` |  |
| `0x2003` | `DispatchTargetIsCriminal` |  |
| `0x2004` | `DispatchTeamCharacterApplied` |  |
| `0x2005` | `DispatchYouAlreadySentRequest` |  |
| `0x2006` | `DispatchTargetIsAlreadyInTeam` |  |
| `0x2007` | `DispatchTeamInvitationTimedOut` |  |
| `0x2008` | `DispatchTargetDeclinedTeamInvite` |  |
| `0x2009` | `DispatchYouAreNoLongerInvited` |  |
| `0x200A` | `DispatchYouInvitedToTeam` |  |
| `0x200B` | `DispatchInviteWithInviterFactionPoints` |  |
| `0x200C` | `DispatchExtendedCharacterInfo` |  |
| `0x200D` | `DispatchExtendedCharacterInfoNotFound` |  |
| `0x200E` | `DispatchYouJoinedTeam` |  |
| `0x200F` | `DispatchTeamSearchResult` |  |
| `0x2010` | `DispatchCharacterSearchResult` |  |
| `0x2011` | `DispatchCharacterNameSearchResult` |  |
| `0x2012` | `DispatchTeamLeave` |  |
| `0x2013` | `DispatchTeamJoin` |  |
| `0x2014` | `DispatchTeamMemberUpdate` |  |
| `0x2015` | `DispatchTeamDisband` |  |
| `0x2016` | `DispatchSetNewTeamLeader` |  |
| `0x2017` | `DispatchTeamLootOptionChanged` |  |
| `0x2018` | `DispatchTeamMasterLooterChanged` |  |
| `0x2019` | `DispatchTeamSummonRequest` |  |
| `0x201A` | `DispatchTeamSummonTimedOut` |  |
| `0x201B` | `DispatchYouAreNoLongerSummoned` |  |
| `0x201C` | `DispatchTeamSummonRequestDeclined` |  |
| `0x201D` | `DispatchTeamSummonRequestAccepted` |  |
| `0x201E` | `DispatchTeamSummonRequestFailedInvalidPF` |  |
| `0x201F` | `DispatchTeamSummonRequestFailedAlreadyInSameInstance` |  |
| `0x2020` | `DispatchWarningTeamSizeTooLarge` |  |
| `0x2021` | `Reserved0x2021` |  |
| `0x2022` | `DispatchYouGotKickedFromTeam` |  |
| `0x2023` | `DispatchVoteKickStarted` |  |
| `0x2024` | `DispatchVoteKickFailed` |  |
| `0x2025` | `DispatchVoteKickEnded` |  |
| `0x2026` | `DispatchCharacterLookingForTeamStatus` |  |
| `0x2027` | `DispatchTeamLookingForMemberStatus` |  |
| `0x2028` | `DispatchDeleteCanceledMail` |  |
| `0x2029` | `DispatchDeleteCanceledMailItem` |  |
| `0x202A` | `DispatchGuildInvite` |  |
| `0x202B` | `Reserved0x202B` |  |
| `0x202C` | `Reserved0x202C` |  |
| `0x202D` | `Reserved0x202D` |  |
| `0x202E` | `DispatchGuildMemberLeft` |  |
| `0x202F` | `DispatchGuildMemberJoined` |  |
| `0x2030` | `DispatchGuildMemberUpdate` |  |
| `0x2031` | `DispatchGuildInviteReplyToInviter` |  |
| `0x2032` | `DispatchGuildGMPromoteFeedback` |  |
| `0x2033` | `DispatchGuildGMDemoteFeedback` |  |
| `0x2034` | `DispatchGuildInviteNoSuchName` |  |
| `0x2035` | `Reserved0x2035` |  |
| `0x2036` | `DispatchGuildPvPPointInfo` |  |
| `0x2037` | `DispatchGuildRenownInfo` |  |
| `0x2038` | `DispatchGuildRankingsUpdate` |  |
| `0x2039` | `DispatchGuildRenownHistoryUpdate` |  |
| `0x203A` | `DispatchClientAddGuildRenown` |  |
| `0x203B` | `DispatchInformAboutRankings` |  |
| `0x203C` | `DispatchInformAboutRankingWinners` |  |
| `0x203D` | `DispatchRankingCaluculationCountdown` |  |
| `0x203E` | `DispatchPersonalRenownCapReached` |  |
| `0x203F` | `DispatchGuildUpdate` |  |
| `0x2040` | `DispatchGuildRenownUpdate` |  |
| `0x2041` | `DispatchGuildName` |  |
| `0x2042` | `DispatchGuildUpdateMember` |  |
| `0x2043` | `DispatchGuildUpdateOther` |  |
| `0x2044` | `DispatchGuildCreated` |  |
| `0x2045` | `DispatchGuildNonAlphanumericName` |  |
| `0x2046` | `Reserved0x2046` |  |
| `0x2047` | `Reserved0x2047` |  |
| `0x2048` | `DispatchGuildBadName` |  |
| `0x2049` | `DispatchGuildNameWaitTime` |  |
| `0x204A` | `Reserved0x204A` |  |
| `0x204B` | `Reserved0x204B` |  |
| `0x204C` | `Reserved0x204C` |  |
| `0x204D` | `Reserved0x204D` |  |
| `0x204E` | `DispatchGuildOnInviteCooldown` |  |
| `0x204F` | `Reserved0x204F` |  |
| `0x2050` | `DispatchGuildTaxError` |  |
| `0x2051` | `DispatchGuildInvalidGoverningForm` |  |
| `0x2052` | `DispatchGuildUpdateCash` |  |
| `0x2053` | `Reserved0x2053` |  |
| `0x2054` | `DispatchGuildTaxChanged` |  |
| `0x2055` | `DispatchGuildGoverningFormChanged` |  |
| `0x2056` | `DispatchGuildRecruitmentModeChanged` |  |
| `0x2057` | `DispatchGuildMessageOfTheDayChanged` |  |
| `0x2058` | `Reserved0x2058` |  |
| `0x2059` | `DispatchGuildNameChanged` |  |
| `0x205A` | `DispatchGMGuildNameChanged` |  |
| `0x205B` | `DispatchGuildMemberList` |  |
| `0x205C` | `DispatchGuildSearchResult` |  |
| `0x205D` | `DispatchGuildSetPvPPoints` |  |
| `0x205E` | `DispatchGuildSetPvPKillPoints` |  |
| `0x205F` | `DispatchGuildSetPvPTotalKills` |  |
| `0x2060` | `DispatchGuildSetPvPSeasonalKills` |  |
| `0x2061` | `DispatchGuildSetGuildWar` |  |
| `0x2062` | `DispatchGuildSetGuildAsDefenderOfWar` |  |
| `0x2063` | `DispatchAddGuildFeat` |  |
| `0x2064` | `DispatchRemoveGuildFeat` |  |
| `0x2065` | `DispatchGuildRankChanged` |  |
| `0x2066` | `DispatchGuildApplicationRequest` |  |
| `0x2067` | `Reserved0x2067` |  |
| `0x2068` | `DispatchGuildApplicationRejected` |  |
| `0x2069` | `DispatchGuildMergeRequest` |  |
| `0x206A` | `DispatchGuildMergeRequestFailed` |  |
| `0x206B` | `DispatchGuildCityBooked` |  |
| `0x206C` | `DispatchGuildCityBookingFailed` |  |
| `0x206D` | `DispatchNewVulnerabilityWindowData` |  |
| `0x206E` | `Reserved0x206E` |  |
| `0x206F` | `DispatchGuildIsMassivePvPCityOwner` |  |
| `0x2070` | `DispatchGuildIsMassivePvPCityAttacker` |  |
| `0x2071` | `DispatchFeedbackMessage` |  |
| `0x2072` | `DispatchRequestBattleKeepInformation` |  |
| `0x2073` | `DispatchGMReplyGuildInfoFromChar` |  |
| `0x2074` | `Reserved0x2074` |  |
| `0x2075` | `DispatchPvPMatchMakingWantsToStart` |  |
| `0x2076` | `DispatchPvPMatchMakingPlayerJoined` |  |
| `0x2077` | `DispatchPvPMatchMakingPlayerChangedSide` |  |
| `0x2078` | `DispatchPvPMatchMakingPlayerLeft` |  |
| `0x2079` | `DispatchPvPMatchMakingPlayerKicked` |  |
| `0x207A` | `DispatchPvPMatchMakingErrorCode` |  |
| `0x207B` | `DispatchPvPMatchMakingYouJoinedMatch` |  |
| `0x207C` | `Reserved0x207C` |  |
| `0x207D` | `DispatchPvPMatchMakingMatchInfo` |  |
| `0x207E` | `DispatchPvPMatchMakingSearchResult` |  |
| `0x207F` | `DispatchPvPMatchMakingStartClicked` |  |
| `0x2080` | `DispatchPvPMatchMakingYouAreInMatchMaking` |  |
| `0x2081` | `Reserved0x2081` |  |
| `0x2082` | `Reserved0x2082` |  |
| `0x2083` | `DispatchPvPMatchMakingMatchSay` |  |
| `0x2084` | `DispatchPvPMatchMakingCollectingMorePlayers` |  |
| `0x2085` | `DispatchPvPMatchMakingNewMatchLeader` |  |
| `0x2086` | `DispatchPvPMatchMakingTeamLeaderSignedUpForMatchMaking` |  |
| `0x2087` | `Reserved0x2087` |  |
| `0x2088` | `Reserved0x2088` |  |
| `0x2089` | `DispatchPvPMatchMakingCantStartMatchNotAllPlayersHaveSide` |  |
| `0x208A` | `DispatchPvPMatchMakingCantLockToSideMustPickASideFirst` |  |
| `0x208B` | `DispatchPvPMatchMakingCantChangeSideYouHaveBeenLockedToSide` |  |
| `0x208C` | `DispatchPvPMatchMakingCharacterNowLockedToSide` |  |
| `0x208D` | `DispatchPvPMatchMakingCharacterNoLongerLockedToSide` |  |
| `0x208E` | `Reserved0x208E` |  |
| `0x208F` | `Reserved0x208F` |  |
| `0x2090` | `Reserved0x2090` |  |
| `0x2091` | `Reserved0x2091` |  |
| `0x2092` | `DispatchPvPMatchMakingInfoRequestReturn` |  |
| `0x2093` | `DispatchYouJoinedRaid` |  |
| `0x2094` | `DispatchRaidTeamJoined` |  |
| `0x2095` | `DispatchRaidTeamLeft` |  |
| `0x2096` | `DispatchRaidTeamUpdate` |  |
| `0x2097` | `DispatchRaidCharacterUpdate` |  |
| `0x2098` | `DispatchRaidNewRaidLeader` |  |
| `0x2099` | `DispatchRaidLootOptionChanged` |  |
| `0x209A` | `DispatchRaidNewMasterLooter` |  |
| `0x209B` | `DispatchRaidInvite` |  |
| `0x209C` | `DispatchRaidTargetIsBusy` |  |
| `0x209D` | `DispatchRaidIsAlreadyInRaid` |  |
| `0x209E` | `DispatchRaidInvitationTimedOut` |  |
| `0x209F` | `DispatchRaidTargetDeclinedRaidInvite` |  |
| `0x20A0` | `DispatchRaidYouAreNoLongerInvited` |  |
| `0x20A1` | `Reserved0x20A1` |  |
| `0x20A2` | `DispatchRaidCantInviteInvalidSubscriptionFlags` |  |
| `0x20A3` | `Reserved0x20A3` |  |
| `0x20A4` | `DispatchRaidCantCreateInvalidMemberSubscriptionFlags` |  |
| `0x20A5` | `DispatchMassivePvpInfoRequestReturn` |  |
| `0x20A6` | `DispatchMassivePvPBattleQueueFeedback` |  |
| `0x20A7` | `DispatchMassivePvpStartCountDown` |  |
| `0x20A8` | `DispatchMassivePvpBattleQueueStatusUpdate` |  |
| `0x20A9` | `DispatchBattleKeepInfoRequestReturnPhase1And2` |  |
| `0x20AA` | `DispatchBattleKeepInfoRequestReturnPhase3And4` |  |
| `0x20AB` | `DispatchYouGotMailNotification` |  |
| `0x20AC` | `Reserved0x20AC` |  |
| `0x20AD` | `DispatchDebugFullGameServerList` |  |
| `0x20AE` | `DispatchDebugGameServerConnected` |  |
| `0x20AF` | `DispatchDebugGameServerDisconnected` |  |
| `0x20B0` | `DispatchDebugClientConnected` |  |
| `0x20B1` | `DispatchDebugClientChanged` |  |
| `0x20B2` | `DispatchDebugClientDisconnected` |  |
| `0x20B3` | `DispatchDebugFullClientList` |  |
| `0x20B4` | `DispatchDebugFullTeamListBurst` |  |
| `0x20B5` | `DispatchDebugAddTeam` |  |
| `0x20B6` | `DispatchDebugRemoveTeam` |  |
| `0x20B7` | `DispatchDebugUpdateTeam` |  |
| `0x20B8` | `DispatchDebugFullMatchListBurst` |  |
| `0x20B9` | `DispatchDebugAddMatch` |  |
| `0x20BA` | `DispatchDebugMatchChanged` |  |
| `0x20BB` | `DispatchDebugRemoveMatch` |  |
| `0x20BC` | `DispatchDebugAddIDToMatchMaking` |  |
| `0x20BD` | `DispatchDebugRemoveIDFromMatchMaking` |  |
| `0x20BE` | `DispatchItemSalesUpdateBalance` |  |
| `0x20BF` | `DispatchItemSalesCoupons` |  |
| `0x20C0` | `Reserved0x20C0` |  |
| `0x20C1` | `DispatchCSFeedback` |  |
| `0x20C2` | `DispatchGroupFinderSignupFailed` |  |
| `0x20C3` | `Reserved0x20C3` |  |
| `0x20C4` | `DispatchGroupFinderSignupSuccess` |  |
| `0x20C5` | `DispatchGroupFinderRemoved` |  |
| `0x20C6` | `DispatchGroupFinderReadyCheck` |  |
| `0x20C7` | `Reserved0x20C7` |  |
| `0x20C8` | `Reserved0x20C8` |  |
| `0x20C9` | `DispatchGroupFinderReadyCheckStatus` |  |
| `0x20CA` | `DispatchGroupFinderReadyCheckRejected` |  |
| `0x20CB` | `DispatchGroupFinderPlayfieldInfo` |  |
| `0x20CC` | `DispatchGroupFinderFeaturedPlayfieldInfo` |  |
| `0x20CD` | `DispatchGroupFinderFeaturedRaidNeededRoleUpdated` |  |
| `0x20CE` | `DispatchGroupFinderReceivedNeededRole` |  |
| `0x20CF` | `DispatchGroupFinderReceivedRotatingActivityChildInfo` |  |
| `0x20D0` | `DispatchGroupFinderUpdateFeaturedActivities` |  |
| `0x20D1` | `DispatchReturnWorldStat` |  |
| `0x20D2` | `DispatchInvalidWorldStat` |  |

## ChatServerInterfaceOpcodes

ChatServer chat — **no RpcHeader** (raw opcode) · S2C

| Opcode | Name | Note |
|--------|------|------|
| `0x0014` | `ChatSenderInfo` | sender id + name; also sent before a ChannelChat so the client can show the speaker |
| `0x001E` | `Whisper` | incoming tell: [u32 senderCharId][string][00 01 00], preceded by 0x0014 sender info |
| `0x0022` | `CharacterChat` |  |
| `0x0023` | `SystemMessage` |  |
| `0x0025` | `FriendPresence` | [u64 0][u32 stamp][u16][u8][string name] - pushed BEFORE the add reply, by NAME |
| `0x0028` | `FriendAdded` | [u32 characterId][u8 online][u8 5][u32 stamp][u8 class] - the added friend's state |
| `0x0029` | `FriendRemoved` | [u32 characterId] - the removal echoed back |
| `0x003C` | `ChannelSubscription` |  |
| `0x0041` | `ChannelChat` | a channel message relayed to a client: [byte type][u32 channelId][u32 senderId][string][0000] |
| `0x0064` | `ChatReadyAck` | echo of the client's chat-init handshake |

## ChatServerOpcodes

ChatServer chat — **no RpcHeader** (raw opcode) · C2S

| Opcode | Name | Note |
|--------|------|------|
| `0x0000` | `Hello` |  |
| `0x001E` | `Whisper` | client sends a tell: [u32 targetCharId][string][00 01 00] (whisper capture 2026-06-12) |
| `0x0028` | `AddFriend` | [u32 characterId] - the friend list is chat-tier and ONE-SIDED (no accept step) |
| `0x0029` | `RemoveFriend` | [u32 characterId] |
| `0x0041` | `ChannelChat` | client posts a message to a chat channel: [byte type][u32 channelId][string][0000] |
| `0x0064` | `ChatReady` | small chat-init handshake the client sends after the channel subscriptions (000102) |

## GameAgentOpcodes

RPC class **GameAgent** (id `0xB171CE13`) · C2S (client→server call)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `PassBlob` |  |
| `0x2001` | `Ping` |  |
| `0x2002` | `ChatVicinity` |  |
| `0x2003` | `Authenticate2` |  |
| `0x2004` | `GetStats` |  |
| `0x2005` | `UpdateGameServerStats` |  |
| `0x2006` | `DataSink` |  |
| `0x2007` | `ClientDisconnected` |  |
| `0x2008` | `SaveFacebookToken` |  |
| `0x2009` | `RecruitFacebookFriends` |  |
| `0x200A` | `UpdateFacebookFriends` |  |

## GameCharAgentOpcodes

RPC class **GameCharAgent** (id `0xA9ECB95D`) · C2S (client→server call)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `RequestAggroStatus` |  |
| `0x2001` | `RequestCharName` |  |
| `0x2002` | `RequestBindLocation` |  |
| `0x2003` | `SetTutorialStats` |  |
| `0x2004` | `UpdateCharGuildMemberInfo` |  |
| `0x2005` | `GuildInfoUpdate` |  |
| `0x2006` | `JoinedGuild` |  |
| `0x2007` | `LeftGuild` |  |
| `0x2008` | `LeaveTeam` |  |
| `0x2009` | `KickFromTeam` |  |
| `0x200A` | `CreatedGuild` |  |
| `0x200B` | `AddGuildFeat` |  |
| `0x200C` | `RemoveGuildFeat` |  |
| `0x200D` | `GuildGMPromote` |  |
| `0x200E` | `GuildGMDemote` |  |
| `0x200F` | `GuildTaxChanged` |  |
| `0x2010` | `GuildGoverningFormChanged` |  |
| `0x2011` | `GuildTransferMoney` |  |
| `0x2012` | `GuildWithdrawMoney` |  |
| `0x2013` | `GuildDisbanded` |  |
| `0x2014` | `GuildBookedCity` |  |
| `0x2015` | `GuildCityOwnershipChange` |  |
| `0x2016` | `GuildChangeRank` |  |
| `0x2017` | `GuildUpdateGuildBank` |  |
| `0x2018` | `SendGuildInfoToObservers` |  |
| `0x2019` | `GuildRankingsUpdate` |  |
| `0x201A` | `ValidateGuildBadges` |  |
| `0x201B` | `OpenGuildBank` |  |
| `0x201C` | `CloseBank` |  |
| `0x201D` | `SplitInventoryItem` |  |
| `0x201E` | `CancelBuff` |  |
| `0x201F` | `StackInventoryItemIfPossible` |  |
| `0x2020` | `UpdateQuestMarkerNPC` |  |
| `0x2021` | `MagicForgetSpell` |  |
| `0x2022` | `ResurrectMe` |  |
| `0x2023` | `CancelResurrectionRequest` |  |
| `0x2024` | `RespawnMe` |  |
| `0x2025` | `SignUpForWaveRespawn` |  |
| `0x2026` | `UpdateWaveRespawn` |  |
| `0x2027` | `CancelWaveRespawn` |  |
| `0x2028` | `RequestWaveRespawnTimerUpdate` |  |
| `0x2029` | `RespawnMeHere` |  |
| `0x202A` | `GMRequestSpellData` |  |
| `0x202B` | `RM_GetQuestFromQuestgiver` |  |
| `0x202C` | `GMRequestFeatData` |  |
| `0x202D` | `GMRequestFeatClassData` |  |
| `0x202E` | `AdjustSkill` |  |
| `0x202F` | `RequestPCDebugData` |  |
| `0x2030` | `GetLootDataForID` |  |
| `0x2031` | `SimulateLootForID` |  |
| `0x2032` | `CloseShop` |  |
| `0x2033` | `BuyItem` |  |
| `0x2034` | `SellItem` |  |
| `0x2035` | `UndoLastSoldItem` |  |
| `0x2036` | `FormationSetData` |  |
| `0x2037` | `FormationClaimPosition` |  |
| `0x2038` | `FormationClear` |  |
| `0x2039` | `FormationLeave` |  |
| `0x203A` | `FormationUpdatePosRot` |  |
| `0x203B` | `ScryMessageBoxAnswer` |  |
| `0x203C` | `ScryOptionsBoxAnswer` |  |
| `0x203D` | `ExitScenarioResults` |  |
| `0x203E` | `FeatUntrainAnswer` |  |
| `0x203F` | `CraftUntrain` |  |
| `0x2040` | `PlantCTFFlags` |  |
| `0x2041` | `UntrainFeat` |  |
| `0x2042` | `TryOpenLootBag` |  |
| `0x2043` | `TryLootAll` |  |
| `0x2044` | `CloseLootBag` |  |
| `0x2045` | `TryLootCash` |  |
| `0x2046` | `TryLootItem` |  |
| `0x2047` | `PassNeedGreedForItem` |  |
| `0x2048` | `DoNeedGreedRollForItem` |  |
| `0x2049` | `LootLockedItem` |  |
| `0x204A` | `SlaveLooterAnswer` |  |
| `0x204B` | `MasterLooterOffer` |  |
| `0x204C` | `MasterLooterCancelOffer` |  |
| `0x204D` | `SetPassOnLootLevel` |  |
| `0x204E` | `UnbindItem` |  |
| `0x204F` | `ShareQuest` |  |
| `0x2050` | `ShareQuestDeclined` |  |
| `0x2051` | `ShareQuestAccepted` |  |
| `0x2052` | `ShareQuestBusy` |  |
| `0x2053` | `RequestNextQuestPendingReward` |  |
| `0x2054` | `RequestTeamQuestList` |  |
| `0x2055` | `AddQuestList` |  |
| `0x2056` | `AcceptNextAwaitingQuest` |  |
| `0x2057` | `QuestManagerMsg` |  |
| `0x2058` | `StartQuickTeleport` |  |
| `0x2059` | `DoPaintballQuery` |  |
| `0x205A` | `PerformSpecialAction` |  |
| `0x205B` | `QueuePetCommand` |  |
| `0x205C` | `DoSkillSellback` |  |
| `0x205D` | `PetSetGroundTargetPos` |  |
| `0x205E` | `PlayStockphraseAsSubtitle` |  |
| `0x205F` | `CinematicTransition` |  |
| `0x2060` | `RequestACGTeleport` |  |
| `0x2061` | `RequestClearOwnACG` |  |
| `0x2062` | `RequestDropTeamBind` |  |
| `0x2063` | `DebugSetOceanData` |  |
| `0x2064` | `RequestTrade` |  |
| `0x2065` | `StartTrade` |  |
| `0x2066` | `DeclineTrade` |  |
| `0x2067` | `SetTradeCash` |  |
| `0x2068` | `AddTradeItem` |  |
| `0x2069` | `RemoveTradeItem` |  |
| `0x206A` | `MoveTradeItemInternal` |  |
| `0x206B` | `AcceptTrade` |  |
| `0x206C` | `AbortTrade` |  |
| `0x206D` | `RequestShortcutBar` |  |
| `0x206E` | `AddShortcut` |  |
| `0x206F` | `UpdateShortcut` |  |
| `0x2070` | `MoveShortcut` |  |
| `0x2071` | `RemoveShortcut` |  |
| `0x2072` | `StartMinigame` |  |
| `0x2073` | `NewMinigamePlayer` |  |
| `0x2074` | `DropMinigamePlayer` |  |
| `0x2075` | `ResendMinigameInfoToPlayer` |  |
| `0x2076` | `FlagAsCoward` |  |
| `0x2077` | `ForceStopMinigame` |  |
| `0x2078` | `ForceSetMinigameTime` |  |
| `0x2079` | `PrepareMinigameTime` |  |
| `0x207A` | `PrepareToMinigameTime` |  |
| `0x207B` | `GetInstanceListForCurrentPlayfield` |  |
| `0x207C` | `ShiftInstanceForCurrentPlayfield` |  |
| `0x207D` | `EnableHeroicMode` |  |
| `0x207E` | `SetHideInspectOption` |  |
| `0x207F` | `SetPlayfieldInstanceList` |  |
| `0x2080` | `InstanceWillResetOnShutdown` |  |
| `0x2081` | `InstanceHasBeenLocked` |  |
| `0x2082` | `InstanceHasBeenUnLocked` |  |
| `0x2083` | `RequestTeamMemberPosList` |  |
| `0x2084` | `TradepostSearchResult` |  |
| `0x2085` | `Ping` |  |
| `0x2086` | `ShiftTeamInstance` |  |
| `0x2087` | `SubscriptionFlagsUpdated` |  |
| `0x2088` | `PlayerServicesUpdated` |  |
| `0x2089` | `PassiveAbilityEquip` |  |
| `0x208A` | `PassiveAbilityUnequip` |  |
| `0x208B` | `ExitPassiveMode` |  |
| `0x208C` | `EnterPassiveMode` |  |
| `0x208D` | `RequestMassivePvPScoreData` |  |
| `0x208E` | `RequestScorePlayerNames` |  |
| `0x208F` | `SetVulnerabilityWindowStartTime` |  |
| `0x2090` | `RequestRecipeInfo` |  |
| `0x2091` | `RequestTeamInvite` |  |
| `0x2092` | `RequestNewJoinHasHostileInFriends` |  |
| `0x2093` | `RequestTeamInviteWithInviterFactionPoints` |  |
| `0x2094` | `RequestItemSalesData` |  |
| `0x2095` | `RequestItemSaleLimitedItemList` |  |
| `0x2096` | `RequestItemSaleFavoriteData` |  |
| `0x2097` | `SetFakeBillingConnection` |  |
| `0x2098` | `AddFavoriteItem` |  |
| `0x2099` | `RemoveFavoriteItem` |  |
| `0x209A` | `BuyItemSalesItem` |  |
| `0x209B` | `CSBuyItemReply` |  |
| `0x209C` | `CSRollbackResponse` |  |
| `0x209D` | `ExecuteChatCommand` |  |
| `0x209E` | `ReplyUpdatedDimensionDB` |  |
| `0x209F` | `RequestUseTeleportSpell` |  |
| `0x20A0` | `UnlockFromPlayfield` |  |
| `0x20A1` | `UpdateMailBank` |  |
| `0x20A2` | `SetSelectedTag` |  |
| `0x20A3` | `SetSelectedBadgeTag` |  |
| `0x20A4` | `KickPlayerFromGroupPlayfield` |  |
| `0x20A5` | `WorldStatChanged` |  |
| `0x20A6` | `UpdateNoXPGainStat` |  |
| `0x20A7` | `SendReport` |  |
| `0x20A8` | `RunLua` |  |

## GameCharAgentProjectOpcodes

RPC class **GameCharAgentProject** (id `0x5B9756FC`) · C2S (client→server call)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `RequestAggroStatus` |  |
| `0x2001` | `GetProcDataForID` |  |
| `0x2002` | `AcceptApprenticeTeacher` |  |
| `0x2003` | `DeclineApprenticeTeacher` |  |
| `0x2004` | `RequestApprenticeStudent` |  |
| `0x2005` | `LeaveApprenticeMode` |  |
| `0x2006` | `RequestHitAndMitigationTables` |  |
| `0x2007` | `StartOfflineTrainingSystem` |  |
| `0x2008` | `ResetOfflineTrainingSystem` |  |
| `0x2009` | `RefreshEquippedAbilities` |  |
| `0x200A` | `EnableRangerTracking` |  |
| `0x200B` | `ToggleVanityArmorFilter` |  |
| `0x200C` | `SetSprintInputState` |  |

## GameCharInterfaceOpcodes

RPC class **GameCharInterface** (id `0x8B87F791`) · S2C (server→client dispatch)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `DispatchUpdateAggroStatus` |  |
| `0x2001` | `DispatchUpdateName` |  |
| `0x2002` | `DispatchUpdateBindLocation` |  |
| `0x2003` | `DispatchGuildNotEnoughCash` |  |
| `0x2004` | `Reserved0x2004` |  |
| `0x2005` | `DispatchUpdateCash` |  |
| `0x2006` | `DispatchUpdateCashRepository` |  |
| `0x2007` | `DispatchMoveInventoryItemToOrFromGuildBank` |  |
| `0x2008` | `DispatchSetQuestMarker` |  |
| `0x2009` | `DispatchChangeItemStackSize` |  |
| `0x200A` | `DispatchLearnSpell` |  |
| `0x200B` | `DispatchForgetSpell` |  |
| `0x200C` | `Reserved0x200C` |  |
| `0x200D` | `DispatchTradeskillComplete` |  |
| `0x200E` | `Reserved0x200E` |  |
| `0x200F` | `DispatchClearRecipe` |  |
| `0x2010` | `DispatchRemoveRecipesByGroupClient` |  |
| `0x2011` | `DispatchUntrainSingleFeat` |  |
| `0x2012` | `DispatchDisplayItemLootChatMessage` |  |
| `0x2013` | `Reserved0x2013` |  |
| `0x2014` | `DispatchSendNPCAutoTradeCashAmount` |  |
| `0x2015` | `DispatchSetDialogueCamera` |  |
| `0x2016` | `DispatchAddTag` |  |
| `0x2017` | `DispatchRemoveTag` |  |
| `0x2018` | `DispatchResurrectRequest` |  |
| `0x2019` | `DispatchSetDbDebugCategory` |  |
| `0x201A` | `DispatchSetDbDebugLevel` |  |
| `0x201B` | `Reserved0x201B` |  |
| `0x201C` | `DispatchSetItemStat` |  |
| `0x201D` | `DispatchAddToItemSlot` |  |
| `0x201E` | `DispatchRemoveFromItemSlot` |  |
| `0x201F` | `DispatchRemoveItem` |  |
| `0x2020` | `DispatchAddItem` |  |
| `0x2021` | `DispatchMoveInventoryItemToGuildBank` |  |
| `0x2022` | `DispatchMoveInventoryItemFromGuildBank` |  |
| `0x2023` | `DispatchCharacterNameReply` |  |
| `0x2024` | `DispatchValidResurrectionSpots` |  |
| `0x2025` | `Reserved0x2025` |  |
| `0x2026` | `DispatchReplyPCDebugData` |  |
| `0x2027` | `DispatchReplyLootForCharacter` |  |
| `0x2028` | `DispatchReplyLootForCharacterAddHash` |  |
| `0x2029` | `DispatchReplyLootForCharacterTemplateClusterOk` |  |
| `0x202A` | `DispatchReplyLootForCharacterTemplateClusterError` |  |
| `0x202B` | `DispatchReplyLootForCharacterAddTreasureTemplate` |  |
| `0x202C` | `DispatchReplySimulateLootForID` |  |
| `0x202D` | `DispatchFormationSetData` |  |
| `0x202E` | `DispatchFormationClaimPosition` |  |
| `0x202F` | `DispatchFormationUpdatePosition` |  |
| `0x2030` | `DispatchFormationClearPosition` |  |
| `0x2031` | `Reserved0x2031` |  |
| `0x2032` | `DispatchFormationUpdatePosRot` |  |
| `0x2033` | `DispatchShopData` |  |
| `0x2034` | `DispatchCloseShop` |  |
| `0x2035` | `DispatchItemSoldToShop` |  |
| `0x2036` | `DispatchItemBoughtBack` |  |
| `0x2037` | `DispatchOpenLootBag` |  |
| `0x2038` | `DispatchCloseLootBag` |  |
| `0x2039` | `DispatchDoNeedGreedRollForItemsInLootBag` |  |
| `0x203A` | `DispatchCancelNeedGreedRollForItemsInLootBag` |  |
| `0x203B` | `DispatchOpenDialogForLockedItemsInLootBag` |  |
| `0x203C` | `DispatchCloseDialogForLockedItemsInLootBag` |  |
| `0x203D` | `DispatchMasterLooterOfferItemTo` |  |
| `0x203E` | `DispatchSlaveLooterAnswer` |  |
| `0x203F` | `DispatchMasterLooterCancelOffer` |  |
| `0x2040` | `DispatchOverrideGameTweaks` |  |
| `0x2041` | `DispatchShowClientFIFO` |  |
| `0x2042` | `DispatchAddFormation` |  |
| `0x2043` | `DispatchRemoveFormation` |  |
| `0x2044` | `DispatchUpgradeCombo` |  |
| `0x2045` | `DispatchUpgradeComboStep` |  |
| `0x2046` | `DispatchXPSelectClass` |  |
| `0x2047` | `DispatchNextQuestAvailable` |  |
| `0x2048` | `DispatchQuestShareGlobalFeedback` |  |
| `0x2049` | `DispatchQuestShareIndividualFeedback` |  |
| `0x204A` | `DispatchQuestShareRequest` |  |
| `0x204B` | `DispatchUpdateTeamQuestList` |  |
| `0x204C` | `DispatchCancelTeamQuestList` |  |
| `0x204D` | `DispatchUpdateTokenAmount` |  |
| `0x204E` | `DispatchLockTemplate` |  |
| `0x204F` | `DispatchApplyFactionRank` |  |
| `0x2050` | `DispatchApplyFactionPoints` |  |
| `0x2051` | `Reserved0x2051` |  |
| `0x2052` | `DispatchAddSpecialAbility` |  |
| `0x2053` | `DispatchRemoveSpecialAbility` |  |
| `0x2054` | `DispatchPerformSpecialAction` |  |
| `0x2055` | `DispatchQueuePetCommand` |  |
| `0x2056` | `DispatchACGTeleportChoice` |  |
| `0x2057` | `DispatchACGSelfInstanceCleared` |  |
| `0x2058` | `DispatchACGTeamBindChanged` |  |
| `0x2059` | `Reserved0x2059` |  |
| `0x205A` | `DispatchOverrideJumpHeight` |  |
| `0x205B` | `DispatchOverrideJumpSpeed` |  |
| `0x205C` | `DispatchVisualSimulateNpc` |  |
| `0x205D` | `DispatchReceivedStockPhraseText` |  |
| `0x205E` | `DispatchScryShowMessageBox` |  |
| `0x205F` | `DispatchScryCloseMessageBox` |  |
| `0x2060` | `DispatchScryShowOptionsBox` |  |
| `0x2061` | `DispatchScryCloseOptionsBox` |  |
| `0x2062` | `DispatchOpenScenarioResults` |  |
| `0x2063` | `DispatchScrySendCustomGUIData` |  |
| `0x2064` | `DispatchFeatUntrainMessageBox` |  |
| `0x2065` | `DispatchCraftUntrainMessageBox` |  |
| `0x2066` | `DispatchUnbindItemReply` |  |
| `0x2067` | `DispatchBroadcastMessage` |  |
| `0x2068` | `DispatchRequestTrade` |  |
| `0x2069` | `DispatchStartTrade` |  |
| `0x206A` | `Reserved0x206A` |  |
| `0x206B` | `Reserved0x206B` |  |
| `0x206C` | `DispatchPartnerAcceptedTrade` |  |
| `0x206D` | `Reserved0x206D` |  |
| `0x206E` | `DispatchPartnerUpdatedCash` |  |
| `0x206F` | `DispatchTradePartnerAddedItem` |  |
| `0x2070` | `DispatchTradeAddedItem` |  |
| `0x2071` | `Reserved0x2071` |  |
| `0x2072` | `Reserved0x2072` |  |
| `0x2073` | `DispatchTradePartnerRemovedItem` |  |
| `0x2074` | `DispatchTradeRemovedItem` |  |
| `0x2075` | `Reserved0x2075` |  |
| `0x2076` | `DispatchShortcutBarReply` |  |
| `0x2077` | `Reserved0x2077` |  |
| `0x2078` | `Reserved0x2078` |  |
| `0x2079` | `Reserved0x2079` |  |
| `0x207A` | `DispatchAddWayPoint` |  |
| `0x207B` | `DispatchRemoveWayPoint` |  |
| `0x207C` | `DispatchReplyWaveRespawnTimerUpdate` |  |
| `0x207D` | `DispatchGMReplySpellData` |  |
| `0x207E` | `DispatchGMReplyFeatData` |  |
| `0x207F` | `DispatchGMReplyFeatClassData` |  |
| `0x2080` | `DispatchInstanceSelectionForCurrentPlayfield` |  |
| `0x2081` | `DispatchApprenticeModeOnOff` |  |
| `0x2082` | `DispatchPong` |  |
| `0x2083` | `DispatchUpdateTeamMemberPosList` |  |
| `0x2084` | `DispatchDynamicSpaceDebugInfo` |  |
| `0x2085` | `DispatchSetGameTweakValue` |  |
| `0x2086` | `Reserved0x2086` |  |
| `0x2087` | `DispatchMinigameScoreChanged` |  |
| `0x2088` | `DispatchMinigameFullScore` |  |
| `0x2089` | `Reserved0x2089` |  |
| `0x208A` | `DispatchMinigameMatchStarted` |  |
| `0x208B` | `DispatchMinigameMatchEnded` |  |
| `0x208C` | `DispatchMinigameCaptureZoneUpdate` |  |
| `0x208D` | `DispatchMinigameCaptureZoneList` |  |
| `0x208E` | `DispatchMinigameCaptureZoneLeaveEnter` |  |
| `0x208F` | `DispatchForceStopMinigame` |  |
| `0x2090` | `DispatchSetMinigameTime` |  |
| `0x2091` | `DispatchMinigameStartsInXSeconds` |  |
| `0x2092` | `Reserved0x2092` |  |
| `0x2093` | `DispatchMassivePvPFullScoreUpdate` |  |
| `0x2094` | `DispatchMassivePvPGuildScoreUpdate` |  |
| `0x2095` | `DispatchMassivePvPPlayerScoreUpdate` |  |
| `0x2096` | `DispatchMassivePvPScoreNames` |  |
| `0x2097` | `Reserved0x2097` |  |
| `0x2098` | `Reserved0x2098` |  |
| `0x2099` | `DispatchShowVulnerabilityWindowGui` |  |
| `0x209A` | `DispatchPassiveAbilityEquip` |  |
| `0x209B` | `DispatchPassiveAbilityUnequip` |  |
| `0x209C` | `DispatchReplyRecipeInfo` |  |
| `0x209D` | `DispatchAddEffectPackage` |  |
| `0x209E` | `DispatchEnterExitGamingRoom` |  |
| `0x209F` | `DispatchReplyTeamInvite` |  |
| `0x20A0` | `DispatchReplyNewJoinHasHostileInFriends` |  |
| `0x20A1` | `Reserved0x20A1` |  |
| `0x20A2` | `DispatchReplyItemForCashSaleSetData` |  |
| `0x20A3` | `DispatchReplyItemForCashSaleUpdateData` |  |
| `0x20A4` | `DispatchItemSaleResult` |  |
| `0x20A5` | `DispatchReplyItemSaleFavoriteData` |  |
| `0x20A6` | `DispatchItemSalesUpdateBalance` |  |
| `0x20A7` | `DispatchPlayfieldAccessDenied` |  |
| `0x20A8` | `DispatchPlayfieldAccessGranted` |  |
| `0x20A9` | `DispatchSendPlayfieldAccessData` |  |
| `0x20AA` | `DispatchSendPlayfieldLockoutData` |  |
| `0x20AB` | `DispatchLockAllSpells` |  |
| `0x20AC` | `DispatchAddSpellResistance` |  |
| `0x20AD` | `DispatchRemoveSpellResistance` |  |
| `0x20AE` | `DispatchSetStackinglineResists` |  |
| `0x20AF` | `DispatchPlayerServicesUpdated` |  |
| `0x20B0` | `DispatchOnFacebookTokenResult` |  |
| `0x20B1` | `DispatchOnUpdateFacebookFriends` |  |
| `0x20B2` | `DispatchOnFacebookFriendSignedUp` |  |
| `0x20B3` | `DispatchRequestGetEffect` |  |
| `0x20B4` | `DispatchSignalWorldStatChanged` |  |

## GameCharInterfaceProjectOpcodes

RPC class **GameCharInterfaceProject** (id `0x28377648`) · C2S (client→server call)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `DispatchUpdateAggroStatus` |  |
| `0x2001` | `DispatchGuildBankData` |  |
| `0x2002` | `Reserved0x2002` |  |
| `0x2003` | `DispatchRequestApprentice` |  |
| `0x2004` | `DispatchAddApprenticeEntry` |  |
| `0x2005` | `DispatchRemoveApprenticeEntry` |  |
| `0x2006` | `DispatchDoYouWantToShiftTeamInstance` |  |
| `0x2007` | `DispatchInDungeonPlayfield` |  |
| `0x2008` | `DispatchReplyDebugGetProcData` |  |
| `0x2009` | `DispatchReplyDebugGetSpellProcData` |  |
| `0x200A` | `DispatchSendHitAndMitigationTable` |  |
| `0x200B` | `DispatchSetIsSprinting` |  |
| `0x200C` | `DispatchSyncCanSprint` |  |
| `0x200D` | `DispatchSyncRegenDelayed` |  |
| `0x200E` | `DispatchUpdateSprintBar` |  |
| `0x200F` | `DispatchOpenSiteInBrowserOnClient` |  |

## GameInterfaceOpcodes

RPC class **GameInterface** (id `0x6C67C147`) · S2C (server→client dispatch)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `DispatchPassBlob` |  |
| `0x2001` | `DispatchAckAuthenticate` |  |
| `0x2002` | `DispatchRedirectToServer` |  |
| `0x2003` | `Reserved0x2003` |  |
| `0x2004` | `Reserved0x2004` |  |
| `0x2005` | `DispatchPong` |  |
| `0x2006` | `DispatchPongWithPacketTiming` |  |
| `0x2007` | `DispatchInfoToUser` |  |
| `0x2008` | `DispatchGameStats` |  |
| `0x2009` | `DispatchGameServerStats` |  |
| `0x200A` | `DispatchGameServerDataset` |  |
| `0x200B` | `DispatchReportDimesionID` |  |
| `0x200C` | `DispatchReportServerID` |  |
| `0x200D` | `DispatchDataSink` |  |
| `0x200E` | `Reserved0x200E` |  |
| `0x200F` | `DispatchDisplayPlayfieldAccessPopup` |  |

## GameServer0X2000Opcodes

The 0x2000-carrier IIR type sub-opcodes (32-bit BKDR-31 hashes of the client IIR type names).
Catalogued in [`iir-types.md`](iir-types.md).

## PlayerAgentOpcodes

RPC class **PlayerAgent** (id `0x82F20484`) · C2S (client→server call)

| Opcode | Name | Note |
|--------|------|------|
| `0x2083` | `Authenticate` | id 0x96CBE509 |
| `0x208E` | `FinishGetStartupData` | id 0xA3E1FE0D |
| `0x209D` | `DeleteCharacter` | id 0x86979E0C |
| `0x20A6` | `LoadFacebookToken` | id 0xE0C19C0D |
| `0x20B6` | `LoginCharacter` | id 0xDD85FB0E |
| `0x20C1` | `GetStartupData` | id 0xDAD1D206 |
| `0x20D6` | `VerifyLanguageSetting` | id 0xDD9EC209 |
| `0x20DE` | `CreateCharacter` | id 0x9CB1D10C |
| `0x20EC` | `ReceivedCharacterSettings` | id 0xA4F2E303 |

## PlayerInterfaceOpcodes

RPC class **PlayerInterface** (id `0x7D155738`) · S2C (server→client dispatch)

| Opcode | Name | Note |
|--------|------|------|
| `0x1A07` | `OverrideGameTweaks` | rides the ServiceConnection endpoint with a 13-byte session header-extra (not the 4-byte id); sent verbatim |
| `0x208C` | `SendCharacterRealmData` | id 0xCBC6FC04 |
| `0x2091` | `InitAuth` | id 0x9386EE05 |
| `0x2093` | `Ox2093` | id 0xF2A5E705 |
| `0x209C` | `Ox209C` | id 0x99959205 |
| `0x20A5` | `SmallCharList` | retail sends a 3-byte header-extra 0x94A760 (88/88 captures), not the 4-byte id; sent verbatim |
| `0x20B9` | `Ox20B9` | id 0x8BD89902 |
| `0x20CC` | `SendRealmList` | id 0xE2E6C40F |
| `0x20E5` | `Ox20E5` | id 0xDDAD8302 |
| `0x20EF` | `SendCharacterList` | id 0x8BD3A00C |

## TradepostAgentOpcodes

RPC class **TradepostAgent** (id `0x3D744981`) · C2S (client→server call)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `GetAllMails` | the mailbox poll |
| `0x2001` | `GetMailByID` | body [mailId:u64] |
| `0x2002` | `SendMail` |  |
| `0x2003` | `MarkMailRead` | also observed for the delete-side flow |
| `0x2004` | `CheckNewMail` |  |
| `0x2005` | `GetMailItems` |  |
| `0x2006` | `GetMailItem` | body [mailId][itemId] |
| `0x2007` | `GetItemData` | observed with a [mailId]-style body (was decoded as "GetMailItems") |
| `0x2008` | `DeleteMail` |  |
| `0x2009` | `CancelMail` |  |
| `0x200A` | `TradepostSellItem` |  |
| `0x200B` | `TradepostChangeItemPrice` |  |
| `0x200C` | `TradepostCancelSellItem` |  |
| `0x200D` | `ItemSearch` | TradepostItemSearch (query body) |
| `0x200E` | `BuyItem` | TradepostBuyItem [saleId:u64][searchResultId:u64] |
| `0x200F` | `TradepostOpen` | not observed in captures - the client opens with UpdateItemTimeouts instead |
| `0x2010` | `Close` | TradepostClose (empty body) |
| `0x2011` | `UpdateItemTimeouts` | TradepostUpdateItemTimeouts - sent (empty body) when the AH window opens |
| `0x2012` | `SetAsyncEnabled` |  |
| `0x2013` | `SetNumWorkerThreads` |  |

## TradepostInterfaceOpcodes

RPC class **TradepostInterface** (id `0xF2C2D5B5`) · S2C (server→client dispatch)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `DispatchOpenTradepost` |  |
| `0x2001` | `DispatchClose` | no-arg; the string catalog missed it (recovered from the TradepostInterface RE) |
| `0x2002` | `DispatchMailMessage` |  |
| `0x2003` | `DispatchSendMailResult` |  |
| `0x2004` | `DispatchRetrieveMailResult` |  |
| `0x2005` | `DispatchTradepostSearchResults` |  |
| `0x2006` | `DispatchDeleteCanceledMail` |  |
| `0x2007` | `DispatchDeleteCanceledMailItem` |  |

## UniverseAgentOpcodes

RPC class **UniverseAgent** (id `0x604DDBA0`) · C2S (client→server call)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `InitiateAuthentication` |  |
| `0x2001` | `AnswerChallenge` |  |
| `0x2002` | `ClientDisconnected` |  |

## UniverseInterfaceOpcodes

RPC class **UniverseInterface** (id `0x0C384054`) · S2C (server→client dispatch)

| Opcode | Name | Note |
|--------|------|------|
| `0x2000` | `DispatchChallenge` | (was "InitiateAuthentication" - it carries the DH challenge reply) |
| `0x2001` | `DispatchAckAuthenticate` | carries the PlayerAgent realm handoff (was "SendPlayerAgentRealm") |
| `0x2002` | `DispatchLoginProblem` |  |
| `0x2003` | `DispatchSetUpdateManager` |  |
| `0x2004` | `DispatchSetUniverseManagerError` |  |
| `0x2005` | `DispatchSetRegionState` | (was "SetRegion") |
