# AoC client RPC method catalog (extracted from AgeOfConan.exe)

Stage 1 of `docs/re/opcode-re-plan.md`. Recovered from the client's own log strings - method **names**,
**param names/types**, and **direction**. (Numeric ids: opcodes are framework-assigned - see
`docs/re/aoc-opcode-ids.md`; the IIR/message + endpoint ids are BKDR-31 name hashes, decoded there.)


**14 interfaces, 376 client->server (proxy) methods, 401 server->client (dispatch) methods.**


- **client->server** = `<Iface>AgentProxy_t::Method(...)`, signature is authoritative (typed params).
- **server->client** = `<Iface>InterfaceDispatch_t::DispatchMethod()`; `fields` are the param NAMES in
  wire/read order (from the per-field `stream bad after '<field>'` strings); types aren't in the string.


### Reading the field names (Hungarian prefixes -> wire type)
The server->client field names encode their type by prefix (verified against the typed client->server
signatures). Use this to read the layout; the proto schema / retail captures stay authoritative for exact
bytes. `i_` just marks an input param (ignore it):

- `c<Name>` / `i_c<Name>` -> an **ID32** (4-byte object/char/inventory handle) or a serialized object
  (e.g. `i_cTargetID`, `i_cInventoryID`, `cName` when it's a string object).
- `n<Name>` / `i_n<Name>` -> an **integer** (uint32/int32) (e.g. `i_nSpellID`, `i_nItemPos`, `nShotType`).
- `b<Name>` -> a **bool** (e.g. `bAutoface`).
- `i_v<Name>` -> a **vector / vec3** (e.g. `i_vHeadPaddingY`) - sometimes a scalar component; check size.
- `f<Name>` -> a **float**. Names without a prefix follow the same root convention.
- A field whose name is plural / `...List` / `...Data` (e.g. `overrides`, `cQuestData`, `fbFriends`) is a
  **repeated/collection** type - it has a nested element encoding not visible in the field name alone.


## CSPlayerAgent


### client -> server (135)

- `Authenticate(ID32::InstanceType nCharacterInstance, uint32 nCookie, unsigned int communityVersion)`
- `CharacterAuthenticated(ID32::InstanceType nCharacterInstance)`
- `CharacterDisconnected(ID32::InstanceType nCharacterInstance)`
- `TeamInvite(const ID32& i_cTargetID, const std::string& i_cMessage, bool i_bCheckCriminalStatus)`
- `TeamInviteByName(const std::string& i_cCharacterName, const std::string& i_cMessage, bool i_bCheckCriminalStatus)`
- `TeamApply(const ID32& i_cTeamID)`
- `TeamSetLootOption(int i_eLootMode, bool i_bUseNeedGreed, int i_nNewLootThreshold)`
- `TeamSetMasterLooter(const ID32& i_cCharacterID)`
- `AcceptTeamInvite(const ID32& i_cInviterID)`
- `DeclineTeamInvite(const ID32& i_cInviterID)`
- `CharacterSearch(int i_eSearchmode, const InstanceVec_t& i_cPlayfieldVec, int i_nMinLevel, int i_nMaxLevel, const IntVec_t& i_nClassVec, const IntVec_t& i_nRaceVec, uint32 i_nSkipResults, const String& i_cNameSearch)`
- `SearchForTeam(const InstanceVec_t& i_cPlayfieldVec, int i_nMinLevel, int i_nMaxLevel, int memberCountMin, int memberCountMax, uint32 i_nSkipResults)`
- `SetNewTeamLeader(const ID32& i_cNewLeaderID)`
- `SummonCharacters(const ID64Vec_t& i_cCharacterVec)`
- `AcceptSummon()`
- `DeclineSummon()`
- `GetExtendedCharacterInfo(const ID32& i_cCharacterID)`
- `GetExtendedCharacterInfoByName(int i_nWindowId, const std::string& i_cCharacterName)`
- `SetLookingForTeamStatus(bool i_bLookingForTeam, const std::string& i_cMessage)`
- `SetLookingForMemberStatus(bool i_bLookingForMember, const std::string& i_cMessage)`
- `GetLookingForTeamStatus()`
- `GetLookingForMemberStatus()`
- `SetCommunityServerOptions(const CommunityServerOptions_c& i_cOptions)`
- `StartVoteKick(const ID32& i_cCharacterID)`
- `VoteKickAnswer(const ID32& i_cCharacterID, bool kick)`
- `StartVoteRetreat()`
- `VoteRetreatAccept()`
- `VoteRetreatReject()`
- `AcceptGuildInvitation(const ID32& i_cGuildID)`
- `DeclineGuildInvitation(const ID32& i_cGuildID)`
- `ClientRequestGuildInfo(const ID32& i_cCharacter)`
- `ClientRequestGuildName(const ID32 i_cGuildID)`
- `ClientRequestCharIDByName(const String& i_cCharacterName)`
- `CreateGuild(const std::string& i_cGuildName, uint8 i_nGoverningForm, uint8 i_nTax, uint8 i_nLookingForMember)`
- `GuildInviteByName(const String& i_cName)`
- `GuildInvite(const ID32& i_cCharacter)`
- `RemoveGuildInvitationCooldown(const ID32& guildID, const ID32& characterID)`
- `GuildPromote(const ID32& i_cCharacter)`
- `GuildDemote(const ID32& i_cCharacter)`
- `GuildKick(const ID32& i_cCharacter)`
- `GuildLeave()`
- `GuildTax(int nTax)`
- `GuildGovernmentForm(uint i_nGovernmentForm)`
- `GuildRecruitmentMode(uint8 i_nRecruitmentMode)`
- `GuildName(const std::string& i_cGuildName)`
- `GuildDescription(const std::string& i_cGuildDescription)`
- `GuildMessageOfTheDay(const std::string& i_cGuildMessageOfTheDay)`
- `GuildRequestMemberList()`
- `GuildApplication(const ID32& i_cGuildID, std::string i_cApplicationText)`
- `GuildRejectApplication(const ID32::InstanceType& i_cApplicant, const ID32::InstanceType& i_cGuildMember)`
- `SetLookingForGuild(uint8 i_nState, const std::string& i_cAbout)`
- `GuildMergeRequest(const ID32& i_cOtherLeader)`
- `GuildMergeReply(const ID32& i_cOtherLeader, int nReply)`
- `GuildWithdrawMoney(uint64 i_nAmount)`
- `GetPvpPointsInfo(const ID32& i_cCharacter)`
- `AddPvpPoints(const ID32& i_cCharacter, uint32 i_nAmount)`
- `RemovePvpPoints(const ID32& i_cCharacter, uint32 i_nAmount)`
- `ClearPvpPoints(const ID32& i_cCharacter)`
- `GetGuildRenownInfo(const ID32& character)`
- `AddGuildRenown(const ID32& character, uint8 grType, uint64 amount, bool onlyGuild)`
- `RemoveGuildRenown(const ID32& character, uint8 grType, uint64 amount, bool onlyGuild)`
- `SetGuildRenown(const ID32& character, uint8 grType, uint64 amount, bool onlyGuild)`
- `CalculateRankings()`
- `UnlockGuildRenown()`
- `RequestRenownHistory()`
- `RequestRenownRankings()`
- `RequestBattleKeepInformation(IntVec_t areaIDVec)`
- `SearchForGuilds(uint16 i_nMinMembers, uint16 i_nMaxMembers, uint8 i_nMinLevel, uint8 i_nMaxLevel, const String& i_cNameSearch)`
- `GMGuildName(const ID32& i_cCharacter, const std::string& i_cGuildName)`
- `RequestGuildCitySpot(const ID32& i_cCharacter, const ID32& i_cGuildID, const ID32& i_cPlayfieldModelID)`
- `ApplyForMassivePVPBattleQueue(const ID32& i_cCharacter, const ID32& i_cGuildID, int i_nArea)`
- `WithdrawFromMassivePVPBattleQueue(const ID32& i_cCharacter, const ID32& i_cGuildID, int i_nArea)`
- `MassivePvPBattleQueueGMCommand(const ID32& i_cCharacter, const ID32& i_cGuildID, int i_nArea, int i_nMode)`
- `MassivePvpBattleQueueStatusRequest(const ID32& i_cGuild)`
- `PvPMatchMakingCreateMatch(const PvPMatchRules_c& i_cMatch)`
- `PvPMatchMakingJoinMatch(ID32::InstanceType i_nMatchID, bool i_bBringTeam, const std::string& i_cPassword)`
- `PvPMatchMakingSetSide(int i_nSide)`
- `PvPMatchMakingMatchSay(const std::string& i_cMessage)`
- `PvPMatchMakingLeaveMatch()`
- `PvPMatchMakingSearchForMatches(const PvPMatchMakingSearchFilter_c& i_cSearchFilter)`
- `PvPMatchMakingGetMatchInfo(ID32::InstanceType i_nMatchID)`
- `PvPMatchMakingTeleportToMinigame()`
- `PvPMatchMakingPutIntoMatchMaking(ID32::InstanceType i_nMap, int i_nType, bool i_bBringTeam, int i_QueueType)`
- `PvPMatchMakingRemoveFromMatchMaking()`
- `PvPMatchMakingRequestInfo(int i_QueueType)`
- `PvPMatchMakingIsInMatchMaking()`
- `PvPMatchMakingKickPlayer(const ID32& i_cCharacterID, const std::string& i_cMessage)`
- `PvPMatchMakingBanPlayer(const ID32& i_cCharacterID, const std::string& i_cMessage)`
- `PvPMatchMakingMakePlayerMatchOwner(const ID32& i_cCharacterID)`
- `PvPMatchMakingForceSide(const ID32& i_cCharacterID, int i_nSide)`
- `PvPMatchMakingStartMatch()`
- `DebugPvPMatchMakingShowDebugInfo()`
- `DebugPvPMatchMakingStopAllMatches(const std::string& i_cMessage)`
- `DebugPvPMatchMakingStopCurrentMatch(const std::string& i_cMessage)`
- `DebugPvPMatchMakingSetNewTimeLimit(uint32 i_nNewTimeLimit)`
- `DebugPvPMatchMakingRestart()`
- `DebugPvPMatchForcePlayerIntoCurrentMatch(const std::string& i_cName, int i_nSide)`
- `DebugPvPMatchMakingDoMatchMaking(int i_nPlayers, int i_nSides, int i_nMap, int i_nGameType, int i_nTier, int i_nTimeLimit, int i_QueueType)`
- `DebugPvPMatchForceStartMatch()`
- `DebugPvPMatchForceEnterAll()`
- `DebugCommunityServerModuleStart(int i_nModule, int i_nLogLevel)`
- `DebugCommunityServerModuleStop(int i_nModule)`
- `SetAsyncEnabled(bool asyncEnabled)`
- `SetNumWorkerThreads(size_t numWorkers)`
- `RaidCreate()`
- `RaidLeave()`
- `RaidInviteTeam(const ID32& i_cTeamID)`
- `RaidInviteTeamAccept(const ID32& i_cTeamID)`
- `RaidInviteTeamDecline(const ID32& i_cTeamID)`
- `RaidKickTeam(const ID32& i_cTeamID)`
- `RaidKickCharacter(const ID32& i_cCharacterID)`
- `RaidMoveTeamMember(const ID32& i_cCharacterID, const ID32& i_cDestinationTeamID)`
- `RaidSwapCharacters(const ID32& i_cCharacterID, const ID32& i_cCharacterID2)`
- `RaidInviteCharacter(const ID32& i_cCharID, const std::string& i_cMessage)`
- `RaidInviteCharacterByName(const std::string& i_CharName, const std::string& i_cMessage)`
- `RaidAcceptInvite(const ID32& i_cCharID)`
- `RaidDeclineInvite(const ID32& i_cCharID)`
- `RaidSetLootOption(int i_eLootMode, bool i_bUseNeedGreed, int i_nLootThreshold)`
- `RaidSetMasterLooter(const ID32& i_cCharacterID)`
- `MassivePvpInfoRequest()`
- `SendTeamInfo(const ID32& i_cTeamID)`
- `GMRequestGuildInfoFromChar(const ID32& i_cCharID)`
- `QueueGroupFinder(const std::vector<uint32>& activites, bool wantsLeader, bool privateTeam)`
- `SelectGroupFinderRoles(uint32 roles)`
- `LeaveGroupFinder()`
- `ReadyGroupFinder(bool ready, bool isTimeout)`
- `RequestRaidQueueInfo(uint32 raidID)`
- `UpdateDefaultRaid(uint32 raidActivityId, uint32 childActivityId)`
- `SetFeaturedRaid(uint32 raidID)`
- `RequestFeaturedRaidNeededRole(uint32 difficulty)`
- `RequestNeededRole(uint32 activityId)`
- `RequestRotatingActivityChildInfo(uint32 activityId)`
- `GetWorldStat(uint32 statHash, int type1, int type2, int dimID)`
- `RequestItemSalesBalance()`
- `RequestItemSalesCoupons(uint salesID)`

## CSPlayerInterface


### server -> client (178)

- `DispatchAuthenticateAck(nStatus)`
- `DispatchTeamInvite(i_cWho, i_cMessage)`
- `DispatchTargetIsBusy(i_cWho)`
- `DispatchTargetIsCriminal(i_cWho)`
- `DispatchTeamCharacterApplied(i_cWho)`
- `DispatchYouAlreadySentRequest(i_cWho)`
- `DispatchTargetIsAlreadyInTeam(i_cWho)`
- `DispatchTeamInvitationTimedOut(i_cWho)`
- `DispatchTargetDeclinedTeamInvite(i_cWho)`
- `DispatchYouAreNoLongerInvited(i_cWho)`
- `DispatchYouInvitedToTeam(i_cWho)`
- `DispatchInviteWithInviterFactionPoints(inviter, message, factionPoints)`
- `DispatchExtendedCharacterInfo(i_cWho, i_nWindowId, i_cLookingForTeamMessage)`
- `DispatchExtendedCharacterInfoNotFound(i_nWindowId)`
- `DispatchYouJoinedTeam(i_cTeamInfo)`
- `DispatchTeamSearchResult(i_cResult, i_bMoreHits)`
- `DispatchCharacterSearchResult(i_cResult, i_bMoreHits)`
- `DispatchCharacterNameSearchResult(i_cCharacter, i_cCharacterName)`
- `DispatchTeamLeave(i_cTeamID, i_cWho)`
- `DispatchTeamJoin(i_cTeamID, i_cWho)`
- `DispatchTeamMemberUpdate(i_cTeamID, i_cWho)`
- `DispatchTeamDisband(i_cTeamID)`
- `DispatchSetNewTeamLeader(i_cTeamID, i_cWho)`
- `DispatchTeamLootOptionChanged(i_eLootMode, i_bUseNeedGreed, i_nNewLootThreshold)`
- `DispatchTeamMasterLooterChanged(i_cCharacterID)`
- `DispatchTeamSummonRequest(i_cWho)`
- `DispatchTeamSummonTimedOut(i_cWho)`
- `DispatchYouAreNoLongerSummoned(i_cWho)`
- `DispatchTeamSummonRequestDeclined(i_cWho)`
- `DispatchTeamSummonRequestAccepted(i_cWho)`
- `DispatchTeamSummonRequestFailedInvalidPF(i_cWho)`
- `DispatchTeamSummonRequestFailedAlreadyInSameInstance(i_cWho)`
- `DispatchWarningTeamSizeTooLarge(i_nDisbandInSeconds)`
- `DispatchYouGotKickedFromTeam(i_cWho)`
- `DispatchVoteKickStarted(i_cInitiatorID, i_cKickedID, i_cKickedName)`
- `DispatchVoteKickFailed(i_cMessage)`
- `DispatchVoteKickEnded(i_cTargetID)`
- `DispatchCharacterLookingForTeamStatus(i_bLookingForTeam, i_cMessage)`
- `DispatchTeamLookingForMemberStatus(i_bLookingForMember, i_cMessage)`
- `DispatchDeleteCanceledMail(mailId)`
- `DispatchDeleteCanceledMailItem(mailId, itemId)`
- `DispatchGuildInvite(i_cGuildID, i_cGuildName, i_cCharacter, i_cInviterName)`
- `DispatchGuildMemberLeft(i_cMember, i_cMemberName)`
- `DispatchGuildMemberJoined(i_cMember, i_cMemberName)`
- `DispatchGuildMemberUpdate(i_cElement)`
- `DispatchGuildInviteReplyToInviter(i_cMessage, i_bJoined)`
- `DispatchGuildGMPromoteFeedback(i_nGoverningForm, i_nOldRank, i_nNewRank, i_cName)`
- `DispatchGuildGMDemoteFeedback(i_nGoverningForm, i_nOldRank, i_nNewRank, i_cName)`
- `DispatchGuildInviteNoSuchName(i_cName)`
- `DispatchGuildPvPPointInfo(i_cGuildName, i_nPoints)`
- `DispatchGuildRenownInfo(infoText)`
- `DispatchGuildRankingsUpdate(guildList, topRankingsMap, nextCalculationTime)`
- `DispatchGuildRenownHistoryUpdate(currentEntry, lastEntry, highestEntry, averageEntry)`
- `DispatchClientAddGuildRenown(renownType, amount, guildOnly, capType)`
- `DispatchInformAboutRankings(overallRanking, gloryRanking, valorRanking, artistryRanking)`
- `DispatchInformAboutRankingWinners(overallWinner, gloryWinner, valorWinner, artistryWinner)`
- `DispatchRankingCaluculationCountdown(hoursUntilCaclulation)`
- `DispatchPersonalRenownCapReached(renownType)`
- `DispatchGuildUpdate(i_pcGuild, i_pcMember, i_cMemberList)`
- `DispatchGuildRenownUpdate(pcGuild)`
- `DispatchGuildName(i_nGuildInstance, i_cGuildName)`
- `DispatchGuildUpdateMember(i_nCharInstance, i_cGuildID, i_pcMember)`
- `DispatchGuildUpdateOther(i_nCharInstance, i_cGuildID, i_pcGuild)`
- `DispatchGuildCreated(i_cMember, i_cGuild)`
- `DispatchGuildNonAlphanumericName(cIllegalCharacters)`
- `DispatchGuildBadName(i_cBadword)`
- `DispatchGuildNameWaitTime(i_nTimeLeft)`
- `DispatchGuildOnInviteCooldown(inviteCooldownStop)`
- `DispatchGuildTaxError(i_nNewTax)`
- `DispatchGuildInvalidGoverningForm(i_cGoverningForm)`
- `DispatchGuildUpdateCash(i_nCash)`
- `DispatchGuildTaxChanged(i_nTax)`
- `DispatchGuildGoverningFormChanged(governingForm, newLeader)`
- `DispatchGuildRecruitmentModeChanged(i_nRecruitmentMode)`
- `DispatchGuildMessageOfTheDayChanged(i_cMessageOfTheDay)`
- `DispatchGuildNameChanged(i_cNewName)`
- `DispatchGMGuildNameChanged(i_nGuildInstance, i_cNewName)`
- `DispatchGuildMemberList(i_cMembers)`
- `DispatchGuildSearchResult(i_cResult, i_nMoreResults)`
- `DispatchGuildSetPvPPoints(i_nPoints)`
- `DispatchGuildSetPvPKillPoints(i_nPoints)`
- `DispatchGuildSetPvPTotalKills(i_nKills)`
- `DispatchGuildSetPvPSeasonalKills(i_nKills)`
- `DispatchGuildSetGuildWar(guildwarID)`
- `DispatchGuildSetGuildAsDefenderOfWar(guildwarID)`
- `DispatchAddGuildFeat(i_nFeatID)`
- `DispatchRemoveGuildFeat(i_nFeatID)`
- `DispatchGuildRankChanged(newRank, oldRank)`
- `DispatchGuildApplicationRequest(i_cName, i_cApplicationText, i_cApplicant)`
- `DispatchGuildApplicationRejected(i_cName)`
- `DispatchGuildMergeRequest(i_nGuidInstance, i_nOtherLeader)`
- `DispatchGuildMergeRequestFailed(i_nReason)`
- `DispatchGuildCityBooked(i_nCitySpotId, i_nCityPlayfieldModelInstance, i_nCitySubgroup)`
- `DispatchGuildCityBookingFailed(i_nErrorCode)`
- `DispatchNewVulnerabilityWindowData(i_cNewAreaInfo, i_nTimeStamp)`
- `DispatchGuildIsMassivePvPCityOwner(i_bNewValue)`
- `DispatchGuildIsMassivePvPCityAttacker(i_bNewValue)`
- `DispatchFeedbackMessage(message)`
- `DispatchRequestBattleKeepInformation(areaIDVec)`
- `DispatchGMReplyGuildInfoFromChar(i_cGuild, i_cMembers)`
- `DispatchPvPMatchMakingWantsToStart(i_nKickedInSeconds)`
- `DispatchPvPMatchMakingPlayerJoined(i_cPlayer, i_nSide)`
- `DispatchPvPMatchMakingPlayerChangedSide(i_cPlayer, i_nNewSide)`
- `DispatchPvPMatchMakingPlayerLeft(i_cCharacterID)`
- `DispatchPvPMatchMakingPlayerKicked(i_cCharacterID, i_cMessage)`
- `DispatchPvPMatchMakingErrorCode(i_nErrorCode)`
- `DispatchPvPMatchMakingYouJoinedMatch(i_cMatch)`
- `DispatchPvPMatchMakingMatchInfo(i_cMatch)`
- `DispatchPvPMatchMakingSearchResult(i_cMatches)`
- `DispatchPvPMatchMakingStartClicked(i_nTeleportInSeconds)`
- `DispatchPvPMatchMakingYouAreInMatchMaking(i_nMap, i_nType, i_ShowFifo, i_AsTeamMember, i_queueType)`
- `DispatchPvPMatchMakingMatchSay(i_cFrom, i_cMessage, i_bFifo)`
- `DispatchPvPMatchMakingCollectingMorePlayers(i_nSecondsUntilForcedStart)`
- `DispatchPvPMatchMakingNewMatchLeader(i_cNewMatchLeader)`
- `DispatchPvPMatchMakingTeamLeaderSignedUpForMatchMaking(i_nMap, i_nType)`
- `DispatchPvPMatchMakingCantStartMatchNotAllPlayersHaveSide(i_cPlayesWithoutSide)`
- `DispatchPvPMatchMakingCantLockToSideMustPickASideFirst(i_cChar)`
- `DispatchPvPMatchMakingCantChangeSideYouHaveBeenLockedToSide(i_nSide)`
- `DispatchPvPMatchMakingCharacterNowLockedToSide(i_cCharID, i_nSide)`
- `DispatchPvPMatchMakingCharacterNoLongerLockedToSide(i_cCharID)`
- `DispatchPvPMatchMakingInfoRequestReturn(i_cList, i_nNumberOfAnyApplies)`
- `DispatchYouJoinedRaid(i_cMatch)`
- `DispatchRaidTeamJoined(i_cTeam)`
- `DispatchRaidTeamLeft(i_cTeamID)`
- `DispatchRaidTeamUpdate(i_cTeam)`
- `DispatchRaidCharacterUpdate(i_cCharacter)`
- `DispatchRaidNewRaidLeader(i_cTeamID)`
- `DispatchRaidLootOptionChanged(i_eLootMode, i_bUseNeedGreed, i_nNewLootThreshold)`
- `DispatchRaidNewMasterLooter(i_cWho)`
- `DispatchRaidInvite(i_cWho, i_cMessage)`
- `DispatchRaidTargetIsBusy(i_cWho)`
- `DispatchRaidIsAlreadyInRaid(i_cWho)`
- `DispatchRaidInvitationTimedOut(i_cWho)`
- `DispatchRaidTargetDeclinedRaidInvite(i_cWho)`
- `DispatchRaidYouAreNoLongerInvited(i_cWho)`
- `DispatchRaidCantInviteInvalidSubscriptionFlags(i_cWho)`
- `DispatchRaidCantCreateInvalidMemberSubscriptionFlags(i_cWho)`
- `DispatchMassivePvpInfoRequestReturn(i_nLastLoadTime, i_cList, i_nTimeRegionOffset)`
- `DispatchMassivePvPBattleQueueFeedback(i_nFeedbackType, i_nFeedbackValue)`
- `DispatchMassivePvpStartCountDown(i_cEndTime)`
- `DispatchMassivePvpBattleQueueStatusUpdate(i_nArea, i_inQueue)`
- `DispatchBattleKeepInfoRequestReturnPhase1And2(bkInfoVec)`
- `DispatchBattleKeepInfoRequestReturnPhase3And4(bkInfoVec)`
- `DispatchYouGotMailNotification(nMailId)`
- `DispatchDebugFullGameServerList(i_cGameServerList)`
- `DispatchDebugGameServerConnected(i_cGameServerID)`
- `DispatchDebugGameServerDisconnected(i_cGameServerID)`
- `DispatchDebugClientConnected(i_cCharacter, i_nGameServerID, i_nGmLevel, i_bLinkDead)`
- `DispatchDebugClientChanged(i_cCharacter, i_nGameServerID, i_nGmLevel, i_bLinkDead)`
- `DispatchDebugClientDisconnected(i_cCharacterID)`
- `DispatchDebugFullClientList(i_cCharacterList, i_cGameServerList, i_nGmLevelList, i_cLinkDeadVec)`
- `DispatchDebugFullTeamListBurst(i_cTeamList)`
- `DispatchDebugAddTeam(i_cTeamInfo)`
- `DispatchDebugRemoveTeam(i_cTeamID)`
- `DispatchDebugUpdateTeam(i_cTeamInfo)`
- `DispatchDebugFullMatchListBurst(i_cMatchList, i_cGameServerList)`
- `DispatchDebugAddMatch(i_cMatch)`
- `DispatchDebugMatchChanged(i_cMatch, i_nGameServer)`
- `DispatchDebugRemoveMatch(i_cMatchID)`
- `DispatchDebugAddIDToMatchMaking(i_cID, i_Map, i_Type, i_Tier, i_Queue)`
- `DispatchDebugRemoveIDFromMatchMaking(i_cID)`
- `DispatchItemSalesUpdateBalance(balance)`
- `DispatchItemSalesCoupons(saleID, coupons)`
- `DispatchCSFeedback(message)`
- `DispatchGroupFinderSignupFailed(message)`
- `DispatchGroupFinderSignupSuccess(activities, silent)`
- `DispatchGroupFinderRemoved(reason, silent)`
- `DispatchGroupFinderReadyCheck(queue, role)`
- `DispatchGroupFinderReadyCheckStatus(ready, declines, total)`
- `DispatchGroupFinderReadyCheckRejected(reason)`
- `DispatchGroupFinderPlayfieldInfo(currPlayfield, nextPlayfield, neededRole)`
- `DispatchGroupFinderFeaturedPlayfieldInfo(raidId, playfieldId, difficultyCount, supportsPrivateTeam, neededRole)`
- `DispatchGroupFinderFeaturedRaidNeededRoleUpdated(neededRole)`
- `DispatchGroupFinderReceivedNeededRole(activityId, neededRole)`
- `DispatchGroupFinderReceivedRotatingActivityChildInfo(activityId, currentChildIndex, nextChildIndex)`
- `DispatchGroupFinderUpdateFeaturedActivities(activities)`
- `DispatchReturnWorldStat(statHash, value, type1, type2, dimID)`
- `DispatchInvalidWorldStat(statHash)`

## GameAgent


### client -> server (11)

- `PassBlob(BinaryStream& b)`
- `Ping(uint32 sequenceId)`
- `ChatVicinity(const ID32& cSenderID, int nMode, BinaryStream& cMsg)`
- `Authenticate2(ID32::InstanceType nClientInst, uint nCookie, const String& cGameClientVersionString)`
- `GetStats()`
- `UpdateGameServerStats()`
- `DataSink(BinaryStream& cMsg)`
- `ClientDisconnected(ID32::InstanceType i_nClientID)`
- `SaveFacebookToken(uint64 facebookUserId, const String& tokenToStore)`
- `RecruitFacebookFriends(const FacebookNS::FacebookFriendsMap& fbFriends)`
- `UpdateFacebookFriends(const std::vector<uint64>& fbFriendIds)`

## GameCharAgent


### client -> server (169)

- `RequestAggroStatus(const ID32& i_cCharID, const ID32& i_cTargetID)`
- `RequestCharName(ID32::InstanceType nCharID)`
- `RequestBindLocation(const ID32& i_cCharID)`
- `SetTutorialStats(int nValue)`
- `UpdateCharGuildMemberInfo(const ID32& i_cCharID, const GuildMemberElement_c& i_pcMember, const ID32& i_cGuildID)`
- `GuildInfoUpdate(const GuildElement_c& cGuild)`
- `JoinedGuild(const ID32& i_cCharID, const GuildMemberElement_c& i_pcMember, const ID32& i_cGuildID)`
- `LeftGuild(const ID32& i_cCharID)`
- `LeaveTeam()`
- `KickFromTeam(const ID32& kickedCharId)`
- `CreatedGuild(const ID32& i_cCharID, const GuildMemberElement_c& i_pcMember, const GuildElement_c& i_cGuild)`
- `AddGuildFeat(const ID32& i_cGuildID, uint32 i_nFeatID)`
- `RemoveGuildFeat(const ID32& i_cGuildID, uint32 i_nFeatID)`
- `GuildGMPromote(const ID32& i_cTarget)`
- `GuildGMDemote(const ID32& i_cTarget)`
- `GuildTaxChanged(Instance64_t i_nGuildInstance, int i_nTax)`
- `GuildGoverningFormChanged(Instance64_t i_nGuildInstance, int i_bGoverningForm)`
- `GuildTransferMoney(uint64 i_nGuildInstance, Explicit64_c i_cAmount)`
- `GuildWithdrawMoney(const ID32& i_cOfficer)`
- `GuildDisbanded(ID32::InstanceType i_nGuildInstance, uint i_nSubgroup, uint i_nModelInstance, const String i_cCityName)`
- `GuildBookedCity(uint i_nSubgroup, uint i_nModelInstance, const String i_cCityName, uint i_nCityNameId, const std::vector<ID32>& i_cMembers)`
- `GuildCityOwnershipChange(const std::vector<ID32>& i_cMembers, bool i_bNewValue)`
- `GuildChangeRank(ID32 i_cChar, uint8 i_nOldRank, uint8 i_nNewRank)`
- `GuildUpdateGuildBank(const ID32& i_cCharacter)`
- `SendGuildInfoToObservers(ID32 i_cChar, const std::string& i_cGuildName)`
- `GuildRankingsUpdate(const GuildCommonRankingMap& guildList, const GuildRankingMap& topRankingsMap)`
- `ValidateGuildBadges()`
- `OpenGuildBank()`
- `CloseBank()`
- `SplitInventoryItem(const ID32& i_cInventory, int i_nSrcPos, int i_nSplitSize, int i_nDstPos)`
- `CancelBuff(uint32 i_nSpellID, ID32::InstanceID_t i_nCaster)`
- `StackInventoryItemIfPossible(const ID32& i_cSrcContainerID, uint8 i_nSrcPos, const ID32& i_cDstContainerID, uint8 i_nDstPos)`
- `UpdateQuestMarkerNPC(const ID32& i_cNPC, const ID32& i_cClientChar)`
- `MagicForgetSpell(int i_nSpellID)`
- `ResurrectMe(uint32 resurrectSpellID)`
- `CancelResurrectionRequest(const ID32& resurrectRequesterID, uint32 resurrectSpellID)`
- `RespawnMe(uint32 i_nSpawnPointID)`
- `SignUpForWaveRespawn(uint32 spawnPointID, uint32 playerFaction)`
- `UpdateWaveRespawn(uint32 spawnPointID, uint32 playerFaction)`
- `CancelWaveRespawn(uint32 spawnPointID, uint32 playerFaction)`
- `RequestWaveRespawnTimerUpdate()`
- `RespawnMeHere()`
- `GMRequestSpellData(const ID32& i_cTarget)`
- `RM_GetQuestFromQuestgiver(int idQuestGiver, const ID32& questGiverInstanceID)`
- `GMRequestFeatData()`
- `GMRequestFeatClassData()`
- `AdjustSkill(int i_nSkill, int i_nAmount)`
- `RequestPCDebugData(ID32::InstanceType nCharID)`
- `GetLootDataForID(const ID32& i_cCharacter)`
- `SimulateLootForID(const ID32& i_cCharacter, int nCount)`
- `CloseShop(const ID32& i_cShopID)`
- `BuyItem(const ID32& i_cShopID, int i_nPos)`
- `SellItem(const ID32& i_cShopID, const ID32& i_cInventoryID, int i_nPos)`
- `UndoLastSoldItem(const ID32& i_cShopID)`
- `FormationSetData(const CreatePlayerFormation_c& i_cFormation)`
- `FormationClaimPosition(int i_nPositionID)`
- `FormationClear()`
- `FormationLeave()`
- `FormationUpdatePosRot(const Vector3_c& i_cPos, const Quaternion_c& i_cRot)`
- `ScryMessageBoxAnswer(int nAnswer, int nScriptThreadCtxId, uint64 nCtxCookie)`
- `ScryOptionsBoxAnswer(int nAnswer, int nScriptThreadCtxId, uint64 nCtxCookie, const ScenarioPacket& packet)`
- `ExitScenarioResults(int nAnswer, int nScriptThreadCtxId, uint64 nCtxCookie)`
- `FeatUntrainAnswer(bool bYes, int i_nCategory, int i_nScriptThreadCtxId, uint64 i_nCtxCookie)`
- `CraftUntrain(int nCraftID)`
- `PlantCTFFlags()`
- `UntrainFeat(int featID)`
- `TryOpenLootBag(const ID32& i_cLootBagID)`
- `TryLootAll(const ID32& i_cLootBagID)`
- `CloseLootBag(const ID32& i_cLootBagID)`
- `TryLootCash(const ID32& i_cLootBagID)`
- `TryLootItem(const ID32& i_cLootBagID, int i_nItemPos, const ID32& i_cCharID, int i_nDesiredPosition)`
- `PassNeedGreedForItem(const ID32& i_cLootBagID, int i_nItemPos)`
- `DoNeedGreedRollForItem(const ID32& i_cLootBagID, int i_nItemPos, bool i_bNeed)`
- `LootLockedItem(const ID32& lootBagID, int itemPos)`
- `SlaveLooterAnswer(const ID32& i_cLootBagID, int i_nItemPos, bool i_bAccept)`
- `MasterLooterOffer(const ID32& i_cLootBagID, int i_nItemPos, const ID32& i_cReceiver)`
- `MasterLooterCancelOffer(const ID32& i_cLootBagID, int i_nItemPos, const ID32& i_cReceiver)`
- `SetPassOnLootLevel(int i_Level)`
- `UnbindItem(const ID32& inventoryID, int unbindItemLocation, int itemLocation)`
- `ShareQuest(const ID32& i_cQuestId)`
- `ShareQuestDeclined(const ID32& i_cRequesterId)`
- `ShareQuestAccepted(const InstanceID_t& i_cQuestId)`
- `ShareQuestBusy(const ID32& i_cRequesterId)`
- `RequestNextQuestPendingReward(const ID32& i_cRequesterId)`
- `RequestTeamQuestList()`
- `AddQuestList(const std::vector<InstanceID_t>& cAvailableQuestList)`
- `AcceptNextAwaitingQuest()`
- `QuestManagerMsg(const ID32& requestingCharacter, const ID32& targetCharacter, int taskTemplate, int msgType)`
- `StartQuickTeleport(const ID32& i_cRequesterId, uint i_nPlayfieldProxyModelType, uint i_nPlayfieldProxyModelInstance, uint i_nPosX, uint i_nPosY, uint i_nPosZ, uint i_nSubgroup, uint i_nDifficulty)`
- `DoPaintballQuery(const PaintballClientServer_c& i_cPaintball)`
- `PerformSpecialAction(int i_nActionPos)`
- `QueuePetCommand(const ID32& i_cPetID, uint i_nPetCommand)`
- `DoSkillSellback(int i_eStat)`
- `PetSetGroundTargetPos(float i_vX, float i_vY, float i_vZ)`
- `PlayStockphraseAsSubtitle(const ID32& i_cNpcId, const String i_cStockphrase, int i_nThreadContextInstanceNr)`
- `CinematicTransition(const ID32& i_cClientId, const bool i_bEntering)`
- `RequestACGTeleport(const PlayfieldProxy& i_cProxy)`
- `RequestClearOwnACG(ID32::InstanceType i_nPlayfieldID)`
- `RequestDropTeamBind(ID32::InstanceType i_nPlayfieldID)`
- `DebugSetOceanData(float i_vOceanAlt, float i_vOceanWave)`
- `RequestTrade(const ID32& i_cTargetID)`
- `StartTrade(const ID32& i_cTargetID)`
- `DeclineTrade(const ID32& i_cTargetID)`
- `SetTradeCash(uint64 i_nCash)`
- `AddTradeItem(const ID32& i_cIventoryID, int i_nItemPos, int i_nDestPos)`
- `RemoveTradeItem(int i_nDestPos)`
- `MoveTradeItemInternal(int i_nItemPos1, int i_nItemPos2)`
- `AcceptTrade()`
- `AbortTrade()`
- `RequestShortcutBar()`
- `AddShortcut(int i_eType, int i_nPosition, uint32 i_nData, uint32 i_nInventoryType)`
- `UpdateShortcut(int i_nPosition, uint32 i_nData, uint32 i_nInventoryType)`
- `MoveShortcut(int i_nFromPos, int i_nToPos)`
- `RemoveShortcut(int i_nPosition)`
- `StartMinigame(const PvPMatchInfo_c& i_cMatchInfo)`
- `NewMinigamePlayer(ID32::InstanceType i_nMatchID, const ID32& i_cPlayerID, int i_nSide)`
- `DropMinigamePlayer(ID32::InstanceType i_nMatchID, const ID32& i_cPlayerID)`
- `ResendMinigameInfoToPlayer(ID32::InstanceType i_nMatchID, const ID32& i_cPlayerID)`
- `FlagAsCoward(const ID32& i_cPlayerID)`
- `ForceStopMinigame(ID32::InstanceType i_nMatchID, const std::string& i_cMessage)`
- `ForceSetMinigameTime(ID32::InstanceType i_nMatchID, uint32 i_nNewTimeInSeconds)`
- `PrepareMinigameTime(ID32::InstanceType i_nMatchID)`
- `PrepareToMinigameTime(ID32::InstanceType i_nMatchID)`
- `GetInstanceListForCurrentPlayfield()`
- `ShiftInstanceForCurrentPlayfield(uint32 i_nInstanceIndex)`
- `EnableHeroicMode(bool i_bEnabled)`
- `SetHideInspectOption(bool i_bEnabled)`
- `SetPlayfieldInstanceList(ID32::InstanceType uCharInst, const PlayfieldInstanceList_t& cPlayfieldList)`
- `InstanceWillResetOnShutdown(const ID32& i_cCharID, const ID32& i_cRequester)`
- `InstanceHasBeenLocked(const PlayfieldProxy& i_cPlayfield)`
- `InstanceHasBeenUnLocked(const PlayfieldProxy& i_cPlayfield)`
- `RequestTeamMemberPosList(const std::vector<uint32>& i_cRequestedIDs)`
- `TradepostSearchResult(const ID32& i_cChar, const TradepostSearchResult_t& i_cTradepostResult)`
- `Ping(uint32 i_nSequence)`
- `ShiftTeamInstance()`
- `SubscriptionFlagsUpdated(const ID32& characterId, int threadId, int subscriptionFlags)`
- `PlayerServicesUpdated(const ID32& characterId, const PlayerServiceMap& playerServiceMap)`
- `PassiveAbilityEquip(uint32 abilityPos, uint32 templateID)`
- `PassiveAbilityUnequip(uint32 abilityPos)`
- `ExitPassiveMode()`
- `EnterPassiveMode()`
- `RequestMassivePvPScoreData()`
- `RequestScorePlayerNames(const std::vector<uint>& i_cPlayerList)`
- `SetVulnerabilityWindowStartTime(const String& i_cCityName, long i_cVulnerabilityWindowStartTime)`
- `RequestRecipeInfo(uint32 levelId, uint32 templateId1, uint32 templateId2, DecryptionKey_c key1, DecryptionKey_c key2)`
- `RequestTeamInvite(const GroupElement_c& i_cWho, const String& i_cMessage)`
- `RequestNewJoinHasHostileInFriends(const GroupElement_c& i_cWho)`
- `RequestTeamInviteWithInviterFactionPoints(const GroupElement_c& inviter, const std::string& message, const FactionsList& factionPoints)`
- `RequestItemSalesData()`
- `RequestItemSaleLimitedItemList()`
- `RequestItemSaleFavoriteData()`
- `SetFakeBillingConnection(bool fake)`
- `AddFavoriteItem(uint32 saleID)`
- `RemoveFavoriteItem(uint32 saleID)`
- `BuyItemSalesItem(uint32 saleID, uint32 couponID)`
- `CSBuyItemReply(uint64 transactionID, uint32 currentBalance, int64 cdlID, int64 uniqueSaleID, uint32 couponID, int32 errorCode, String errorString)`
- `CSRollbackResponse(uint64 transactionID, uint32 currentBalance, int32 errorCode, String errorString)`
- `ExecuteChatCommand(const ID32& target, int chatWindowID, const chatcommands::ChatTreeNodeIDList& matchedNodes, const chatcommands::ParameterMap& parameterMap)`
- `ReplyUpdatedDimensionDB()`
- `RequestUseTeleportSpell(uint32 playfieldId, uint32 teleportId)`
- `UnlockFromPlayfield(uint32 playfieldId)`
- `UpdateMailBank(const ID32& characterId)`
- `SetSelectedTag(uint32 tagId)`
- `SetSelectedBadgeTag(uint32 tagId)`
- `KickPlayerFromGroupPlayfield(const ID32& i_cPlayerID)`
- `WorldStatChanged(uint32 statHash, int value, int type1, int type2, int dimID)`
- `UpdateNoXPGainStat(bool isOn)`
- `SendReport(ID32 reporter, ID32 reported, const String& text, int8 reason)`
- `RunLua(const std::string& luaScript)`

## GameCharAgentProject


### client -> server (13)

- `RequestAggroStatus(const ID32& i_cCharID, const ID32& i_cTargetID)`
- `GetProcDataForID(const ID32& i_cCharacter)`
- `AcceptApprenticeTeacher(const ID32& i_cTeacherID)`
- `DeclineApprenticeTeacher(const ID32& i_cTeacherID)`
- `RequestApprenticeStudent(const ID32& i_cStudentID)`
- `LeaveApprenticeMode()`
- `RequestHitAndMitigationTables(const ID32& target)`
- `StartOfflineTrainingSystem(uint32 featId)`
- `ResetOfflineTrainingSystem()`
- `RefreshEquippedAbilities()`
- `EnableRangerTracking(const ID32& target)`
- `ToggleVanityArmorFilter(int slot, uint32 removeditemOccupySlots, bool toggleState)`
- `SetSprintInputState(bool sprintInput, bool clientCheck)`

## GameCharInterface


### server -> client (157)

- `DispatchUpdateAggroStatus(cID, cCategory)`
- `DispatchUpdateName(nCharID, cName)`
- `DispatchUpdateBindLocation(nPlayfieldInstanceID)`
- `DispatchGuildNotEnoughCash(i_nAmount)`
- `DispatchUpdateCash(i_nCash, i_nFeedback)`
- `DispatchUpdateCashRepository(newCash, feedback)`
- `DispatchMoveInventoryItemToOrFromGuildBank(i_cSrcInv, i_nSrcPos, i_cDstInv, i_nDstPos)`
- `DispatchSetQuestMarker(i_cNPC, i_eMarker)`
- `DispatchChangeItemStackSize(i_cInventoryID, i_nItemPos, i_nValue)`
- `DispatchLearnSpell(i_nSpellID)`
- `DispatchForgetSpell(i_nSpellID)`
- `DispatchTradeskillComplete(i_nRecepieID)`
- `DispatchClearRecipe(i_nRecepieID)`
- `DispatchRemoveRecipesByGroupClient(i_cRecipeList)`
- `DispatchUntrainSingleFeat(i_nFeatID)`
- `DispatchDisplayItemLootChatMessage(acgData)`
- `DispatchSendNPCAutoTradeCashAmount(i_nAmount)`
- `DispatchSetDialogueCamera(i_cTargetID, i_cOther, nShotSize, nShotType, nShotEmphasis, nShotFov, nShotTilt, bAutoface, i_vHeadPaddingY)`
- `DispatchAddTag(tagID)`
- `DispatchRemoveTag(tagID)`
- `DispatchResurrectRequest(i_cResurrectorID, i_nSpellTemplate, i_castSpellOnResurrect)`
- `DispatchSetDbDebugCategory(i_cStr, i_nLevel)`
- `DispatchSetDbDebugLevel(i_nLevel)`
- `DispatchSetItemStat(i_cInventoryID, i_nItemPos, i_nStat, i_nValue)`
- `DispatchAddToItemSlot(i_inventoryID, i_itemPos, i_itemSlot, i_acgItem)`
- `DispatchRemoveFromItemSlot(i_inventoryID, i_itemPos, i_itemSlot)`
- `DispatchRemoveItem(i_cInventoryID, i_nItemPos, i_nAmountToRemove)`
- `DispatchAddItem(i_cInventoryID, i_nItemPos, i_cEntry, i_nFeedback)`
- `DispatchMoveInventoryItemToGuildBank(i_cInventoryID, i_nItemPos, i_nDstPos)`
- `DispatchMoveInventoryItemFromGuildBank(i_cInventoryID, i_nItemPos, i_nDstPos)`
- `DispatchCharacterNameReply(i_cCharID, i_cName)`
- `DispatchValidResurrectionSpots(i_cPosition, i_cValidResSpots)`
- `DispatchReplyPCDebugData(i_cPCData)`
- `DispatchReplyLootForCharacter(i_nTemplateInstance, i_nMonsterLevel, i_cCharThatDidMostDamage, i_vFactor, i_nNumHashes)`
- `DispatchReplyLootForCharacterAddHash(i_nNumber, i_nHash, nChance, nRepeatCount, nItemUse)`
- `DispatchReplyLootForCharacterTemplateClusterOk(i_nHash, i_nMinLvl, i_nMaxLvl, nTemplateType, i_nNumTemplates)`
- `DispatchReplyLootForCharacterTemplateClusterError(i_nHash, i_cError)`
- `DispatchReplyLootForCharacterAddTreasureTemplate(nTemplateInstance, i_nLevel)`
- `DispatchReplySimulateLootForID(i_cDebugLootMap, nCash, nSimulations)`
- `DispatchFormationSetData(i_cFormation)`
- `DispatchFormationClaimPosition(i_cCharID, i_nPositionID, i_bOccupied)`
- `DispatchFormationUpdatePosition(i_nPositionID, i_bOccupied)`
- `DispatchFormationClearPosition(i_nPositionID)`
- `DispatchFormationUpdatePosRot(i_cPos, i_cRot)`
- `DispatchShopData(i_cShopID, i_cShopParentID, i_cShopContent)`
- `DispatchCloseShop(i_cShopID)`
- `DispatchItemSoldToShop(inventoryID, inventoryPos)`
- `DispatchItemBoughtBack(inventoryID, inventoryPos, itemEntry)`
- `DispatchOpenLootBag(i_cLootBagID)`
- `DispatchCloseLootBag(i_cLootBagID)`
- `DispatchDoNeedGreedRollForItemsInLootBag(i_cLootBagID, i_nItemPos, i_cFallbackLootBagItems)`
- `DispatchCancelNeedGreedRollForItemsInLootBag(i_cLootBagID)`
- `DispatchOpenDialogForLockedItemsInLootBag(lootBagID, itemPosList, fallbackLootBagItems)`
- `DispatchCloseDialogForLockedItemsInLootBag(i_cLootBagID)`
- `DispatchMasterLooterOfferItemTo(masterLooterName, i_cLootBagID, i_nItemPos, i_cFallbackLootBagItems)`
- `DispatchSlaveLooterAnswer(i_cLootBagID, i_nItemPos, i_cSlaveID, i_bAccept, i_cFallbackLootBagItems)`
- `DispatchMasterLooterCancelOffer(i_cLootBagID, i_nItemPos, i_cMasterLooterID, i_cFallbackLootBagItems)`
- `DispatchOverrideGameTweaks(overrides)`
- `DispatchShowClientFIFO(remoteFormat)`
- `DispatchAddFormation(i_nFormationID)`
- `DispatchRemoveFormation(i_nFormationID)`
- `DispatchUpgradeCombo(i_nComboID, i_nRecharge, i_nDamage, i_bReverse)`
- `DispatchUpgradeComboStep(i_nComboID, i_nStep, i_nDPSIncrease, i_nCritIncrease, i_nHateIncrease, i_nDamageType, i_bReverse)`
- `DispatchXPSelectClass(i_nClass)`
- `DispatchNextQuestAvailable(nextAwaitingQuest, questName)`
- `DispatchQuestShareGlobalFeedback(i_nFeedbackType, i_nFeedbackValue)`
- `DispatchQuestShareIndividualFeedback(i_nFeedbackType, i_cFeedbackValue)`
- `DispatchQuestShareRequest(i_cCharID, i_nStartTaskTemplate, i_cQuestName)`
- `DispatchUpdateTeamQuestList(cQuestData)`
- `DispatchCancelTeamQuestList(nQuestShareCode)`
- `DispatchUpdateTokenAmount(tokenID, gained, spent, includeBonus)`
- `DispatchLockTemplate(i_nTemplateID, i_vSeconds)`
- `DispatchApplyFactionRank(i_FactionId, i_Rank)`
- `DispatchApplyFactionPoints(i_FactionId, i_Points)`
- `DispatchAddSpecialAbility(i_nAbilityID, i_cItem, i_nType, i_nResourceID, i_bDoFeedback)`
- `DispatchRemoveSpecialAbility(i_nAbilityID, i_cItem)`
- `DispatchPerformSpecialAction(i_nActionPos)`
- `DispatchQueuePetCommand(i_cPetID, i_nPetCommand)`
- `DispatchACGTeleportChoice(i_nPlayfieldModelID, i_cOwnerOfTargetInstanceIfAny, i_cDestination, i_bCanReset)`
- `DispatchACGSelfInstanceCleared(i_nPlayfieldModelID)`
- `DispatchACGTeamBindChanged(i_nPlayfieldModelID, i_cNewCharID)`
- `DispatchOverrideJumpHeight(i_vOverride)`
- `DispatchOverrideJumpSpeed(i_vOverride)`
- `DispatchVisualSimulateNpc(subject, monsterData, headMesh, morphValues, i_nChatWndID)`
- `DispatchReceivedStockPhraseText(i_nThreadContextInstanceNr, i_rfText)`
- `DispatchScryShowMessageBox(cText, nText1, nText2, nText3, nTip1, nTip2, nTip3, nScriptThreadCtxId, cCustomName, nCtxCookie)`
- `DispatchScryCloseMessageBox(nCtxCookie)`
- `DispatchScryShowOptionsBox(nScriptThreadCtxId, nCtxCookie)`
- `DispatchScryCloseOptionsBox(nCtxCookie)`
- `DispatchOpenScenarioResults(packet, context, cookie)`
- `DispatchScrySendCustomGUIData(packet)`
- `DispatchFeatUntrainMessageBox(i_nCategory, nScriptThreadCtxId, nCtxCookie)`
- `DispatchCraftUntrainMessageBox(nCost, nCraftID)`
- `DispatchUnbindItemReply(inventoryID, itemLocation)`
- `DispatchBroadcastMessage(i_cMessage)`
- `DispatchRequestTrade(i_cWhoID)`
- `DispatchStartTrade(i_cWhoID)`
- `DispatchPartnerAcceptedTrade(i_cWhoID)`
- `DispatchPartnerUpdatedCash(i_nCash)`
- `DispatchTradePartnerAddedItem(i_cItem, i_nItemPos)`
- `DispatchTradeAddedItem(i_cItem, i_nItemPos)`
- `DispatchTradePartnerRemovedItem(i_nItemPos)`
- `DispatchTradeRemovedItem(i_nItemPos)`
- `DispatchShortcutBarReply(i_cShortcuts)`
- `DispatchAddWayPoint(i_cID, i_nPlayfieldID, i_cPos, i_vRadius, i_nLabel, i_nToolTip)`
- `DispatchRemoveWayPoint(i_cID, i_nPlayfieldID)`
- `DispatchReplyWaveRespawnTimerUpdate(timeLeft, factionCounter)`
- `DispatchGMReplySpellData(i_cSpellBook)`
- `DispatchGMReplyFeatData(i_cFeatMap)`
- `DispatchGMReplyFeatClassData(i_cFeatClassMap)`
- `DispatchInstanceSelectionForCurrentPlayfield(i_cInstanceList)`
- `DispatchApprenticeModeOnOff(i_bOnOff)`
- `DispatchPong(i_nSequence, i_nServerTime)`
- `DispatchUpdateTeamMemberPosList(i_cTeamMemberPosList)`
- `DispatchDynamicSpaceDebugInfo(i_nLayer, i_cSpaceBounds)`
- `DispatchSetGameTweakValue(i_cName, i_vValue)`
- `DispatchMinigameScoreChanged(i_cUpdatelist)`
- `DispatchMinigameFullScore(i_cScore)`
- `DispatchMinigameMatchStarted(i_nSecondsSinceStarted)`
- `DispatchMinigameMatchEnded(i_eWinningSide, i_eYourResult)`
- `DispatchMinigameCaptureZoneUpdate(i_nSensorVolumeID, i_vCaptureState, i_nCurrentOwnerSide, i_nCurrentOwner, i_bInsideCaptureZone)`
- `DispatchMinigameCaptureZoneList(i_cSensorVolumeIDs, i_cSensorVolumeNames, i_cOwnerVec)`
- `DispatchMinigameCaptureZoneLeaveEnter(i_nSensorVolumeID, i_bLeave)`
- `DispatchForceStopMinigame(i_cMessage)`
- `DispatchSetMinigameTime(i_nNewTimeLimit)`
- `DispatchMinigameStartsInXSeconds(i_nSeconds)`
- `DispatchMassivePvPFullScoreUpdate(i_cMassivePvPScore)`
- `DispatchMassivePvPGuildScoreUpdate(i_cScoreList)`
- `DispatchMassivePvPPlayerScoreUpdate(i_cScoreList)`
- `DispatchMassivePvPScoreNames(i_cNameMap)`
- `DispatchShowVulnerabilityWindowGui(i_cAreaEditInfo)`
- `DispatchPassiveAbilityEquip(abilityPos, templateID)`
- `DispatchPassiveAbilityUnequip(abilityPos)`
- `DispatchReplyRecipeInfo(i_RecipeId, i_Recipe)`
- `DispatchAddEffectPackage(i_nPackageID, i_cTargets)`
- `DispatchEnterExitGamingRoom(entering)`
- `DispatchReplyTeamInvite(i_cWho, i_cMessage, hasHostileInFriends)`
- `DispatchReplyNewJoinHasHostileInFriends(i_cWho)`
- `DispatchReplyItemForCashSaleSetData(data)`
- `DispatchReplyItemForCashSaleUpdateData(data)`
- `DispatchItemSaleResult(saleID, status, externalErrorCode, externalErrorString)`
- `DispatchReplyItemSaleFavoriteData(data)`
- `DispatchItemSalesUpdateBalance(balance)`
- `DispatchPlayfieldAccessDenied(playfieldId)`
- `DispatchPlayfieldAccessGranted(playfieldId, hours, allCharacters)`
- `DispatchSendPlayfieldAccessData(playfieldId, accessInfo)`
- `DispatchSendPlayfieldLockoutData(playfieldId, lockoutInfo)`
- `DispatchLockAllSpells(cooldown, onlyIncrease)`
- `DispatchAddSpellResistance(metaType, percentage)`
- `DispatchRemoveSpellResistance(metaType, percentage)`
- `DispatchSetStackinglineResists(resistsMap)`
- `DispatchPlayerServicesUpdated(playerServiceMap)`
- `DispatchOnFacebookTokenResult(storedToken, shouldAskToSignIntoFacebook)`
- `DispatchOnUpdateFacebookFriends(fbFriends)`
- `DispatchOnFacebookFriendSignedUp(fbFriend)`
- `DispatchRequestGetEffect(nEffect)`
- `DispatchSignalWorldStatChanged(hash, value, type1, type2, dimID)`

## GameCharInterfaceProject


### server -> client (15)

- `DispatchUpdateAggroStatus(cID, cCategory)`
- `DispatchGuildBankData(i_cShopContent, i_eFeedback)`
- `DispatchRequestApprentice(i_cTeacher)`
- `DispatchAddApprenticeEntry(i_cTeacher, i_cStudent)`
- `DispatchRemoveApprenticeEntry(i_cTeacher, i_cStudent)`
- `DispatchDoYouWantToShiftTeamInstance(i_bFailedPreviousInstanceShift)`
- `DispatchInDungeonPlayfield(inDungeon)`
- `DispatchReplyDebugGetProcData(o_cSelfTemplate, o_cOtherTemplate, o_cProcRate, o_cProcFlags, o_cListID, o_cReactive)`
- `DispatchReplyDebugGetSpellProcData(i_cSpellTemplate, i_cStackinglines, o_cSelfTemplate, o_cOtherTemplate, o_cProcRate)`
- `DispatchSendHitAndMitigationTable(tables)`
- `DispatchSetIsSprinting(isSprinting)`
- `DispatchSyncCanSprint(canSprint)`
- `DispatchSyncRegenDelayed(regenDelayed)`
- `DispatchUpdateSprintbar(energyValue, forceUpdate)`
- `DispatchOpenSiteInBrowserOnClient(siteName)`

## GameInterface


### server -> client (13)

- `DispatchPassBlob(b)`
- `DispatchAckAuthenticate(nStatus)`
- `DispatchRedirectToServer(cOtherGameAgentAddr)`
- `DispatchPong(sequenceId)`
- `DispatchPongWithPacketTiming(sequenceId, proxyTiming, dispatchTiming)`
- `DispatchInfoToUser(cInfoFromServer)`
- `DispatchGameStats(cStats)`
- `DispatchGameServerStats(nGSFrameRate, uGSFrameCount, nGSCpuUsage)`
- `DispatchGameServerDataset(projectID, datasetID, datasetName)`
- `DispatchReportDimesionID(cDimId)`
- `DispatchReportServerID(nServerId, nServerIndex)`
- `DispatchDataSink(cMsg)`
- `DispatchDisplayPlayfieldAccessPopup(playfieldID)`

## PlayerAgent


### client -> server (25)

- `Authenticate(ID32::InstanceType nAccount, unsigned int nCookie, unsigned int territoryVersion)`
- `AuthenticateLoggedIn(ID32::InstanceType playerInstance, unsigned int cookie)`
- `LoginFromCrash(uint32 player, uint32 cookie, uint32 character)`
- `CreateCharacter(uint i_nDimID, const std::string& cRDBDSN, const uint16 regionCode, const uint16 ageCode, const uint16 genderCode, const String& igrID, const uint32 userType, const uint32 serviceType)`
- `CreateCharacterWithData(uint dim, StrStrMap& dataMap, const uint16 regionCode, const uint16 ageCode, const uint16 genderCode, const String& igrID, const uint32 userType, const uint32 serviceType)`
- `LoginCharacter(ID32::InstanceType nCharInstance, const StrStrMap& dataMap, const std::string& cClientLanguage, const uint16 regionCode, const uint16 ageCode, const uint16 genderCode, const String& igrID, const uint32 userType, const uint32 serviceType)`
- `RenameCharacter(ID32::InstanceType nCharInstance, String& cNewName)`
- `RequestDeleteCharacter(ID32::InstanceType nCharacterInstance)`
- `VerifyLanguageSetting(const int nClientLanguage)`
- `AddOfflineLevel(ID32::InstanceType nCharInstance)`
- `UnlockCharacter(ID32::InstanceType charInstance)`
- `SetHardcoreLevel(ID32::InstanceType charInstance, int nHardcoreLevel)`
- `GetDimensionList()`
- `RequestSuggestNickname(int i_nRace, int i_nSex)`
- `GetStartupData(const String& md5)`
- `LoadFacebookToken()`
- `SaveFacebookToken(uint64 facebookUserId, const String& tokenToStore)`
- `UpdateFacebookFriends(const std::vector<uint64>& fbFriendIds)`
- `RecruitFacebookFriends(const FacebookNS::FacebookFriendsMap& fbFriends)`
- `UpdateSubscriptionSettings(IOBuffer& data, const String& md5)`
- `UpdateCharacterSettings(const ID32& characterId, IOBuffer& data, const String& md5)`
- `UpdateCharacterPreview(const ID32& characterId, IOBuffer& data)`
- `FinishGetStartupData(const CharacterCheckSumMap& characterMap)`
- `ReceivedCharacterSettings()`
- `ClientReplyKeepCookie()`

## PlayerInterface


### server -> client (25)

- `DispatchOnFacebookTokenResult(storedToken, shouldAskToSignIntoFacebook)`
- `DispatchOnUpdateFacebookFriends(fbFriends)`
- `DispatchOnFacebookFriendSignedUp(fbFriend)`
- `DispatchAuthenticateAck(nStatus)`
- `DispatchLoginConnectReady(gameServerAddr, nCookie, cClientCharacterID, cPF)`
- `DispatchCSServerConnectReady(i_nAddress, nCookie, cClientCharacterID)`
- `DispatchChatServerConnectReady(address, cookie, characterID)`
- `DispatchUpdateClientPlayerData(clientPlayerData, fidData, territoryID)`
- `DispatchSetDimensionList(cDimensionList)`
- `DispatchSetStats(cStatsStr)`
- `DispatchSuggestNicknameResult(cSuggestionName)`
- `DispatchPlayerSetupComplete(nAuthStatus)`
- `DispatchLoginQueuePosition(i_nDimensionID, i_nCharID, i_nQueuePosition)`
- `DispatchOfflineLevelApplied(nCharInstance)`
- `DispatchCharacterUnlocked(charInstance)`
- `DispatchHardcoreLevelSet(nCharInstance, nHardcoreLevel)`
- `DispatchSendSubscriptionSettings(data)`
- `DispatchSendCharacterSettings(characterID, data)`
- `DispatchSendCharacterPreview(characterID, data)`
- `DispatchSendCharacterSettingsCheckSumMap(charMD5Map)`
- `DispatchOverrideGameTweaks(overrides)`
- `DispatchCharacterNeedsNewName(nCharInstance, nLDBReason, cRequestedName)`
- `DispatchLoginProblem(eStatus)`
- `DispatchTeleportProblem(cReason)`
- `DispatchResetLanguage(nNewLanguage)`

## TradepostAgent


### client -> server (20)

- `GetAllMails()`
- `GetMailByID(Instance64_t mailID)`
- `SendMail(const String& cRecpName, const String& cMessageBody, uint64 nMoney, const std::vector<uint>& cBankItemIndices)`
- `MarkMailRead(Instance64_t mailID)`
- `CheckNewMail()`
- `GetMailItems(Instance64_t mailID)`
- `GetMailItem(Instance64_t mailID, Instance64_t itemID)`
- `GetItemData(Instance64_t mailID)`
- `DeleteMail(Instance64_t mailID)`
- `CancelMail(Instance64_t itemID)`
- `TradepostSellItem(uint nBankItemSlot, uint64 nPrice)`
- `TradepostChangeItemPrice(uint nBankItemSlot, uint64 nPrice)`
- `TradepostCancelSellItem(uint nBankItemSlot)`
- `TradepostItemSearch(TradepostSearch_c& cSearch)`
- `TradepostBuyItem(Instance64_t nItemId, uint64 i_nSearchResultID)`
- `TradepostOpen(float vMaxRadius)`
- `TradepostClose()`
- `TradepostUpdateItemTimeouts()`
- `SetAsyncEnabled(bool asyncEnabled)`
- `SetNumWorkerThreads(size_t numWorkers)`

## TradepostInterface


### server -> client (7)

- `DispatchOpenTradepost(cAccessPos, vMaxDistance)`
- `DispatchMailMessage(cMailMessage)`
- `DispatchSendMailResult(bSuccess, cErrorMessage)`
- `DispatchRetrieveMailResult(bSuccess)`
- `DispatchTradepostSearchResults(cSearchResults)`
- `DispatchDeleteCanceledMail(mailId)`
- `DispatchDeleteCanceledMailItem(mailId, itemId)`

## UniverseAgent


### client -> server (3)

- `InitiateAuthentication(const std::string& username, const uint32 nGameID)`
- `AnswerChallenge(const std::string& cAnswerChallenge)`
- `ClientDisconnected(ID32::InstanceType nConnectionID)`

## UniverseInterface


### server -> client (6)

- `DispatchChallenge(data)`
- `DispatchAckAuthenticate(nAuthStatus, cPlayerID, cTerritoryManagerAddr, uCookie, eReason)`
- `DispatchLoginProblem(eLoginStatus)`
- `DispatchSetUpdateManager(cUpdateManagerAddr)`
- `DispatchSetUniverseManagerError(nErrorCode)`
- `DispatchSetRegionState(cRegionData)`
