# Opcode catalog (generated)

> **GENERATED — do not edit.** Source: the compiled enums in
> `Faolan.Core/Enums/Opcodes.cs`, read by reflection.
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'`.

16 enums, 860 entries. The 32-bit IIR sub-opcodes carried inside a PassBlob (`IirTypes`) are catalogued separately in [`iir-types.md`](iir-types.md).

## What is (and is not) verified here [V1]

- **Names + values** are the server's curated method catalogs — the same tables that drive the
  Wireshark dissector, recovered from the client's RPC method registration. A value here IS the
  `method_instance` the RpcHeader carries (field 4, one varint): the registration INDEX on the
  index-registered classes, the 32-bit registration HASH on PlayerAgent/PlayerInterface. This is a
  **V1** fact: the method set is complete and client-verified.
- **Reads as** is what a hex dump, the Wireshark plugin and a `.tshark.json` show for it — the method
  field's tag `0x20` then the varint's first group. A rendering, never an identity: on the hashed pair
  it keeps 7 of 32 bits and names two methods at once (#613/#615).
- **A named method does NOT mean the message BODY is decoded.** Body layouts are documented
  per-message (and gated by golden tests) elsewhere; see [`coverage.md`](coverage.md) for which have one.
- **Direction** is the RpcHeader receiver's role: `*Agent` classes receive client→server calls (C2S),
  `*Interface` classes receive server→client dispatches (S2C). ChatServer chat carries no RpcHeader.
- **Class id** = BKDR-31 hash of the client class name, recomputed + asserted here.

## CSPlayerAgentMethods

RPC class **CSPlayerAgent** (id `0x6DD55614`) · C2S (client→server call)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `Authenticate` |  |
| `0x01` | `0x2001` | `CharacterAuthenticated` |  |
| `0x02` | `0x2002` | `CharacterDisconnected` |  |
| `0x03` | `0x2003` | `TeamInvite` |  |
| `0x04` | `0x2004` | `TeamInviteByName` |  |
| `0x05` | `0x2005` | `TeamApply` |  |
| `0x06` | `0x2006` | `TeamSetLootOption` |  |
| `0x07` | `0x2007` | `TeamSetMasterLooter` |  |
| `0x08` | `0x2008` | `AcceptTeamInvite` |  |
| `0x09` | `0x2009` | `DeclineTeamInvite` |  |
| `0x0A` | `0x200A` | `CharacterSearch` |  |
| `0x0B` | `0x200B` | `SearchForTeam` |  |
| `0x0C` | `0x200C` | `SetNewTeamLeader` |  |
| `0x0D` | `0x200D` | `SummonCharacters` |  |
| `0x0E` | `0x200E` | `AcceptSummon` |  |
| `0x0F` | `0x200F` | `DeclineSummon` |  |
| `0x10` | `0x2010` | `GetExtendedCharacterInfo` |  |
| `0x11` | `0x2011` | `GetExtendedCharacterInfoByName` |  |
| `0x12` | `0x2012` | `SetLookingForTeamStatus` |  |
| `0x13` | `0x2013` | `SetLookingForMemberStatus` |  |
| `0x14` | `0x2014` | `GetLookingForTeamStatus` |  |
| `0x15` | `0x2015` | `GetLookingForMemberStatus` |  |
| `0x16` | `0x2016` | `SetCommunityServerOptions` |  |
| `0x17` | `0x2017` | `StartVoteKick` |  |
| `0x18` | `0x2018` | `VoteKickAnswer` |  |
| `0x19` | `0x2019` | `StartVoteRetreat` | StartVoteRetreat(), NO params. The "observed [u32 0xC350][u32 clientInstance] at login" was 0x201E ClientRequestGuildInfo mis-attributed (#458) |
| `0x1A` | `0x201A` | `VoteRetreatAccept` |  |
| `0x1B` | `0x201B` | `VoteRetreatReject` |  |
| `0x1C` | `0x201C` | `AcceptGuildInvitation` |  |
| `0x1D` | `0x201D` | `DeclineGuildInvitation` |  |
| `0x1E` | `0x201E` | `ClientRequestGuildInfo` |  |
| `0x1F` | `0x201F` | `ClientRequestGuildName` |  |
| `0x20` | `0x2020` | `ClientRequestCharIDByName` |  |
| `0x21` | `0x2021` | `CreateGuild` |  |
| `0x22` | `0x2022` | `GuildInviteByName` |  |
| `0x23` | `0x2023` | `GuildInvite` |  |
| `0x24` | `0x2024` | `RemoveGuildInvitationCooldown` |  |
| `0x25` | `0x2025` | `GuildPromote` |  |
| `0x26` | `0x2026` | `GuildDemote` |  |
| `0x27` | `0x2027` | `GuildKick` |  |
| `0x28` | `0x2028` | `GuildLeave` |  |
| `0x29` | `0x2029` | `GuildTax` |  |
| `0x2A` | `0x202A` | `GuildGovernmentForm` |  |
| `0x2B` | `0x202B` | `GuildRecruitmentMode` |  |
| `0x2C` | `0x202C` | `GuildName` |  |
| `0x2D` | `0x202D` | `GuildDescription` |  |
| `0x2E` | `0x202E` | `GuildMessageOfTheDay` |  |
| `0x2F` | `0x202F` | `GuildRequestMemberList` |  |
| `0x30` | `0x2030` | `GuildApplication` |  |
| `0x31` | `0x2031` | `GuildRejectApplication` |  |
| `0x32` | `0x2032` | `SetLookingForGuild` |  |
| `0x33` | `0x2033` | `GuildMergeRequest` |  |
| `0x34` | `0x2034` | `GuildMergeReply` |  |
| `0x35` | `0x2035` | `GuildWithdrawMoney` |  |
| `0x36` | `0x2036` | `GetPvpPointsInfo` |  |
| `0x37` | `0x2037` | `AddPvpPoints` |  |
| `0x38` | `0x2038` | `RemovePvpPoints` |  |
| `0x39` | `0x2039` | `ClearPvpPoints` |  |
| `0x3A` | `0x203A` | `GetGuildRenownInfo` |  |
| `0x3B` | `0x203B` | `AddGuildRenown` |  |
| `0x3C` | `0x203C` | `RemoveGuildRenown` |  |
| `0x3D` | `0x203D` | `SetGuildRenown` |  |
| `0x3E` | `0x203E` | `CalculateRankings` |  |
| `0x3F` | `0x203F` | `UnlockGuildRenown` |  |
| `0x40` | `0x2040` | `RequestRenownHistory` |  |
| `0x41` | `0x2041` | `RequestRenownRankings` |  |
| `0x42` | `0x2042` | `RequestBattleKeepInformation` |  |
| `0x43` | `0x2043` | `SearchForGuilds` |  |
| `0x44` | `0x2044` | `GMGuildName` |  |
| `0x45` | `0x2045` | `RequestGuildCitySpot` |  |
| `0x46` | `0x2046` | `ApplyForMassivePVPBattleQueue` |  |
| `0x47` | `0x2047` | `WithdrawFromMassivePVPBattleQueue` |  |
| `0x48` | `0x2048` | `MassivePvPBattleQueueGMCommand` |  |
| `0x49` | `0x2049` | `MassivePvpBattleQueueStatusRequest` |  |
| `0x4A` | `0x204A` | `PvPMatchMakingCreateMatch` |  |
| `0x4B` | `0x204B` | `PvPMatchMakingJoinMatch` |  |
| `0x4C` | `0x204C` | `PvPMatchMakingSetSide` |  |
| `0x4D` | `0x204D` | `PvPMatchMakingMatchSay` |  |
| `0x4E` | `0x204E` | `PvPMatchMakingLeaveMatch` |  |
| `0x4F` | `0x204F` | `PvPMatchMakingSearchForMatches` |  |
| `0x50` | `0x2050` | `PvPMatchMakingGetMatchInfo` |  |
| `0x51` | `0x2051` | `PvPMatchMakingTeleportToMinigame` |  |
| `0x52` | `0x2052` | `PvPMatchMakingPutIntoMatchMaking` |  |
| `0x53` | `0x2053` | `PvPMatchMakingRemoveFromMatchMaking` |  |
| `0x54` | `0x2054` | `PvPMatchMakingRequestInfo` |  |
| `0x55` | `0x2055` | `PvPMatchMakingIsInMatchMaking` |  |
| `0x56` | `0x2056` | `PvPMatchMakingKickPlayer` |  |
| `0x57` | `0x2057` | `PvPMatchMakingBanPlayer` |  |
| `0x58` | `0x2058` | `PvPMatchMakingMakePlayerMatchOwner` |  |
| `0x59` | `0x2059` | `PvPMatchMakingForceSide` |  |
| `0x5A` | `0x205A` | `PvPMatchMakingStartMatch` |  |
| `0x5B` | `0x205B` | `DebugPvPMatchMakingShowDebugInfo` |  |
| `0x5C` | `0x205C` | `DebugPvPMatchMakingStopAllMatches` |  |
| `0x5D` | `0x205D` | `DebugPvPMatchMakingStopCurrentMatch` |  |
| `0x5E` | `0x205E` | `DebugPvPMatchMakingSetNewTimeLimit` |  |
| `0x5F` | `0x205F` | `DebugPvPMatchMakingRestart` |  |
| `0x60` | `0x2060` | `DebugPvPMatchForcePlayerIntoCurrentMatch` |  |
| `0x61` | `0x2061` | `DebugPvPMatchMakingDoMatchMaking` |  |
| `0x62` | `0x2062` | `DebugPvPMatchForceStartMatch` |  |
| `0x63` | `0x2063` | `DebugPvPMatchForceEnterAll` |  |
| `0x64` | `0x2064` | `DebugCommunityServerModuleStart` |  |
| `0x65` | `0x2065` | `DebugCommunityServerModuleStop` |  |
| `0x66` | `0x2066` | `SetAsyncEnabled` |  |
| `0x67` | `0x2067` | `SetNumWorkerThreads` |  |
| `0x68` | `0x2068` | `RaidCreate` |  |
| `0x69` | `0x2069` | `RaidLeave` |  |
| `0x6A` | `0x206A` | `RaidInviteTeam` |  |
| `0x6B` | `0x206B` | `RaidInviteTeamAccept` |  |
| `0x6C` | `0x206C` | `RaidInviteTeamDecline` |  |
| `0x6D` | `0x206D` | `RaidKickTeam` |  |
| `0x6E` | `0x206E` | `RaidKickCharacter` |  |
| `0x6F` | `0x206F` | `RaidMoveTeamMember` |  |
| `0x70` | `0x2070` | `RaidSwapCharacters` |  |
| `0x71` | `0x2071` | `RaidInviteCharacter` |  |
| `0x72` | `0x2072` | `RaidInviteCharacterByName` |  |
| `0x73` | `0x2073` | `RaidAcceptInvite` |  |
| `0x74` | `0x2074` | `RaidDeclineInvite` |  |
| `0x75` | `0x2075` | `RaidSetLootOption` |  |
| `0x76` | `0x2076` | `RaidSetMasterLooter` |  |
| `0x77` | `0x2077` | `MassivePvpInfoRequest` |  |
| `0x78` | `0x2078` | `SendTeamInfo` |  |
| `0x79` | `0x2079` | `GMRequestGuildInfoFromChar` |  |
| `0x7A` | `0x207A` | `QueueGroupFinder` |  |
| `0x7B` | `0x207B` | `SelectGroupFinderRoles` |  |
| `0x7C` | `0x207C` | `LeaveGroupFinder` |  |
| `0x7D` | `0x207D` | `ReadyGroupFinder` |  |
| `0x7E` | `0x207E` | `RequestRaidQueueInfo` |  |
| `0x7F` | `0x207F` | `UpdateDefaultRaid` |  |
| `0x80` | `0x2080` | `SetFeaturedRaid` |  |
| `0x81` | `0x2081` | `RequestFeaturedRaidNeededRole` |  |
| `0x82` | `0x2082` | `RequestNeededRole` |  |
| `0x83` | `0x2083` | `RequestRotatingActivityChildInfo` |  |
| `0x84` | `0x2084` | `GetWorldStat` |  |
| `0x85` | `0x2085` | `RequestItemSalesBalance` |  |
| `0x86` | `0x2086` | `RequestItemSalesCoupons` |  |
| `0xC5` | `0x20C5` | `EchoGroupFinderRemoved` | s2c DispatchGroupFinderRemoved (args: reason, silent) |
| `0xD0` | `0x20D0` | `EchoGroupFinderUpdateFeaturedActivities` | s2c DispatchGroupFinderUpdateFeaturedActivities (args: activities list) |

## CSPlayerInterfaceMethods

RPC class **CSPlayerInterface** (id `0xBBD560C8`) · S2C (server→client dispatch)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `DispatchAuthenticateAck` |  |
| `0x01` | `0x2001` | `DispatchTeamInvite` |  |
| `0x02` | `0x2002` | `DispatchTargetIsBusy` |  |
| `0x03` | `0x2003` | `DispatchTargetIsCriminal` |  |
| `0x04` | `0x2004` | `DispatchTeamCharacterApplied` |  |
| `0x05` | `0x2005` | `DispatchYouAlreadySentRequest` |  |
| `0x06` | `0x2006` | `DispatchTargetIsAlreadyInTeam` |  |
| `0x07` | `0x2007` | `DispatchTeamInvitationTimedOut` |  |
| `0x08` | `0x2008` | `DispatchTargetDeclinedTeamInvite` |  |
| `0x09` | `0x2009` | `DispatchYouAreNoLongerInvited` |  |
| `0x0A` | `0x200A` | `DispatchYouInvitedToTeam` |  |
| `0x0B` | `0x200B` | `DispatchInviteWithInviterFactionPoints` |  |
| `0x0C` | `0x200C` | `DispatchExtendedCharacterInfo` |  |
| `0x0D` | `0x200D` | `DispatchExtendedCharacterInfoNotFound` |  |
| `0x0E` | `0x200E` | `DispatchYouJoinedTeam` |  |
| `0x0F` | `0x200F` | `DispatchTeamSearchResult` |  |
| `0x10` | `0x2010` | `DispatchCharacterSearchResult` |  |
| `0x11` | `0x2011` | `DispatchCharacterNameSearchResult` |  |
| `0x12` | `0x2012` | `DispatchTeamLeave` |  |
| `0x13` | `0x2013` | `DispatchTeamJoin` |  |
| `0x14` | `0x2014` | `DispatchTeamMemberUpdate` |  |
| `0x15` | `0x2015` | `DispatchTeamDisband` |  |
| `0x16` | `0x2016` | `DispatchSetNewTeamLeader` |  |
| `0x17` | `0x2017` | `DispatchTeamLootOptionChanged` |  |
| `0x18` | `0x2018` | `DispatchTeamMasterLooterChanged` |  |
| `0x19` | `0x2019` | `DispatchTeamSummonRequest` |  |
| `0x1A` | `0x201A` | `DispatchTeamSummonTimedOut` |  |
| `0x1B` | `0x201B` | `DispatchYouAreNoLongerSummoned` |  |
| `0x1C` | `0x201C` | `DispatchTeamSummonRequestDeclined` |  |
| `0x1D` | `0x201D` | `DispatchTeamSummonRequestAccepted` |  |
| `0x1E` | `0x201E` | `DispatchTeamSummonRequestFailedInvalidPF` |  |
| `0x1F` | `0x201F` | `DispatchTeamSummonRequestFailedAlreadyInSameInstance` |  |
| `0x20` | `0x2020` | `DispatchWarningTeamSizeTooLarge` |  |
| `0x21` | `0x2021` | `Reserved0x21` |  |
| `0x22` | `0x2022` | `DispatchYouGotKickedFromTeam` |  |
| `0x23` | `0x2023` | `DispatchVoteKickStarted` |  |
| `0x24` | `0x2024` | `DispatchVoteKickFailed` |  |
| `0x25` | `0x2025` | `DispatchVoteKickEnded` |  |
| `0x26` | `0x2026` | `DispatchCharacterLookingForTeamStatus` |  |
| `0x27` | `0x2027` | `DispatchTeamLookingForMemberStatus` |  |
| `0x28` | `0x2028` | `DispatchDeleteCanceledMail` |  |
| `0x29` | `0x2029` | `DispatchDeleteCanceledMailItem` |  |
| `0x2A` | `0x202A` | `DispatchGuildInvite` |  |
| `0x2B` | `0x202B` | `Reserved0x2B` |  |
| `0x2C` | `0x202C` | `Reserved0x2C` |  |
| `0x2D` | `0x202D` | `Reserved0x2D` |  |
| `0x2E` | `0x202E` | `DispatchGuildMemberLeft` |  |
| `0x2F` | `0x202F` | `DispatchGuildMemberJoined` |  |
| `0x30` | `0x2030` | `DispatchGuildMemberUpdate` |  |
| `0x31` | `0x2031` | `DispatchGuildInviteReplyToInviter` |  |
| `0x32` | `0x2032` | `DispatchGuildGMPromoteFeedback` |  |
| `0x33` | `0x2033` | `DispatchGuildGMDemoteFeedback` |  |
| `0x34` | `0x2034` | `DispatchGuildInviteNoSuchName` |  |
| `0x35` | `0x2035` | `Reserved0x35` |  |
| `0x36` | `0x2036` | `DispatchGuildPvPPointInfo` |  |
| `0x37` | `0x2037` | `DispatchGuildRenownInfo` |  |
| `0x38` | `0x2038` | `DispatchGuildRankingsUpdate` |  |
| `0x39` | `0x2039` | `DispatchGuildRenownHistoryUpdate` |  |
| `0x3A` | `0x203A` | `DispatchClientAddGuildRenown` |  |
| `0x3B` | `0x203B` | `DispatchInformAboutRankings` |  |
| `0x3C` | `0x203C` | `DispatchInformAboutRankingWinners` |  |
| `0x3D` | `0x203D` | `DispatchRankingCaluculationCountdown` |  |
| `0x3E` | `0x203E` | `DispatchPersonalRenownCapReached` |  |
| `0x3F` | `0x203F` | `DispatchGuildUpdate` |  |
| `0x40` | `0x2040` | `DispatchGuildRenownUpdate` |  |
| `0x41` | `0x2041` | `DispatchGuildName` |  |
| `0x42` | `0x2042` | `DispatchGuildUpdateMember` |  |
| `0x43` | `0x2043` | `DispatchGuildUpdateOther` |  |
| `0x44` | `0x2044` | `DispatchGuildCreated` |  |
| `0x45` | `0x2045` | `DispatchGuildNonAlphanumericName` |  |
| `0x46` | `0x2046` | `DispatchGuildNameWrongLength` |  |
| `0x47` | `0x2047` | `DispatchGuildNameTaken` |  |
| `0x48` | `0x2048` | `DispatchGuildBadName` |  |
| `0x49` | `0x2049` | `DispatchGuildNameWaitTime` |  |
| `0x4A` | `0x204A` | `Reserved0x4A` |  |
| `0x4B` | `0x204B` | `Reserved0x4B` |  |
| `0x4C` | `0x204C` | `Reserved0x4C` |  |
| `0x4D` | `0x204D` | `Reserved0x4D` |  |
| `0x4E` | `0x204E` | `DispatchGuildOnInviteCooldown` |  |
| `0x4F` | `0x204F` | `Reserved0x4F` |  |
| `0x50` | `0x2050` | `DispatchGuildTaxError` |  |
| `0x51` | `0x2051` | `DispatchGuildInvalidGoverningForm` |  |
| `0x52` | `0x2052` | `DispatchGuildUpdateCash` |  |
| `0x53` | `0x2053` | `Reserved0x53` |  |
| `0x54` | `0x2054` | `DispatchGuildTaxChanged` |  |
| `0x55` | `0x2055` | `DispatchGuildGoverningFormChanged` |  |
| `0x56` | `0x2056` | `DispatchGuildRecruitmentModeChanged` |  |
| `0x57` | `0x2057` | `DispatchGuildMessageOfTheDayChanged` |  |
| `0x58` | `0x2058` | `Reserved0x58` |  |
| `0x59` | `0x2059` | `DispatchGuildNameChanged` |  |
| `0x5A` | `0x205A` | `DispatchGMGuildNameChanged` |  |
| `0x5B` | `0x205B` | `DispatchGuildMemberList` |  |
| `0x5C` | `0x205C` | `DispatchGuildSearchResult` |  |
| `0x5D` | `0x205D` | `DispatchGuildSetPvPPoints` |  |
| `0x5E` | `0x205E` | `DispatchGuildSetPvPKillPoints` |  |
| `0x5F` | `0x205F` | `DispatchGuildSetPvPTotalKills` |  |
| `0x60` | `0x2060` | `DispatchGuildSetPvPSeasonalKills` |  |
| `0x61` | `0x2061` | `DispatchGuildSetGuildWar` |  |
| `0x62` | `0x2062` | `DispatchGuildSetGuildAsDefenderOfWar` |  |
| `0x63` | `0x2063` | `DispatchAddGuildFeat` |  |
| `0x64` | `0x2064` | `DispatchRemoveGuildFeat` |  |
| `0x65` | `0x2065` | `DispatchGuildRankChanged` |  |
| `0x66` | `0x2066` | `DispatchGuildApplicationRequest` |  |
| `0x67` | `0x2067` | `Reserved0x67` |  |
| `0x68` | `0x2068` | `DispatchGuildApplicationRejected` |  |
| `0x69` | `0x2069` | `DispatchGuildMergeRequest` |  |
| `0x6A` | `0x206A` | `DispatchGuildMergeRequestFailed` |  |
| `0x6B` | `0x206B` | `DispatchGuildCityBooked` |  |
| `0x6C` | `0x206C` | `DispatchGuildCityBookingFailed` |  |
| `0x6D` | `0x206D` | `DispatchNewVulnerabilityWindowData` |  |
| `0x6E` | `0x206E` | `Reserved0x6E` |  |
| `0x6F` | `0x206F` | `DispatchGuildIsMassivePvPCityOwner` |  |
| `0x70` | `0x2070` | `DispatchGuildIsMassivePvPCityAttacker` |  |
| `0x71` | `0x2071` | `DispatchFeedbackMessage` |  |
| `0x72` | `0x2072` | `DispatchRequestBattleKeepInformation` |  |
| `0x73` | `0x2073` | `DispatchGMReplyGuildInfoFromChar` |  |
| `0x74` | `0x2074` | `Reserved0x74` |  |
| `0x75` | `0x2075` | `DispatchPvPMatchMakingWantsToStart` |  |
| `0x76` | `0x2076` | `DispatchPvPMatchMakingPlayerJoined` |  |
| `0x77` | `0x2077` | `DispatchPvPMatchMakingPlayerChangedSide` |  |
| `0x78` | `0x2078` | `DispatchPvPMatchMakingPlayerLeft` |  |
| `0x79` | `0x2079` | `DispatchPvPMatchMakingPlayerKicked` |  |
| `0x7A` | `0x207A` | `DispatchPvPMatchMakingErrorCode` |  |
| `0x7B` | `0x207B` | `DispatchPvPMatchMakingYouJoinedMatch` |  |
| `0x7C` | `0x207C` | `Reserved0x7C` |  |
| `0x7D` | `0x207D` | `DispatchPvPMatchMakingMatchInfo` |  |
| `0x7E` | `0x207E` | `DispatchPvPMatchMakingSearchResult` |  |
| `0x7F` | `0x207F` | `DispatchPvPMatchMakingStartClicked` |  |
| `0x80` | `0x2080` | `DispatchPvPMatchMakingYouAreInMatchMaking` |  |
| `0x81` | `0x2081` | `Reserved0x81` |  |
| `0x82` | `0x2082` | `Reserved0x82` |  |
| `0x83` | `0x2083` | `DispatchPvPMatchMakingMatchSay` |  |
| `0x84` | `0x2084` | `DispatchPvPMatchMakingCollectingMorePlayers` |  |
| `0x85` | `0x2085` | `DispatchPvPMatchMakingNewMatchLeader` |  |
| `0x86` | `0x2086` | `DispatchPvPMatchMakingTeamLeaderSignedUpForMatchMaking` |  |
| `0x87` | `0x2087` | `Reserved0x87` |  |
| `0x88` | `0x2088` | `Reserved0x88` |  |
| `0x89` | `0x2089` | `DispatchPvPMatchMakingCantStartMatchNotAllPlayersHaveSide` |  |
| `0x8A` | `0x208A` | `DispatchPvPMatchMakingCantLockToSideMustPickASideFirst` |  |
| `0x8B` | `0x208B` | `DispatchPvPMatchMakingCantChangeSideYouHaveBeenLockedToSide` |  |
| `0x8C` | `0x208C` | `DispatchPvPMatchMakingCharacterNowLockedToSide` |  |
| `0x8D` | `0x208D` | `DispatchPvPMatchMakingCharacterNoLongerLockedToSide` |  |
| `0x8E` | `0x208E` | `Reserved0x8E` |  |
| `0x8F` | `0x208F` | `Reserved0x8F` |  |
| `0x90` | `0x2090` | `Reserved0x90` |  |
| `0x91` | `0x2091` | `Reserved0x91` |  |
| `0x92` | `0x2092` | `DispatchPvPMatchMakingInfoRequestReturn` |  |
| `0x93` | `0x2093` | `DispatchYouJoinedRaid` |  |
| `0x94` | `0x2094` | `DispatchRaidTeamJoined` |  |
| `0x95` | `0x2095` | `DispatchRaidTeamLeft` |  |
| `0x96` | `0x2096` | `DispatchRaidTeamUpdate` |  |
| `0x97` | `0x2097` | `DispatchRaidCharacterUpdate` |  |
| `0x98` | `0x2098` | `DispatchRaidNewRaidLeader` |  |
| `0x99` | `0x2099` | `DispatchRaidLootOptionChanged` |  |
| `0x9A` | `0x209A` | `DispatchRaidNewMasterLooter` |  |
| `0x9B` | `0x209B` | `DispatchRaidInvite` |  |
| `0x9C` | `0x209C` | `DispatchRaidTargetIsBusy` |  |
| `0x9D` | `0x209D` | `DispatchRaidIsAlreadyInRaid` |  |
| `0x9E` | `0x209E` | `DispatchRaidInvitationTimedOut` |  |
| `0x9F` | `0x209F` | `DispatchRaidTargetDeclinedRaidInvite` |  |
| `0xA0` | `0x20A0` | `DispatchRaidYouAreNoLongerInvited` |  |
| `0xA1` | `0x20A1` | `Reserved0xA1` |  |
| `0xA2` | `0x20A2` | `DispatchRaidCantInviteInvalidSubscriptionFlags` |  |
| `0xA3` | `0x20A3` | `Reserved0xA3` |  |
| `0xA4` | `0x20A4` | `DispatchRaidCantCreateInvalidMemberSubscriptionFlags` |  |
| `0xA5` | `0x20A5` | `DispatchMassivePvpInfoRequestReturn` |  |
| `0xA6` | `0x20A6` | `DispatchMassivePvPBattleQueueFeedback` |  |
| `0xA7` | `0x20A7` | `DispatchMassivePvpStartCountDown` |  |
| `0xA8` | `0x20A8` | `DispatchMassivePvpBattleQueueStatusUpdate` |  |
| `0xA9` | `0x20A9` | `DispatchBattleKeepInfoRequestReturnPhase1And2` |  |
| `0xAA` | `0x20AA` | `DispatchBattleKeepInfoRequestReturnPhase3And4` |  |
| `0xAB` | `0x20AB` | `DispatchYouGotMailNotification` |  |
| `0xAC` | `0x20AC` | `Reserved0xAC` |  |
| `0xAD` | `0x20AD` | `DispatchDebugFullGameServerList` |  |
| `0xAE` | `0x20AE` | `DispatchDebugGameServerConnected` |  |
| `0xAF` | `0x20AF` | `DispatchDebugGameServerDisconnected` |  |
| `0xB0` | `0x20B0` | `DispatchDebugClientConnected` |  |
| `0xB1` | `0x20B1` | `DispatchDebugClientChanged` |  |
| `0xB2` | `0x20B2` | `DispatchDebugClientDisconnected` |  |
| `0xB3` | `0x20B3` | `DispatchDebugFullClientList` |  |
| `0xB4` | `0x20B4` | `DispatchDebugFullTeamListBurst` |  |
| `0xB5` | `0x20B5` | `DispatchDebugAddTeam` |  |
| `0xB6` | `0x20B6` | `DispatchDebugRemoveTeam` |  |
| `0xB7` | `0x20B7` | `DispatchDebugUpdateTeam` |  |
| `0xB8` | `0x20B8` | `DispatchDebugFullMatchListBurst` |  |
| `0xB9` | `0x20B9` | `DispatchDebugAddMatch` |  |
| `0xBA` | `0x20BA` | `DispatchDebugMatchChanged` |  |
| `0xBB` | `0x20BB` | `DispatchDebugRemoveMatch` |  |
| `0xBC` | `0x20BC` | `DispatchDebugAddIDToMatchMaking` |  |
| `0xBD` | `0x20BD` | `DispatchDebugRemoveIDFromMatchMaking` |  |
| `0xBE` | `0x20BE` | `DispatchItemSalesUpdateBalance` |  |
| `0xBF` | `0x20BF` | `DispatchItemSalesCoupons` |  |
| `0xC0` | `0x20C0` | `Reserved0xC0` |  |
| `0xC1` | `0x20C1` | `DispatchCSFeedback` |  |
| `0xC2` | `0x20C2` | `DispatchGroupFinderSignupFailed` |  |
| `0xC3` | `0x20C3` | `DispatchGroupFinderRoleSelect` |  |
| `0xC4` | `0x20C4` | `DispatchGroupFinderSignupSuccess` |  |
| `0xC5` | `0x20C5` | `DispatchGroupFinderRemoved` |  |
| `0xC6` | `0x20C6` | `DispatchGroupFinderReadyCheck` |  |
| `0xC7` | `0x20C7` | `DispatchVoteRetreatStarted` |  |
| `0xC8` | `0x20C8` | `DispatchGroupFinderActivityStarted` |  |
| `0xC9` | `0x20C9` | `DispatchGroupFinderReadyCheckStatus` |  |
| `0xCA` | `0x20CA` | `DispatchGroupFinderReadyCheckRejected` |  |
| `0xCB` | `0x20CB` | `DispatchGroupFinderPlayfieldInfo` |  |
| `0xCC` | `0x20CC` | `DispatchGroupFinderFeaturedPlayfieldInfo` |  |
| `0xCD` | `0x20CD` | `DispatchGroupFinderFeaturedRaidNeededRoleUpdated` |  |
| `0xCE` | `0x20CE` | `DispatchGroupFinderReceivedNeededRole` |  |
| `0xCF` | `0x20CF` | `DispatchGroupFinderReceivedRotatingActivityChildInfo` |  |
| `0xD0` | `0x20D0` | `DispatchGroupFinderUpdateFeaturedActivities` |  |
| `0xD1` | `0x20D1` | `DispatchReturnWorldStat` |  |
| `0xD2` | `0x20D2` | `DispatchInvalidWorldStat` |  |

## ChatServerInterfaceOpcodes

ChatServer chat — **no RpcHeader** (raw opcode) · S2C

| Opcode | Name | Note |
|--------|------|------|
| `0x0005` | `HelloAck` |  |
| `0x0014` | `ChatSenderInfo` | sender id + name; also sent before a ChannelChat so the client can show the speaker |
| `0x001E` | `Whisper` | incoming tell: [u32 senderCharId][string][00 01 00], preceded by 0x0014 sender info |
| `0x0022` | `CharacterChat` |  |
| `0x0023` | `SystemMessage` |  |
| `0x0025` | `FriendPresence` | [u64 0][u32 stamp][u16][u8][string name] - pushed BEFORE the add reply, by NAME |
| `0x0028` | `FriendAdded` | [u32 characterId][u8 online][u8 5][u32 stamp][u8 class] - the added friend's state |
| `0x0029` | `FriendRemoved` | [u32 characterId] - the removal echoed back |
| `0x003C` | `ChannelSubscription` |  |
| `0x003D` | `ChannelUnsubscribe` |  |
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

## GameAgentMethods

RPC class **GameAgent** (id `0xB171CE13`) · C2S (client→server call)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `PassBlob` |  |
| `0x01` | `0x2001` | `Ping` |  |
| `0x02` | `0x2002` | `ChatVicinity` |  |
| `0x03` | `0x2003` | `Authenticate2` |  |
| `0x04` | `0x2004` | `GetStats` |  |
| `0x05` | `0x2005` | `UpdateGameServerStats` |  |
| `0x06` | `0x2006` | `DataSink` |  |
| `0x07` | `0x2007` | `ClientDisconnected` |  |
| `0x08` | `0x2008` | `SaveFacebookToken` |  |
| `0x09` | `0x2009` | `RecruitFacebookFriends` |  |
| `0x0A` | `0x200A` | `UpdateFacebookFriends` |  |

## GameCharAgentMethods

RPC class **GameCharAgent** (id `0xA9ECB95D`) · C2S (client→server call)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `RequestAggroStatus` |  |
| `0x01` | `0x2001` | `RequestCharName` |  |
| `0x02` | `0x2002` | `RequestBindLocation` |  |
| `0x03` | `0x2003` | `SetTutorialStats` |  |
| `0x04` | `0x2004` | `UpdateCharGuildMemberInfo` |  |
| `0x05` | `0x2005` | `GuildInfoUpdate` |  |
| `0x06` | `0x2006` | `JoinedGuild` |  |
| `0x07` | `0x2007` | `LeftGuild` |  |
| `0x08` | `0x2008` | `LeaveTeam` |  |
| `0x09` | `0x2009` | `KickFromTeam` |  |
| `0x0A` | `0x200A` | `CreatedGuild` |  |
| `0x0B` | `0x200B` | `AddGuildFeat` |  |
| `0x0C` | `0x200C` | `RemoveGuildFeat` |  |
| `0x0D` | `0x200D` | `GuildGMPromote` |  |
| `0x0E` | `0x200E` | `GuildGMDemote` |  |
| `0x0F` | `0x200F` | `GuildTaxChanged` |  |
| `0x10` | `0x2010` | `GuildGoverningFormChanged` |  |
| `0x11` | `0x2011` | `GuildTransferMoney` |  |
| `0x12` | `0x2012` | `GuildWithdrawMoney` |  |
| `0x13` | `0x2013` | `GuildDisbanded` |  |
| `0x14` | `0x2014` | `GuildBookedCity` |  |
| `0x15` | `0x2015` | `GuildCityOwnershipChange` |  |
| `0x16` | `0x2016` | `GuildChangeRank` |  |
| `0x17` | `0x2017` | `GuildUpdateGuildBank` |  |
| `0x18` | `0x2018` | `SendGuildInfoToObservers` |  |
| `0x19` | `0x2019` | `GuildRankingsUpdate` |  |
| `0x1A` | `0x201A` | `ValidateGuildBadges` |  |
| `0x1B` | `0x201B` | `OpenGuildBank` |  |
| `0x1C` | `0x201C` | `CloseBank` |  |
| `0x1D` | `0x201D` | `SplitInventoryItem` |  |
| `0x1E` | `0x201E` | `CancelBuff` |  |
| `0x1F` | `0x201F` | `StackInventoryItemIfPossible` |  |
| `0x20` | `0x2020` | `UpdateQuestMarkerNPC` |  |
| `0x21` | `0x2021` | `MagicForgetSpell` |  |
| `0x22` | `0x2022` | `ResurrectMe` |  |
| `0x23` | `0x2023` | `CancelResurrectionRequest` |  |
| `0x24` | `0x2024` | `RespawnMe` |  |
| `0x25` | `0x2025` | `SignUpForWaveRespawn` |  |
| `0x26` | `0x2026` | `UpdateWaveRespawn` |  |
| `0x27` | `0x2027` | `CancelWaveRespawn` |  |
| `0x28` | `0x2028` | `RequestWaveRespawnTimerUpdate` |  |
| `0x29` | `0x2029` | `RespawnMeHere` |  |
| `0x2A` | `0x202A` | `GMRequestSpellData` |  |
| `0x2B` | `0x202B` | `RM_GetQuestFromQuestgiver` |  |
| `0x2C` | `0x202C` | `GMRequestFeatData` |  |
| `0x2D` | `0x202D` | `GMRequestFeatClassData` |  |
| `0x2E` | `0x202E` | `AdjustSkill` |  |
| `0x2F` | `0x202F` | `RequestPCDebugData` |  |
| `0x30` | `0x2030` | `GetLootDataForID` |  |
| `0x31` | `0x2031` | `SimulateLootForID` |  |
| `0x32` | `0x2032` | `CloseShop` |  |
| `0x33` | `0x2033` | `BuyItem` |  |
| `0x34` | `0x2034` | `SellItem` |  |
| `0x35` | `0x2035` | `UndoLastSoldItem` |  |
| `0x36` | `0x2036` | `FormationSetData` |  |
| `0x37` | `0x2037` | `FormationClaimPosition` |  |
| `0x38` | `0x2038` | `FormationClear` |  |
| `0x39` | `0x2039` | `FormationLeave` |  |
| `0x3A` | `0x203A` | `FormationUpdatePosRot` |  |
| `0x3B` | `0x203B` | `ScryMessageBoxAnswer` |  |
| `0x3C` | `0x203C` | `ScryOptionsBoxAnswer` |  |
| `0x3D` | `0x203D` | `ExitScenarioResults` |  |
| `0x3E` | `0x203E` | `FeatUntrainAnswer` |  |
| `0x3F` | `0x203F` | `CraftUntrain` |  |
| `0x40` | `0x2040` | `PlantCTFFlags` |  |
| `0x41` | `0x2041` | `UntrainFeat` |  |
| `0x42` | `0x2042` | `TryOpenLootBag` |  |
| `0x43` | `0x2043` | `TryLootAll` |  |
| `0x44` | `0x2044` | `CloseLootBag` |  |
| `0x45` | `0x2045` | `TryLootCash` |  |
| `0x46` | `0x2046` | `TryLootItem` |  |
| `0x47` | `0x2047` | `PassNeedGreedForItem` |  |
| `0x48` | `0x2048` | `DoNeedGreedRollForItem` |  |
| `0x49` | `0x2049` | `LootLockedItem` |  |
| `0x4A` | `0x204A` | `SlaveLooterAnswer` |  |
| `0x4B` | `0x204B` | `MasterLooterOffer` |  |
| `0x4C` | `0x204C` | `MasterLooterCancelOffer` |  |
| `0x4D` | `0x204D` | `SetPassOnLootLevel` |  |
| `0x4E` | `0x204E` | `UnbindItem` |  |
| `0x4F` | `0x204F` | `ShareQuest` |  |
| `0x50` | `0x2050` | `ShareQuestDeclined` |  |
| `0x51` | `0x2051` | `ShareQuestAccepted` |  |
| `0x52` | `0x2052` | `ShareQuestBusy` |  |
| `0x53` | `0x2053` | `RequestNextQuestPendingReward` |  |
| `0x54` | `0x2054` | `RequestTeamQuestList` |  |
| `0x55` | `0x2055` | `AddQuestList` |  |
| `0x56` | `0x2056` | `AcceptNextAwaitingQuest` |  |
| `0x57` | `0x2057` | `QuestManagerMsg` |  |
| `0x58` | `0x2058` | `StartQuickTeleport` |  |
| `0x59` | `0x2059` | `DoPaintballQuery` |  |
| `0x5A` | `0x205A` | `PerformSpecialAction` |  |
| `0x5B` | `0x205B` | `QueuePetCommand` |  |
| `0x5C` | `0x205C` | `DoSkillSellback` |  |
| `0x5D` | `0x205D` | `PetSetGroundTargetPos` |  |
| `0x5E` | `0x205E` | `PlayStockphraseAsSubtitle` |  |
| `0x5F` | `0x205F` | `CinematicTransition` |  |
| `0x60` | `0x2060` | `RequestACGTeleport` |  |
| `0x61` | `0x2061` | `RequestClearOwnACG` |  |
| `0x62` | `0x2062` | `RequestDropTeamBind` |  |
| `0x63` | `0x2063` | `DebugSetOceanData` |  |
| `0x64` | `0x2064` | `RequestTrade` |  |
| `0x65` | `0x2065` | `StartTrade` |  |
| `0x66` | `0x2066` | `DeclineTrade` |  |
| `0x67` | `0x2067` | `SetTradeCash` |  |
| `0x68` | `0x2068` | `AddTradeItem` |  |
| `0x69` | `0x2069` | `RemoveTradeItem` |  |
| `0x6A` | `0x206A` | `MoveTradeItemInternal` |  |
| `0x6B` | `0x206B` | `AcceptTrade` |  |
| `0x6C` | `0x206C` | `AbortTrade` |  |
| `0x6D` | `0x206D` | `RequestShortcutBar` |  |
| `0x6E` | `0x206E` | `AddShortcut` |  |
| `0x6F` | `0x206F` | `UpdateShortcut` |  |
| `0x70` | `0x2070` | `MoveShortcut` |  |
| `0x71` | `0x2071` | `RemoveShortcut` |  |
| `0x72` | `0x2072` | `StartMinigame` |  |
| `0x73` | `0x2073` | `NewMinigamePlayer` |  |
| `0x74` | `0x2074` | `DropMinigamePlayer` |  |
| `0x75` | `0x2075` | `ResendMinigameInfoToPlayer` |  |
| `0x76` | `0x2076` | `FlagAsCoward` |  |
| `0x77` | `0x2077` | `ForceStopMinigame` |  |
| `0x78` | `0x2078` | `ForceSetMinigameTime` |  |
| `0x79` | `0x2079` | `PrepareMinigameTime` |  |
| `0x7A` | `0x207A` | `PrepareToMinigameTime` |  |
| `0x7B` | `0x207B` | `GetInstanceListForCurrentPlayfield` |  |
| `0x7C` | `0x207C` | `ShiftInstanceForCurrentPlayfield` |  |
| `0x7D` | `0x207D` | `EnableHeroicMode` |  |
| `0x7E` | `0x207E` | `SetHideInspectOption` |  |
| `0x7F` | `0x207F` | `SetPlayfieldInstanceList` |  |
| `0x80` | `0x2080` | `InstanceWillResetOnShutdown` |  |
| `0x81` | `0x2081` | `InstanceHasBeenLocked` |  |
| `0x82` | `0x2082` | `InstanceHasBeenUnLocked` |  |
| `0x83` | `0x2083` | `RequestTeamMemberPosList` |  |
| `0x84` | `0x2084` | `TradepostSearchResult` |  |
| `0x85` | `0x2085` | `Ping` |  |
| `0x86` | `0x2086` | `ShiftTeamInstance` |  |
| `0x87` | `0x2087` | `SubscriptionFlagsUpdated` |  |
| `0x88` | `0x2088` | `PlayerServicesUpdated` |  |
| `0x89` | `0x2089` | `PassiveAbilityEquip` |  |
| `0x8A` | `0x208A` | `PassiveAbilityUnequip` |  |
| `0x8B` | `0x208B` | `ExitPassiveMode` |  |
| `0x8C` | `0x208C` | `EnterPassiveMode` |  |
| `0x8D` | `0x208D` | `RequestMassivePvPScoreData` |  |
| `0x8E` | `0x208E` | `RequestScorePlayerNames` |  |
| `0x8F` | `0x208F` | `SetVulnerabilityWindowStartTime` |  |
| `0x90` | `0x2090` | `RequestRecipeInfo` |  |
| `0x91` | `0x2091` | `RequestTeamInvite` |  |
| `0x92` | `0x2092` | `RequestNewJoinHasHostileInFriends` |  |
| `0x93` | `0x2093` | `RequestTeamInviteWithInviterFactionPoints` |  |
| `0x94` | `0x2094` | `RequestItemSalesData` |  |
| `0x95` | `0x2095` | `RequestItemSaleLimitedItemList` |  |
| `0x96` | `0x2096` | `RequestItemSaleFavoriteData` |  |
| `0x97` | `0x2097` | `SetFakeBillingConnection` |  |
| `0x98` | `0x2098` | `AddFavoriteItem` |  |
| `0x99` | `0x2099` | `RemoveFavoriteItem` |  |
| `0x9A` | `0x209A` | `BuyItemSalesItem` |  |
| `0x9B` | `0x209B` | `CSBuyItemReply` |  |
| `0x9C` | `0x209C` | `CSRollbackResponse` |  |
| `0x9D` | `0x209D` | `ExecuteChatCommand` |  |
| `0x9E` | `0x209E` | `ReplyUpdatedDimensionDB` |  |
| `0x9F` | `0x209F` | `RequestUseTeleportSpell` |  |
| `0xA0` | `0x20A0` | `UnlockFromPlayfield` |  |
| `0xA1` | `0x20A1` | `UpdateMailBank` |  |
| `0xA2` | `0x20A2` | `SetSelectedTag` |  |
| `0xA3` | `0x20A3` | `SetSelectedBadgeTag` |  |
| `0xA4` | `0x20A4` | `KickPlayerFromGroupPlayfield` |  |
| `0xA5` | `0x20A5` | `WorldStatChanged` |  |
| `0xA6` | `0x20A6` | `UpdateNoXPGainStat` |  |
| `0xA7` | `0x20A7` | `SendReport` |  |
| `0xA8` | `0x20A8` | `RunLua` |  |

## GameCharAgentProjectMethods

RPC class **GameCharAgentProject** (id `0x5B9756FC`) · C2S (client→server call)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `RequestAggroStatus` |  |
| `0x01` | `0x2001` | `GetProcDataForID` |  |
| `0x02` | `0x2002` | `AcceptApprenticeTeacher` |  |
| `0x03` | `0x2003` | `DeclineApprenticeTeacher` |  |
| `0x04` | `0x2004` | `RequestApprenticeStudent` |  |
| `0x05` | `0x2005` | `LeaveApprenticeMode` |  |
| `0x06` | `0x2006` | `RequestHitAndMitigationTables` |  |
| `0x07` | `0x2007` | `StartOfflineTrainingSystem` |  |
| `0x08` | `0x2008` | `ResetOfflineTrainingSystem` |  |
| `0x09` | `0x2009` | `RefreshEquippedAbilities` |  |
| `0x0A` | `0x200A` | `EnableRangerTracking` |  |
| `0x0B` | `0x200B` | `ToggleVanityArmorFilter` |  |
| `0x0C` | `0x200C` | `SetSprintInputState` |  |

## GameCharInterfaceMethods

RPC class **GameCharInterface** (id `0x8B87F791`) · S2C (server→client dispatch)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `DispatchUpdateAggroStatus` |  |
| `0x01` | `0x2001` | `DispatchUpdateName` |  |
| `0x02` | `0x2002` | `DispatchUpdateBindLocation` |  |
| `0x03` | `0x2003` | `DispatchGuildNotEnoughCash` |  |
| `0x04` | `0x2004` | `Reserved0x04` |  |
| `0x05` | `0x2005` | `DispatchUpdateCash` |  |
| `0x06` | `0x2006` | `DispatchUpdateCashRepository` |  |
| `0x07` | `0x2007` | `DispatchMoveInventoryItemToOrFromGuildBank` |  |
| `0x08` | `0x2008` | `DispatchSetQuestMarker` |  |
| `0x09` | `0x2009` | `DispatchChangeItemStackSize` |  |
| `0x0A` | `0x200A` | `DispatchLearnSpell` |  |
| `0x0B` | `0x200B` | `DispatchForgetSpell` |  |
| `0x0C` | `0x200C` | `Reserved0x0C` |  |
| `0x0D` | `0x200D` | `DispatchTradeskillComplete` |  |
| `0x0E` | `0x200E` | `Reserved0x0E` |  |
| `0x0F` | `0x200F` | `DispatchClearRecipe` |  |
| `0x10` | `0x2010` | `DispatchRemoveRecipesByGroupClient` |  |
| `0x11` | `0x2011` | `DispatchUntrainSingleFeat` |  |
| `0x12` | `0x2012` | `DispatchDisplayItemLootChatMessage` |  |
| `0x13` | `0x2013` | `Reserved0x13` |  |
| `0x14` | `0x2014` | `DispatchSendNPCAutoTradeCashAmount` |  |
| `0x15` | `0x2015` | `DispatchSetDialogueCamera` |  |
| `0x16` | `0x2016` | `DispatchAddTag` |  |
| `0x17` | `0x2017` | `DispatchRemoveTag` |  |
| `0x18` | `0x2018` | `DispatchResurrectRequest` |  |
| `0x19` | `0x2019` | `DispatchSetDbDebugCategory` |  |
| `0x1A` | `0x201A` | `DispatchSetDbDebugLevel` |  |
| `0x1B` | `0x201B` | `Reserved0x1B` |  |
| `0x1C` | `0x201C` | `DispatchSetItemStat` |  |
| `0x1D` | `0x201D` | `DispatchAddToItemSlot` |  |
| `0x1E` | `0x201E` | `DispatchRemoveFromItemSlot` |  |
| `0x1F` | `0x201F` | `DispatchRemoveItem` |  |
| `0x20` | `0x2020` | `DispatchAddItem` |  |
| `0x21` | `0x2021` | `DispatchMoveInventoryItemToGuildBank` |  |
| `0x22` | `0x2022` | `DispatchMoveInventoryItemFromGuildBank` |  |
| `0x23` | `0x2023` | `DispatchCharacterNameReply` |  |
| `0x24` | `0x2024` | `DispatchValidResurrectionSpots` |  |
| `0x25` | `0x2025` | `Reserved0x25` |  |
| `0x26` | `0x2026` | `DispatchReplyPCDebugData` |  |
| `0x27` | `0x2027` | `DispatchReplyLootForCharacter` |  |
| `0x28` | `0x2028` | `DispatchReplyLootForCharacterAddHash` |  |
| `0x29` | `0x2029` | `DispatchReplyLootForCharacterTemplateClusterOk` |  |
| `0x2A` | `0x202A` | `DispatchReplyLootForCharacterTemplateClusterError` |  |
| `0x2B` | `0x202B` | `DispatchReplyLootForCharacterAddTreasureTemplate` |  |
| `0x2C` | `0x202C` | `DispatchReplySimulateLootForID` |  |
| `0x2D` | `0x202D` | `DispatchFormationSetData` |  |
| `0x2E` | `0x202E` | `DispatchFormationClaimPosition` |  |
| `0x2F` | `0x202F` | `DispatchFormationUpdatePosition` |  |
| `0x30` | `0x2030` | `DispatchFormationClearPosition` |  |
| `0x31` | `0x2031` | `Reserved0x31` |  |
| `0x32` | `0x2032` | `DispatchFormationUpdatePosRot` |  |
| `0x33` | `0x2033` | `DispatchShopData` |  |
| `0x34` | `0x2034` | `DispatchCloseShop` |  |
| `0x35` | `0x2035` | `DispatchItemSoldToShop` |  |
| `0x36` | `0x2036` | `DispatchItemBoughtBack` |  |
| `0x37` | `0x2037` | `DispatchOpenLootBag` |  |
| `0x38` | `0x2038` | `DispatchCloseLootBag` |  |
| `0x39` | `0x2039` | `DispatchDoNeedGreedRollForItemsInLootBag` |  |
| `0x3A` | `0x203A` | `DispatchCancelNeedGreedRollForItemsInLootBag` |  |
| `0x3B` | `0x203B` | `DispatchOpenDialogForLockedItemsInLootBag` |  |
| `0x3C` | `0x203C` | `DispatchCloseDialogForLockedItemsInLootBag` |  |
| `0x3D` | `0x203D` | `DispatchMasterLooterOfferItemTo` |  |
| `0x3E` | `0x203E` | `DispatchSlaveLooterAnswer` |  |
| `0x3F` | `0x203F` | `DispatchMasterLooterCancelOffer` |  |
| `0x40` | `0x2040` | `DispatchOverrideGameTweaks` |  |
| `0x41` | `0x2041` | `DispatchShowClientFIFO` |  |
| `0x42` | `0x2042` | `DispatchAddFormation` |  |
| `0x43` | `0x2043` | `DispatchRemoveFormation` |  |
| `0x44` | `0x2044` | `DispatchUpgradeCombo` |  |
| `0x45` | `0x2045` | `DispatchUpgradeComboStep` |  |
| `0x46` | `0x2046` | `DispatchXPSelectClass` |  |
| `0x47` | `0x2047` | `DispatchNextQuestAvailable` |  |
| `0x48` | `0x2048` | `DispatchQuestShareGlobalFeedback` |  |
| `0x49` | `0x2049` | `DispatchQuestShareIndividualFeedback` |  |
| `0x4A` | `0x204A` | `DispatchQuestShareRequest` |  |
| `0x4B` | `0x204B` | `DispatchUpdateTeamQuestList` |  |
| `0x4C` | `0x204C` | `DispatchCancelTeamQuestList` |  |
| `0x4D` | `0x204D` | `DispatchUpdateTokenAmount` |  |
| `0x4E` | `0x204E` | `DispatchLockTemplate` |  |
| `0x4F` | `0x204F` | `DispatchApplyFactionRank` |  |
| `0x50` | `0x2050` | `DispatchApplyFactionPoints` |  |
| `0x51` | `0x2051` | `Reserved0x51` |  |
| `0x52` | `0x2052` | `DispatchAddSpecialAbility` |  |
| `0x53` | `0x2053` | `DispatchRemoveSpecialAbility` |  |
| `0x54` | `0x2054` | `DispatchPerformSpecialAction` |  |
| `0x55` | `0x2055` | `DispatchQueuePetCommand` |  |
| `0x56` | `0x2056` | `DispatchACGTeleportChoice` |  |
| `0x57` | `0x2057` | `DispatchACGSelfInstanceCleared` |  |
| `0x58` | `0x2058` | `DispatchACGTeamBindChanged` |  |
| `0x59` | `0x2059` | `Reserved0x59` |  |
| `0x5A` | `0x205A` | `DispatchOverrideJumpHeight` |  |
| `0x5B` | `0x205B` | `DispatchOverrideJumpSpeed` |  |
| `0x5C` | `0x205C` | `DispatchVisualSimulateNpc` |  |
| `0x5D` | `0x205D` | `DispatchReceivedStockPhraseText` |  |
| `0x5E` | `0x205E` | `DispatchScryShowMessageBox` |  |
| `0x5F` | `0x205F` | `DispatchScryCloseMessageBox` |  |
| `0x60` | `0x2060` | `DispatchScryShowOptionsBox` |  |
| `0x61` | `0x2061` | `DispatchScryCloseOptionsBox` |  |
| `0x62` | `0x2062` | `DispatchOpenScenarioResults` |  |
| `0x63` | `0x2063` | `DispatchScrySendCustomGUIData` |  |
| `0x64` | `0x2064` | `DispatchFeatUntrainMessageBox` |  |
| `0x65` | `0x2065` | `DispatchCraftUntrainMessageBox` |  |
| `0x66` | `0x2066` | `DispatchUnbindItemReply` |  |
| `0x67` | `0x2067` | `DispatchBroadcastMessage` |  |
| `0x68` | `0x2068` | `DispatchRequestTrade` |  |
| `0x69` | `0x2069` | `DispatchStartTrade` |  |
| `0x6A` | `0x206A` | `DispatchTradeAbort` |  |
| `0x6B` | `0x206B` | `DispatchAcceptedTrade` |  |
| `0x6C` | `0x206C` | `DispatchPartnerAcceptedTrade` |  |
| `0x6D` | `0x206D` | `DispatchTradeCompleted` |  |
| `0x6E` | `0x206E` | `DispatchPartnerUpdatedCash` |  |
| `0x6F` | `0x206F` | `DispatchTradePartnerAddedItem` |  |
| `0x70` | `0x2070` | `DispatchTradeAddedItem` |  |
| `0x71` | `0x2071` | `DispatchUnacceptedTrade` |  |
| `0x72` | `0x2072` | `Reserved0x72` | #792: fires the trade-window refresh signal and nothing else. Never sent in the corpus. |
| `0x73` | `0x2073` | `DispatchTradePartnerRemovedItem` |  |
| `0x74` | `0x2074` | `DispatchTradeRemovedItem` |  |
| `0x75` | `0x2075` | `DispatchDeleteCachedLDBData` |  |
| `0x76` | `0x2076` | `DispatchShortcutBarReply` |  |
| `0x77` | `0x2077` | `Reserved0x77` |  |
| `0x78` | `0x2078` | `Reserved0x78` |  |
| `0x79` | `0x2079` | `Reserved0x79` |  |
| `0x7A` | `0x207A` | `DispatchAddWayPoint` |  |
| `0x7B` | `0x207B` | `DispatchRemoveWayPoint` |  |
| `0x7C` | `0x207C` | `DispatchReplyWaveRespawnTimerUpdate` |  |
| `0x7D` | `0x207D` | `DispatchGMReplySpellData` |  |
| `0x7E` | `0x207E` | `DispatchGMReplyFeatData` |  |
| `0x7F` | `0x207F` | `DispatchGMReplyFeatClassData` |  |
| `0x80` | `0x2080` | `DispatchInstanceSelectionForCurrentPlayfield` |  |
| `0x81` | `0x2081` | `DispatchApprenticeModeOnOff` |  |
| `0x82` | `0x2082` | `DispatchPong` |  |
| `0x83` | `0x2083` | `DispatchUpdateTeamMemberPosList` |  |
| `0x84` | `0x2084` | `DispatchDynamicSpaceDebugInfo` |  |
| `0x85` | `0x2085` | `DispatchSetGameTweakValue` |  |
| `0x86` | `0x2086` | `Reserved0x86` |  |
| `0x87` | `0x2087` | `DispatchMinigameScoreChanged` |  |
| `0x88` | `0x2088` | `DispatchMinigameFullScore` |  |
| `0x89` | `0x2089` | `Reserved0x89` |  |
| `0x8A` | `0x208A` | `DispatchMinigameMatchStarted` |  |
| `0x8B` | `0x208B` | `DispatchMinigameMatchEnded` |  |
| `0x8C` | `0x208C` | `DispatchMinigameCaptureZoneUpdate` |  |
| `0x8D` | `0x208D` | `DispatchMinigameCaptureZoneList` |  |
| `0x8E` | `0x208E` | `DispatchMinigameCaptureZoneLeaveEnter` |  |
| `0x8F` | `0x208F` | `DispatchForceStopMinigame` |  |
| `0x90` | `0x2090` | `DispatchSetMinigameTime` |  |
| `0x91` | `0x2091` | `DispatchMinigameStartsInXSeconds` |  |
| `0x92` | `0x2092` | `Reserved0x92` |  |
| `0x93` | `0x2093` | `DispatchMassivePvPFullScoreUpdate` |  |
| `0x94` | `0x2094` | `DispatchMassivePvPGuildScoreUpdate` |  |
| `0x95` | `0x2095` | `DispatchMassivePvPPlayerScoreUpdate` |  |
| `0x96` | `0x2096` | `DispatchMassivePvPScoreNames` |  |
| `0x97` | `0x2097` | `Reserved0x97` |  |
| `0x98` | `0x2098` | `Reserved0x98` |  |
| `0x99` | `0x2099` | `DispatchShowVulnerabilityWindowGui` |  |
| `0x9A` | `0x209A` | `DispatchPassiveAbilityEquip` |  |
| `0x9B` | `0x209B` | `DispatchPassiveAbilityUnequip` |  |
| `0x9C` | `0x209C` | `DispatchReplyRecipeInfo` |  |
| `0x9D` | `0x209D` | `DispatchAddEffectPackage` |  |
| `0x9E` | `0x209E` | `DispatchEnterExitGamingRoom` |  |
| `0x9F` | `0x209F` | `DispatchReplyTeamInvite` |  |
| `0xA0` | `0x20A0` | `DispatchReplyNewJoinHasHostileInFriends` |  |
| `0xA1` | `0x20A1` | `Reserved0xA1` |  |
| `0xA2` | `0x20A2` | `DispatchReplyItemForCashSaleSetData` |  |
| `0xA3` | `0x20A3` | `DispatchReplyItemForCashSaleUpdateData` |  |
| `0xA4` | `0x20A4` | `DispatchItemSaleResult` |  |
| `0xA5` | `0x20A5` | `DispatchReplyItemSaleFavoriteData` |  |
| `0xA6` | `0x20A6` | `DispatchItemSalesUpdateBalance` |  |
| `0xA7` | `0x20A7` | `DispatchPlayfieldAccessDenied` |  |
| `0xA8` | `0x20A8` | `DispatchPlayfieldAccessGranted` |  |
| `0xA9` | `0x20A9` | `DispatchSendPlayfieldAccessData` |  |
| `0xAA` | `0x20AA` | `DispatchSendPlayfieldLockoutData` |  |
| `0xAB` | `0x20AB` | `DispatchLockAllSpells` |  |
| `0xAC` | `0x20AC` | `DispatchAddSpellResistance` |  |
| `0xAD` | `0x20AD` | `DispatchRemoveSpellResistance` |  |
| `0xAE` | `0x20AE` | `DispatchSetStackinglineResists` |  |
| `0xAF` | `0x20AF` | `DispatchPlayerServicesUpdated` |  |
| `0xB0` | `0x20B0` | `DispatchOnFacebookTokenResult` |  |
| `0xB1` | `0x20B1` | `DispatchOnUpdateFacebookFriends` |  |
| `0xB2` | `0x20B2` | `DispatchOnFacebookFriendSignedUp` |  |
| `0xB3` | `0x20B3` | `DispatchRequestGetEffect` |  |
| `0xB4` | `0x20B4` | `DispatchSignalWorldStatChanged` |  |

## GameCharInterfaceProjectMethods

RPC class **GameCharInterfaceProject** (id `0x28377648`) · C2S (client→server call)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `DispatchUpdateAggroStatus` |  |
| `0x01` | `0x2001` | `DispatchGuildBankData` |  |
| `0x02` | `0x2002` | `Reserved0x02` |  |
| `0x03` | `0x2003` | `DispatchRequestApprentice` |  |
| `0x04` | `0x2004` | `DispatchAddApprenticeEntry` |  |
| `0x05` | `0x2005` | `DispatchRemoveApprenticeEntry` |  |
| `0x06` | `0x2006` | `DispatchDoYouWantToShiftTeamInstance` |  |
| `0x07` | `0x2007` | `DispatchInDungeonPlayfield` |  |
| `0x08` | `0x2008` | `DispatchReplyDebugGetProcData` |  |
| `0x09` | `0x2009` | `DispatchReplyDebugGetSpellProcData` |  |
| `0x0A` | `0x200A` | `DispatchSendHitAndMitigationTable` |  |
| `0x0B` | `0x200B` | `DispatchSetIsSprinting` |  |
| `0x0C` | `0x200C` | `DispatchSyncCanSprint` |  |
| `0x0D` | `0x200D` | `DispatchSyncRegenDelayed` |  |
| `0x0E` | `0x200E` | `DispatchUpdateSprintBar` |  |
| `0x0F` | `0x200F` | `DispatchOpenSiteInBrowserOnClient` |  |

## GameInterfaceMethods

RPC class **GameInterface** (id `0x6C67C147`) · S2C (server→client dispatch)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `DispatchPassBlob` |  |
| `0x01` | `0x2001` | `DispatchAckAuthenticate` |  |
| `0x02` | `0x2002` | `DispatchRedirectToServer` |  |
| `0x03` | `0x2003` | `Reserved0x03` |  |
| `0x04` | `0x2004` | `DispatchQuitGame` | GameInterface_t::OnQuitGame (#792) |
| `0x05` | `0x2005` | `DispatchPong` |  |
| `0x06` | `0x2006` | `DispatchPongWithPacketTiming` |  |
| `0x07` | `0x2007` | `DispatchInfoToUser` |  |
| `0x08` | `0x2008` | `DispatchGameStats` |  |
| `0x09` | `0x2009` | `DispatchGameServerStats` |  |
| `0x0A` | `0x200A` | `DispatchGameServerDataset` |  |
| `0x0B` | `0x200B` | `DispatchReportDimesionID` |  |
| `0x0C` | `0x200C` | `DispatchReportServerID` |  |
| `0x0D` | `0x200D` | `DispatchDataSink` |  |
| `0x0E` | `0x200E` | `Reserved0x0E` |  |
| `0x0F` | `0x200F` | `DispatchDisplayPlayfieldAccessPopup` |  |

## IirTypes

The PassBlob-carrier IIR type sub-opcodes (32-bit BKDR-31 hashes of the client IIR type names).
Catalogued in [`iir-types.md`](iir-types.md).

## PlayerAgentMethods

RPC class **PlayerAgent** (id `0x82F20484`) · C2S (client→server call)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x01035784` | `0x2084` | `UpdateCharacterSettings` |  |
| `0x1B2CF268` | `0x20E8` | `RenameCharacter` |  |
| `0x2976E1B7` | `0x20B7` | `AddOfflineLevel` |  |
| `0x3A96B2B3` | `0x20B3` | `UpdateFacebookFriends` |  |
| `0x3C7C926C` | `0x20EC` | `ReceivedCharacterSettings` |  |
| `0x43476328` | `0x20A8` | `GetDimensionList` |  |
| `0x43D90C1B` | `0x209B` | `ClientReplyKeepCookie` |  |
| `0x4D661656` | `0x20D6` | `UpdateCharacterPreview` |  |
| `0x516C9EF5` | `0x20F5` | `UpdateSubscriptionSettings` |  |
| `0x5B51044B` | `0x20CB` | `RecruitFacebookFriends` |  |
| `0x6A546D41` | `0x20C1` | `GetStartupData` |  |
| `0x6A6B621C` | `0x209C` | `SetHardcoreLevel` |  |
| `0x9847AED6` | `0x20D6` | `VerifyLanguageSetting` |  |
| `0x9C370D1E` | `0x209E` | `UnlockCharacter` |  |
| `0x9CB2CB03` | `0x2083` | `Authenticate` |  |
| `0x9D991E6C` | `0x20EC` | `AuthenticateLoggedIn` |  |
| `0xBE735486` | `0x2086` | `CreateCharacter` |  |
| `0xC3C5C31D` | `0x209D` | `RequestDeleteCharacter` |  |
| `0xCA2C4E5E` | `0x20DE` | `CreateCharacterWithData` |  |
| `0xCBDE126E` | `0x20EE` | `LoginFromCrash` |  |
| `0xD3907026` | `0x20A6` | `LoadFacebookToken` |  |
| `0xD8AD362B` | `0x20AB` | `SaveFacebookToken` |  |
| `0xDBE172E5` | `0x20E5` | `RequestSuggestNickname` |  |
| `0xDFD8518E` | `0x208E` | `FinishGetStartupData` |  |
| `0xEF616EB6` | `0x20B6` | `LoginCharacter` |  |

## PlayerInterfaceMethods

RPC class **PlayerInterface** (id `0x7D155738`) · S2C (server→client dispatch)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00C46317` | `0x2097` | `VerifyClientStillConnected` |  |
| `0x0C09CA25` | `0x20A5` | `SendCharacterSettingsCheckSumMap` | was "SmallCharList" |
| `0x0FE09F03` | `0x2083` | `LoginQueuePosition` |  |
| `0x1562CE84` | `0x2084` | `HardcoreLevelSet` |  |
| `0x15F7AF22` | `0x20A2` | `SendSubscriptionSettings` |  |
| `0x206B6EE5` | `0x20E5` | `SendCharacterPreview` | was "Ox20E5" |
| `0x233605B9` | `0x20B9` | `SendCharacterSettings` | was "Ox20B9" |
| `0x23A632FA` | `0x20FA` | `ChatServerConnectReady` |  |
| `0x3E8F0623` | `0x20A3` | `OfflineLevelApplied` |  |
| `0x4F91A58C` | `0x208C` | `PlayerSetupComplete` | was "SendCharacterRealmData" |
| `0x52454C9C` | `0x209C` | `OnFacebookTokenResult` | was "Ox209C" - the reply to c2s LoadFacebookToken |
| `0x5AED2A60` | `0x20E0` | `CSServerConnectReady` |  |
| `0x5CE97913` | `0x2093` | `CharacterUnlocked` | was "Ox2093" - the reply to c2s UnlockCharacter |
| `0x5DC18991` | `0x2091` | `AuthenticateAck` | was "InitAuth" |
| `0x7955BD80` | `0x2080` | `OnFacebookFriendSignedUp` |  |
| `0x7C47F763` | `0x20E3` | `OverrideGameTweaks` |  |
| `0x88750390` | `0x2090` | `OnUpdateFacebookFriends` |  |
| `0x9625EB4A` | `0x20CA` | `CharacterNeedsNewName` |  |
| `0xA7C86A29` | `0x20A9` | `SetStats` |  |
| `0xC414C5EF` | `0x20EF` | `UpdateClientPlayerData` | was "SendCharacterList" |
| `0xD4063CA0` | `0x20A0` | `LoginProblem` |  |
| `0xD4C59816` | `0x2096` | `ResetLanguage` |  |
| `0xD8B66C79` | `0x20F9` | `LoginConnectReady` |  |
| `0xE33EBAE1` | `0x20E1` | `TeleportProblem` |  |
| `0xE8EAD4F8` | `0x20F8` | `SuggestNicknameResult` |  |
| `0xF899B14C` | `0x20CC` | `SetDimensionList` | was "SendRealmList" (a dimension IS a realm) |

## TradepostAgentMethods

RPC class **TradepostAgent** (id `0x3D744981`) · C2S (client→server call)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `GetAllMails` | the mailbox poll |
| `0x01` | `0x2001` | `GetMailByID` | body [mailId:u64] |
| `0x02` | `0x2002` | `SendMail` |  |
| `0x03` | `0x2003` | `MarkMailRead` | also observed for the delete-side flow |
| `0x04` | `0x2004` | `CheckNewMail` |  |
| `0x05` | `0x2005` | `GetMailItems` |  |
| `0x06` | `0x2006` | `GetMailItem` | body [mailId][itemId] |
| `0x07` | `0x2007` | `GetItemData` | observed with a [mailId]-style body (was decoded as "GetMailItems") |
| `0x08` | `0x2008` | `DeleteMail` |  |
| `0x09` | `0x2009` | `CancelMail` |  |
| `0x0A` | `0x200A` | `TradepostSellItem` |  |
| `0x0B` | `0x200B` | `TradepostChangeItemPrice` |  |
| `0x0C` | `0x200C` | `TradepostCancelSellItem` |  |
| `0x0D` | `0x200D` | `ItemSearch` | TradepostItemSearch (query body) |
| `0x0E` | `0x200E` | `BuyItem` | TradepostBuyItem [saleId:u64][searchResultId:u64] |
| `0x0F` | `0x200F` | `TradepostOpen` | not observed in captures - the client opens with UpdateItemTimeouts instead |
| `0x10` | `0x2010` | `Close` | TradepostClose (empty body) |
| `0x11` | `0x2011` | `UpdateItemTimeouts` | TradepostUpdateItemTimeouts - sent (empty body) when the AH window opens |
| `0x12` | `0x2012` | `SetAsyncEnabled` |  |
| `0x13` | `0x2013` | `SetNumWorkerThreads` |  |

## TradepostInterfaceMethods

RPC class **TradepostInterface** (id `0xF2C2D5B5`) · S2C (server→client dispatch)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `DispatchOpenTradepost` |  |
| `0x01` | `0x2001` | `DispatchClose` | no-arg; the string catalog missed it (recovered from the TradepostInterface RE) |
| `0x02` | `0x2002` | `DispatchMailMessage` |  |
| `0x03` | `0x2003` | `DispatchSendMailResult` |  |
| `0x04` | `0x2004` | `DispatchRetrieveMailResult` |  |
| `0x05` | `0x2005` | `DispatchTradepostSearchResults` |  |
| `0x06` | `0x2006` | `DispatchDeleteCanceledMail` |  |
| `0x07` | `0x2007` | `DispatchDeleteCanceledMailItem` |  |

## UniverseAgentMethods

RPC class **UniverseAgent** (id `0x604DDBA0`) · C2S (client→server call)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `InitiateAuthentication` |  |
| `0x01` | `0x2001` | `AnswerChallenge` |  |
| `0x02` | `0x2002` | `ClientDisconnected` |  |

## UniverseInterfaceMethods

RPC class **UniverseInterface** (id `0x0C384054`) · S2C (server→client dispatch)

| method_instance | Reads as | Name | Note |
|-----------------|----------|------|------|
| `0x00` | `0x2000` | `DispatchChallenge` | (was "InitiateAuthentication" - it carries the DH challenge reply) |
| `0x01` | `0x2001` | `DispatchAckAuthenticate` | carries the PlayerAgent realm handoff (was "SendPlayerAgentRealm") |
| `0x02` | `0x2002` | `DispatchLoginProblem` |  |
| `0x03` | `0x2003` | `DispatchSetUpdateManager` |  |
| `0x04` | `0x2004` | `DispatchSetUniverseManagerError` |  |
| `0x05` | `0x2005` | `DispatchSetRegionState` | (was "SetRegion") |
