# AoC RPC method opcodes — recovered from AgeOfConan.exe (Ghidra, 2026-06-13)

Ground-truth RPC method ids, recovered by RE'ing the client. **`wire rendering = 0x2000 + method
registration index`**, and the registration index is per-interface and contiguous from 0. The base is
`0x2000` for every interface EXCEPT PlayerAgent/PlayerInterface (see the note at the end).

> **Live on the wire (2026-07-08):** the Wireshark plugin resolves these names per packet
> (`faolan.pkt.method`, COL_INFO `GameCharAgent TryLootItem`) via tables generated at build time from
> the curated C# catalogs in `Faolan.Core/Enums/Opcodes.cs` — that file, not this doc's
> tables, is the canonical machine-readable form (`Faolan.Wireshark/scripts/gen_opcode_names.py`).
> Regenerated `*.tshark.json` dumps carry the name as an additive `Method` key.

Recovery method (reproducible — scripts in `tools/ghidra/`):
- **c2s (Agent/Proxy):** each `<Class>AgentProxy_t` constructor registers its methods in order via repeated
  `register(); setName("Method", len)` calls (decompiled from the `"...Proxy_t::called with a too low id!
  min allowed"` constructors). The registration order is identical to the address order of the
  `"<Class>Proxy_t::<Method>(...) streamed payload of size"` log strings — i.e. **identical to the order
  already in `aoc-client-rpc-methods.json`**. Confirmed for all 7 c2s interfaces.
- **s2c (Interface/Dispatch):** each `<Class>InterfaceDispatch_t::DispatchMethod(idx)` is a `switch(idx)`;
  case N -> handler N. Each handler references its own `"<Class>Dispatch_t::<Method>() ... stream bad after"`
  string, giving case->name. **This is the fix for the s2c "drift":** the live build inserts extra
  dispatch methods (mostly no-arg, so they emit no `stream bad` string and were missing from the string-
  extracted catalog), which shifted every later method. The switch is the authoritative table.

## Validation anchors (all match ground truth)
- c2s GameCharAgent: shop CloseShop/BuyItem/SellItem = 0x2032/33/34; loot TryLootAll/CloseLootBag/
  TryLootCash = 0x2043/44/45; c2s GameAgent PassBlob/ChatVicinity/Authenticate2 = 0x2000/02/03;
  c2s CSPlayerAgent Authenticate=0x2000, AcceptGuildInvitation=0x201C; c2s UniverseAgent InitiateAuth=0x2000.
- s2c GameCharInterface: DispatchUpdateBindLocation=0x2002, **DispatchUpdateCash=0x2005**, DispatchShopData
  =0x2033, DispatchItemSoldToShop=0x2035, DispatchAddSpecialAbility=0x2052, DispatchPong=0x2082 — all match
  the in-client-verified server behavior.

## s2c (server->client) tables
`<no-arg>` = an inserted method with no body fields (no `stream bad` string); its opcode slot is known
but its name is not recoverable from strings (catalog also missed it). These are the drift insertions.

### UniverseInterface — 6 methods (catalog had 6; +0 inserted)
```
0x2000  DispatchChallenge
0x2001  DispatchAckAuthenticate
0x2002  DispatchLoginProblem
0x2003  DispatchSetUpdateManager
0x2004  DispatchSetUniverseManagerError
0x2005  DispatchSetRegionState
```

### GameInterface — 16 methods (catalog had 13; +3 inserted)
```
0x2000  DispatchPassBlob
0x2001  DispatchAckAuthenticate
0x2002  DispatchRedirectToServer
0x2003  <no-arg>
0x2004  <no-arg>
0x2005  DispatchPong
0x2006  DispatchPongWithPacketTiming
0x2007  DispatchInfoToUser
0x2008  DispatchGameStats
0x2009  DispatchGameServerStats
0x200A  DispatchGameServerDataset
0x200B  DispatchReportDimesionID
0x200C  DispatchReportServerID
0x200D  DispatchDataSink
0x200E  <no-arg>
0x200F  DispatchDisplayPlayfieldAccessPopup
```

### GameCharInterfaceProject — 16 methods (catalog had 15; +1 inserted)
```
0x2000  DispatchUpdateAggroStatus
0x2001  DispatchGuildBankData
0x2002  <no-arg>
0x2003  DispatchRequestApprentice
0x2004  DispatchAddApprenticeEntry
0x2005  DispatchRemoveApprenticeEntry
0x2006  DispatchDoYouWantToShiftTeamInstance
0x2007  DispatchInDungeonPlayfield
0x2008  DispatchReplyDebugGetProcData
0x2009  DispatchReplyDebugGetSpellProcData
0x200A  DispatchSendHitAndMitigationTable
0x200B  DispatchSetIsSprinting
0x200C  DispatchSyncCanSprint
0x200D  DispatchSyncRegenDelayed
0x200E  DispatchUpdateSprintbar
0x200F  DispatchOpenSiteInBrowserOnClient
```

### TradepostInterface — 8 methods (catalog had 7; +1 inserted)
```
0x2000  DispatchOpenTradepost
0x2001  DispatchClose        (inserted slot; name later recovered from the TradepostInterface dispatch RE)
0x2002  DispatchMailMessage
0x2003  DispatchSendMailResult
0x2004  DispatchRetrieveMailResult
0x2005  DispatchTradepostSearchResults
0x2006  DispatchDeleteCanceledMail
0x2007  DispatchDeleteCanceledMailItem
```

### GameCharInterface — 181 methods (catalog had 157; +24 inserted)
```
0x2000  DispatchUpdateAggroStatus
0x2001  DispatchUpdateName
0x2002  DispatchUpdateBindLocation
0x2003  DispatchGuildNotEnoughCash
0x2004  <no-arg>
0x2005  DispatchUpdateCash
0x2006  DispatchUpdateCashRepository
0x2007  DispatchMoveInventoryItemToOrFromGuildBank
0x2008  DispatchSetQuestMarker
0x2009  DispatchChangeItemStackSize
0x200A  DispatchLearnSpell
0x200B  DispatchForgetSpell
0x200C  <no-arg>
0x200D  DispatchTradeskillComplete
0x200E  <no-arg>
0x200F  DispatchClearRecipe
0x2010  DispatchRemoveRecipesByGroupClient
0x2011  DispatchUntrainSingleFeat
0x2012  DispatchDisplayItemLootChatMessage
0x2013  <no-arg>
0x2014  DispatchSendNPCAutoTradeCashAmount
0x2015  DispatchSetDialogueCamera
0x2016  DispatchAddTag
0x2017  DispatchRemoveTag
0x2018  DispatchResurrectRequest
0x2019  DispatchSetDbDebugCategory
0x201A  DispatchSetDbDebugLevel
0x201B  <no-arg>
0x201C  DispatchSetItemStat
0x201D  DispatchAddToItemSlot
0x201E  DispatchRemoveFromItemSlot
0x201F  DispatchRemoveItem
0x2020  DispatchAddItem
0x2021  DispatchMoveInventoryItemToGuildBank
0x2022  DispatchMoveInventoryItemFromGuildBank
0x2023  DispatchCharacterNameReply
0x2024  DispatchValidResurrectionSpots
0x2025  <no-arg>
0x2026  DispatchReplyPCDebugData
0x2027  DispatchReplyLootForCharacter
0x2028  DispatchReplyLootForCharacterAddHash
0x2029  DispatchReplyLootForCharacterTemplateClusterOk
0x202A  DispatchReplyLootForCharacterTemplateClusterError
0x202B  DispatchReplyLootForCharacterAddTreasureTemplate
0x202C  DispatchReplySimulateLootForID
0x202D  DispatchFormationSetData
0x202E  DispatchFormationClaimPosition
0x202F  DispatchFormationUpdatePosition
0x2030  DispatchFormationClearPosition
0x2031  <no-arg>
0x2032  DispatchFormationUpdatePosRot
0x2033  DispatchShopData
0x2034  DispatchCloseShop
0x2035  DispatchItemSoldToShop
0x2036  DispatchItemBoughtBack
0x2037  DispatchOpenLootBag
0x2038  DispatchCloseLootBag
0x2039  DispatchDoNeedGreedRollForItemsInLootBag
0x203A  DispatchCancelNeedGreedRollForItemsInLootBag
0x203B  DispatchOpenDialogForLockedItemsInLootBag
0x203C  DispatchCloseDialogForLockedItemsInLootBag
0x203D  DispatchMasterLooterOfferItemTo
0x203E  DispatchSlaveLooterAnswer
0x203F  DispatchMasterLooterCancelOffer
0x2040  DispatchOverrideGameTweaks
0x2041  DispatchShowClientFIFO
0x2042  DispatchAddFormation
0x2043  DispatchRemoveFormation
0x2044  DispatchUpgradeCombo
0x2045  DispatchUpgradeComboStep
0x2046  DispatchXPSelectClass
0x2047  DispatchNextQuestAvailable
0x2048  DispatchQuestShareGlobalFeedback
0x2049  DispatchQuestShareIndividualFeedback
0x204A  DispatchQuestShareRequest
0x204B  DispatchUpdateTeamQuestList
0x204C  DispatchCancelTeamQuestList
0x204D  DispatchUpdateTokenAmount
0x204E  DispatchLockTemplate
0x204F  DispatchApplyFactionRank
0x2050  DispatchApplyFactionPoints
0x2051  <no-arg>
0x2052  DispatchAddSpecialAbility
0x2053  DispatchRemoveSpecialAbility
0x2054  DispatchPerformSpecialAction
0x2055  DispatchQueuePetCommand
0x2056  DispatchACGTeleportChoice
0x2057  DispatchACGSelfInstanceCleared
0x2058  DispatchACGTeamBindChanged
0x2059  <no-arg>
0x205A  DispatchOverrideJumpHeight
0x205B  DispatchOverrideJumpSpeed
0x205C  DispatchVisualSimulateNpc
0x205D  DispatchReceivedStockPhraseText
0x205E  DispatchScryShowMessageBox
0x205F  DispatchScryCloseMessageBox
0x2060  DispatchScryShowOptionsBox
0x2061  DispatchScryCloseOptionsBox
0x2062  DispatchOpenScenarioResults
0x2063  DispatchScrySendCustomGUIData
0x2064  DispatchFeatUntrainMessageBox
0x2065  DispatchCraftUntrainMessageBox
0x2066  DispatchUnbindItemReply
0x2067  DispatchBroadcastMessage
0x2068  DispatchRequestTrade
0x2069  DispatchStartTrade
0x206A  <no-arg>
0x206B  <no-arg>
0x206C  DispatchPartnerAcceptedTrade
0x206D  <no-arg>
0x206E  DispatchPartnerUpdatedCash
0x206F  DispatchTradePartnerAddedItem
0x2070  DispatchTradeAddedItem
0x2071  <no-arg>
0x2072  <no-arg>
0x2073  DispatchTradePartnerRemovedItem
0x2074  DispatchTradeRemovedItem
0x2075  <no-arg>
0x2076  DispatchShortcutBarReply
0x2077  <no-arg>
0x2078  <no-arg>
0x2079  <no-arg>
0x207A  DispatchAddWayPoint
0x207B  DispatchRemoveWayPoint
0x207C  DispatchReplyWaveRespawnTimerUpdate
0x207D  DispatchGMReplySpellData
0x207E  DispatchGMReplyFeatData
0x207F  DispatchGMReplyFeatClassData
0x2080  DispatchInstanceSelectionForCurrentPlayfield
0x2081  DispatchApprenticeModeOnOff
0x2082  DispatchPong
0x2083  DispatchUpdateTeamMemberPosList
0x2084  DispatchDynamicSpaceDebugInfo
0x2085  DispatchSetGameTweakValue
0x2086  <no-arg>
0x2087  DispatchMinigameScoreChanged
0x2088  DispatchMinigameFullScore
0x2089  <no-arg>
0x208A  DispatchMinigameMatchStarted
0x208B  DispatchMinigameMatchEnded
0x208C  DispatchMinigameCaptureZoneUpdate
0x208D  DispatchMinigameCaptureZoneList
0x208E  DispatchMinigameCaptureZoneLeaveEnter
0x208F  DispatchForceStopMinigame
0x2090  DispatchSetMinigameTime
0x2091  DispatchMinigameStartsInXSeconds
0x2092  <no-arg>
0x2093  DispatchMassivePvPFullScoreUpdate
0x2094  DispatchMassivePvPGuildScoreUpdate
0x2095  DispatchMassivePvPPlayerScoreUpdate
0x2096  DispatchMassivePvPScoreNames
0x2097  <no-arg>
0x2098  <no-arg>
0x2099  DispatchShowVulnerabilityWindowGui
0x209A  DispatchPassiveAbilityEquip
0x209B  DispatchPassiveAbilityUnequip
0x209C  DispatchReplyRecipeInfo
0x209D  DispatchAddEffectPackage
0x209E  DispatchEnterExitGamingRoom
0x209F  DispatchReplyTeamInvite
0x20A0  DispatchReplyNewJoinHasHostileInFriends
0x20A1  <no-arg>
0x20A2  DispatchReplyItemForCashSaleSetData
0x20A3  DispatchReplyItemForCashSaleUpdateData
0x20A4  DispatchItemSaleResult
0x20A5  DispatchReplyItemSaleFavoriteData
0x20A6  DispatchItemSalesUpdateBalance
0x20A7  DispatchPlayfieldAccessDenied
0x20A8  DispatchPlayfieldAccessGranted
0x20A9  DispatchSendPlayfieldAccessData
0x20AA  DispatchSendPlayfieldLockoutData
0x20AB  DispatchLockAllSpells
0x20AC  DispatchAddSpellResistance
0x20AD  DispatchRemoveSpellResistance
0x20AE  DispatchSetStackinglineResists
0x20AF  DispatchPlayerServicesUpdated
0x20B0  DispatchOnFacebookTokenResult
0x20B1  DispatchOnUpdateFacebookFriends
0x20B2  DispatchOnFacebookFriendSignedUp
0x20B3  DispatchRequestGetEffect
0x20B4  DispatchSignalWorldStatChanged
```

### CSPlayerInterface — 211 methods (catalog had 178; +33 inserted)
```
0x2000  DispatchAuthenticateAck
0x2001  DispatchTeamInvite
0x2002  DispatchTargetIsBusy
0x2003  DispatchTargetIsCriminal
0x2004  DispatchTeamCharacterApplied
0x2005  DispatchYouAlreadySentRequest
0x2006  DispatchTargetIsAlreadyInTeam
0x2007  DispatchTeamInvitationTimedOut
0x2008  DispatchTargetDeclinedTeamInvite
0x2009  DispatchYouAreNoLongerInvited
0x200A  DispatchYouInvitedToTeam
0x200B  DispatchInviteWithInviterFactionPoints
0x200C  DispatchExtendedCharacterInfo
0x200D  DispatchExtendedCharacterInfoNotFound
0x200E  DispatchYouJoinedTeam
0x200F  DispatchTeamSearchResult
0x2010  DispatchCharacterSearchResult
0x2011  DispatchCharacterNameSearchResult
0x2012  DispatchTeamLeave
0x2013  DispatchTeamJoin
0x2014  DispatchTeamMemberUpdate
0x2015  DispatchTeamDisband
0x2016  DispatchSetNewTeamLeader
0x2017  DispatchTeamLootOptionChanged
0x2018  DispatchTeamMasterLooterChanged
0x2019  DispatchTeamSummonRequest
0x201A  DispatchTeamSummonTimedOut
0x201B  DispatchYouAreNoLongerSummoned
0x201C  DispatchTeamSummonRequestDeclined
0x201D  DispatchTeamSummonRequestAccepted
0x201E  DispatchTeamSummonRequestFailedInvalidPF
0x201F  DispatchTeamSummonRequestFailedAlreadyInSameInstance
0x2020  DispatchWarningTeamSizeTooLarge
0x2021  <no-arg>
0x2022  DispatchYouGotKickedFromTeam
0x2023  DispatchVoteKickStarted
0x2024  DispatchVoteKickFailed
0x2025  DispatchVoteKickEnded
0x2026  DispatchCharacterLookingForTeamStatus
0x2027  DispatchTeamLookingForMemberStatus
0x2028  DispatchDeleteCanceledMail
0x2029  DispatchDeleteCanceledMailItem
0x202A  DispatchGuildInvite
0x202B  <no-arg>
0x202C  <no-arg>
0x202D  <no-arg>
0x202E  DispatchGuildMemberLeft
0x202F  DispatchGuildMemberJoined
0x2030  DispatchGuildMemberUpdate
0x2031  DispatchGuildInviteReplyToInviter
0x2032  DispatchGuildGMPromoteFeedback
0x2033  DispatchGuildGMDemoteFeedback
0x2034  DispatchGuildInviteNoSuchName
0x2035  <no-arg>
0x2036  DispatchGuildPvPPointInfo
0x2037  DispatchGuildRenownInfo
0x2038  DispatchGuildRankingsUpdate
0x2039  DispatchGuildRenownHistoryUpdate
0x203A  DispatchClientAddGuildRenown
0x203B  DispatchInformAboutRankings
0x203C  DispatchInformAboutRankingWinners
0x203D  DispatchRankingCaluculationCountdown
0x203E  DispatchPersonalRenownCapReached
0x203F  DispatchGuildUpdate
0x2040  DispatchGuildRenownUpdate
0x2041  DispatchGuildName
0x2042  DispatchGuildUpdateMember
0x2043  DispatchGuildUpdateOther
0x2044  DispatchGuildCreated
0x2045  DispatchGuildNonAlphanumericName
0x2046  <no-arg>
0x2047  <no-arg>
0x2048  DispatchGuildBadName
0x2049  DispatchGuildNameWaitTime
0x204A  <no-arg>
0x204B  <no-arg>
0x204C  <no-arg>
0x204D  <no-arg>
0x204E  DispatchGuildOnInviteCooldown
0x204F  <no-arg>
0x2050  DispatchGuildTaxError
0x2051  DispatchGuildInvalidGoverningForm
0x2052  DispatchGuildUpdateCash
0x2053  <no-arg>
0x2054  DispatchGuildTaxChanged
0x2055  DispatchGuildGoverningFormChanged
0x2056  DispatchGuildRecruitmentModeChanged
0x2057  DispatchGuildMessageOfTheDayChanged
0x2058  <no-arg>
0x2059  DispatchGuildNameChanged
0x205A  DispatchGMGuildNameChanged
0x205B  DispatchGuildMemberList
0x205C  DispatchGuildSearchResult
0x205D  DispatchGuildSetPvPPoints
0x205E  DispatchGuildSetPvPKillPoints
0x205F  DispatchGuildSetPvPTotalKills
0x2060  DispatchGuildSetPvPSeasonalKills
0x2061  DispatchGuildSetGuildWar
0x2062  DispatchGuildSetGuildAsDefenderOfWar
0x2063  DispatchAddGuildFeat
0x2064  DispatchRemoveGuildFeat
0x2065  DispatchGuildRankChanged
0x2066  DispatchGuildApplicationRequest
0x2067  <no-arg>
0x2068  DispatchGuildApplicationRejected
0x2069  DispatchGuildMergeRequest
0x206A  DispatchGuildMergeRequestFailed
0x206B  DispatchGuildCityBooked
0x206C  DispatchGuildCityBookingFailed
0x206D  DispatchNewVulnerabilityWindowData
0x206E  <no-arg>
0x206F  DispatchGuildIsMassivePvPCityOwner
0x2070  DispatchGuildIsMassivePvPCityAttacker
0x2071  DispatchFeedbackMessage
0x2072  DispatchRequestBattleKeepInformation
0x2073  DispatchGMReplyGuildInfoFromChar
0x2074  <no-arg>
0x2075  DispatchPvPMatchMakingWantsToStart
0x2076  DispatchPvPMatchMakingPlayerJoined
0x2077  DispatchPvPMatchMakingPlayerChangedSide
0x2078  DispatchPvPMatchMakingPlayerLeft
0x2079  DispatchPvPMatchMakingPlayerKicked
0x207A  DispatchPvPMatchMakingErrorCode
0x207B  DispatchPvPMatchMakingYouJoinedMatch
0x207C  <no-arg>
0x207D  DispatchPvPMatchMakingMatchInfo
0x207E  DispatchPvPMatchMakingSearchResult
0x207F  DispatchPvPMatchMakingStartClicked
0x2080  DispatchPvPMatchMakingYouAreInMatchMaking
0x2081  <no-arg>
0x2082  <no-arg>
0x2083  DispatchPvPMatchMakingMatchSay
0x2084  DispatchPvPMatchMakingCollectingMorePlayers
0x2085  DispatchPvPMatchMakingNewMatchLeader
0x2086  DispatchPvPMatchMakingTeamLeaderSignedUpForMatchMaking
0x2087  <no-arg>
0x2088  <no-arg>
0x2089  DispatchPvPMatchMakingCantStartMatchNotAllPlayersHaveSide
0x208A  DispatchPvPMatchMakingCantLockToSideMustPickASideFirst
0x208B  DispatchPvPMatchMakingCantChangeSideYouHaveBeenLockedToSide
0x208C  DispatchPvPMatchMakingCharacterNowLockedToSide
0x208D  DispatchPvPMatchMakingCharacterNoLongerLockedToSide
0x208E  <no-arg>
0x208F  <no-arg>
0x2090  <no-arg>
0x2091  <no-arg>
0x2092  DispatchPvPMatchMakingInfoRequestReturn
0x2093  DispatchYouJoinedRaid
0x2094  DispatchRaidTeamJoined
0x2095  DispatchRaidTeamLeft
0x2096  DispatchRaidTeamUpdate
0x2097  DispatchRaidCharacterUpdate
0x2098  DispatchRaidNewRaidLeader
0x2099  DispatchRaidLootOptionChanged
0x209A  DispatchRaidNewMasterLooter
0x209B  DispatchRaidInvite
0x209C  DispatchRaidTargetIsBusy
0x209D  DispatchRaidIsAlreadyInRaid
0x209E  DispatchRaidInvitationTimedOut
0x209F  DispatchRaidTargetDeclinedRaidInvite
0x20A0  DispatchRaidYouAreNoLongerInvited
0x20A1  <no-arg>
0x20A2  DispatchRaidCantInviteInvalidSubscriptionFlags
0x20A3  <no-arg>
0x20A4  DispatchRaidCantCreateInvalidMemberSubscriptionFlags
0x20A5  DispatchMassivePvpInfoRequestReturn
0x20A6  DispatchMassivePvPBattleQueueFeedback
0x20A7  DispatchMassivePvpStartCountDown
0x20A8  DispatchMassivePvpBattleQueueStatusUpdate
0x20A9  DispatchBattleKeepInfoRequestReturnPhase1And2
0x20AA  DispatchBattleKeepInfoRequestReturnPhase3And4
0x20AB  DispatchYouGotMailNotification
0x20AC  <no-arg>
0x20AD  DispatchDebugFullGameServerList
0x20AE  DispatchDebugGameServerConnected
0x20AF  DispatchDebugGameServerDisconnected
0x20B0  DispatchDebugClientConnected
0x20B1  DispatchDebugClientChanged
0x20B2  DispatchDebugClientDisconnected
0x20B3  DispatchDebugFullClientList
0x20B4  DispatchDebugFullTeamListBurst
0x20B5  DispatchDebugAddTeam
0x20B6  DispatchDebugRemoveTeam
0x20B7  DispatchDebugUpdateTeam
0x20B8  DispatchDebugFullMatchListBurst
0x20B9  DispatchDebugAddMatch
0x20BA  DispatchDebugMatchChanged
0x20BB  DispatchDebugRemoveMatch
0x20BC  DispatchDebugAddIDToMatchMaking
0x20BD  DispatchDebugRemoveIDFromMatchMaking
0x20BE  DispatchItemSalesUpdateBalance
0x20BF  DispatchItemSalesCoupons
0x20C0  <no-arg>
0x20C1  DispatchCSFeedback
0x20C2  DispatchGroupFinderSignupFailed
0x20C3  <no-arg>
0x20C4  DispatchGroupFinderSignupSuccess
0x20C5  DispatchGroupFinderRemoved
0x20C6  DispatchGroupFinderReadyCheck
0x20C7  <no-arg>
0x20C8  <no-arg>
0x20C9  DispatchGroupFinderReadyCheckStatus
0x20CA  DispatchGroupFinderReadyCheckRejected
0x20CB  DispatchGroupFinderPlayfieldInfo
0x20CC  DispatchGroupFinderFeaturedPlayfieldInfo
0x20CD  DispatchGroupFinderFeaturedRaidNeededRoleUpdated
0x20CE  DispatchGroupFinderReceivedNeededRole
0x20CF  DispatchGroupFinderReceivedRotatingActivityChildInfo
0x20D0  DispatchGroupFinderUpdateFeaturedActivities
0x20D1  DispatchReturnWorldStat
0x20D2  DispatchInvalidWorldStat
```

### PlayerInterface / PlayerAgent — DIFFERENT dispatch scheme (hash id ON THE WIRE), #320

**Corrected 2026-07 (verified against the wire + Ghidra; supersedes the earlier "hash not recovered" note).**

Unlike every other interface, PlayerAgent/PlayerInterface looked like it carried **two** ids in its header:
`[endpoints][u16 opcode][u32 method-id]`, with that trailing `u32` **empty for all 16 other interfaces** and present
only here. ⚠️ Superseded by #610: the header carries ONE id, `method_instance` (a varint), and the "two" were that
varint cut after its first group — long here because these constants are 32-bit hashes rather than small indices. Confirmed on all 13 PlayerAgent/PlayerInterface packets (both
directions) of `Barbarian-login` — e.g. EnterWorld = opcode `0x20B6` + id `0xDD85FB0E`; RequestSmallCharList =
`0x20C1` + `0xDAD1D206`. The server already emits it (`WriteHeader(..., InitAuth, [0x93,0x86,0xee,0x05])` =
`0x9386ee05`). So the `//0x…`-style comments in `Opcodes.cs` are the **wire** id, not a client-internal value.
The two bytes a dump shows are the usual `0x2000 + first varint group` (readings 0x2083..0x20EF) carried alongside; the client dispatches
on the 32-bit id.

**s2c POST-AUTH table recovered** (`PlayerInterfaceDispatch_t::DispatchMethod` = `FUN_00484270`, a binary-search
tree over 32-bit constants; `DumpDispatch.java` + `DumpHandlerNames.java`). 26 methods, e.g. `0x5DC18991`
DispatchAuthenticateAck, `0xA7C86A29` DispatchSetStats, `0xF899B14C` DispatchSetDimensionList. Full table:
`$FAOLAN_GHIDRA_OUT/playerinterface_postauth_s2c.json` (external, not committed - the scripts write to
that env var, falling back to the system temp dir).

**c2s method list recovered** (`PlayerAgentProxy_t`, registration order, `DumpRpcTables.java`): 25 methods —
Authenticate, AuthenticateLoggedIn, LoginFromCrash, CreateCharacter, LoginCharacter (= our EnterWorld),
RenameCharacter, RequestDeleteCharacter, … The per-method opcode/hash are NOT inline in the proxy send
(`DumpPlayerAgentProxy.java` shows the send only marshals params) — they come from the RPC registration.

**The login (pre-auth) id encoding is the remaining open piece.** The 26-method tree above is **post-auth only**
(`DispatchMethod` gates on an is-authenticated flag and errors `"not possible before being authenticated"`
otherwise), so the login methods we actually use (Authenticate/EnterWorld/InitAuth/SendCharacterList/…) are
dispatched by a separate pre-auth path and their wire ids (`0x9386EE05`, `0xDAD1D206`, …) appear in **none** of
the 26 tree constants. Two hard facts about those login ids:
- They are **not stored anywhere in `AgeOfConan.exe`** — absent as instruction immediates (`FindConst.java`,
  0 hits) and as static data in either endianness (raw byte scan, 0 hits). So they are **computed at runtime**.
- They are **not a plain name-hash**: 25 clean (name→id) anchors from the post-auth table were tested against
  crc32 / djb2(±xor) / sdbm / fnv1(a) / jenkins / elf / bkdr(9 seeds) over 5 string forms — **zero** matches
  (not even 2/25). And every login id has a **small final wire byte** (`…09,…06,…0d,…0e,…05,…0c,…0f,…04,…02`;
  13/13 in `[0x02,0x0F]`) — statistically impossible for a uniform hash, so the id is structured
  `[24-bit hash][small tag byte]`, not a raw hash.

**c2s catalog + dispatch MECHANISM recovered** (Ghidra 2026-07, `DumpPlayerAgentProxy.java` +
`DumpPlayerAgentCtor.java`). The `PlayerAgentProxy_t` constructor (`FUN_00493c40`) registers all **25** c2s
methods, each as `key = 0x…; register(); setName("Method", len)`:

| method | internal key | | method | internal key |
|---|---|---|---|---|
| Authenticate | `0x9CB2CB03` | | RequestSuggestNickname | `0xDBE172E5` |
| AuthenticateLoggedIn | `0x9D991E6C` | | GetStartupData | `0x6A546D41` |
| LoginFromCrash | `0xCBDE126E` | | LoadFacebookToken | `0xD3907026` |
| CreateCharacter | `0xBE735486` | | SaveFacebookToken | `0xD8AD362B` |
| CreateCharacterWithData | `0xCA2C4E5E` | | UpdateFacebookFriends | `0x3A96B2B3` |
| LoginCharacter | `0xEF616EB6` | | RecruitFacebookFriends | `0x5B51044B` |
| RenameCharacter | `0x1B2CF268` | | UpdateSubscriptionSettings | `0x516C9EF5` |
| RequestDeleteCharacter | `0xC3C5C31D` | | UpdateCharacterSettings | `0x01035784` |
| VerifyLanguageSetting | `0x9847AED6` | | UpdateCharacterPreview | `0x4D661656` |
| AddOfflineLevel | `0x2976E1B7` | | FinishGetStartupData | `0xDFD8518E` |
| UnlockCharacter | `0x9C370D1E` | | ReceivedCharacterSettings | `0x3C7C926C` |
| SetHardcoreLevel | `0x6A6B621C` | | ClientReplyKeepCookie | `0x43D90C1B` |
| GetDimensionList | `0x43476328` | | | |

(Full JSON: `$FAOLAN_GHIDRA_OUT/playeragent_c2s_catalog.json`, external.) The **mechanism**: `register`
(`FUN_00487900`) inserts the method into a **binary tree keyed by that internal key**; the proxy send passes the
key to `rpc::Proxy_t::SendMethodInvocation` (`FUN_0046c910`), which builds the `RpcHeader`. The **internal key is
what each proxy method passes at its send call** (matches the constants dumped from the method bodies).

**The wire (opcode, id) ← key/name transform is the ONE remaining unknown, and it is genuinely obscured:**
- The internal **key is itself not a standard name-hash** (25 clean name→key anchors tested against crc32/djb2±/
  sdbm/fnv1(a)/jenkins/oat/elf/bkdr(11 seeds) × 5 string forms — 0 matches, not even 2/25).
- The **wire id is neither the key nor a name-hash**, is not stored in the exe, and carries the small tag byte;
  it is assembled inside `SendMethodInvocation`/`RpcHeader` construction from the tree-node data, not a one-liner.
- So enumerating the *unobserved* methods' wire (opcode, id) needs a deeper trace of `RpcHeader` construction
  (`FUN_0046c910` → the tree-node fields it reads), or a per-method in-client capture. The **observed** wire
  (opcode, id) pairs are already in `Opcodes.cs` / `PlayerAgentMethodIds` (capture-verified, what we use).

Until the transform is cracked the login opcodes+ids stay behavior-derived (they work); the names above are the
proven client method set but their name↔opcode link is not yet established (so no rename — #320).

**SOLVED for the login flow 2026-07 (Frida-under-Wine).** Static RE stalled on the key→wire transform, so instead
we hooked `SendMethodInvocation` (`FUN_0046c910` @ `0x0046C910`) with Frida during a real login and logged each
call's internal key (→ name via the constructor table above), then correlated by SEND ORDER with the wire
`(opcode, id)` from a simultaneous loopback capture. `SendMethodInvocation` is the GENERAL proxy send — for
PlayerAgent it passes the 32-bit key, for the other interfaces the small method index — so the PlayerAgent calls
fall out cleanly. The login exercised 7 c2s methods, giving a **proven `name → (opcode, wire-id)`** table:

| client method | opcode | wire id | (our old name) |
|---|---|---|---|
| `Authenticate` | `0x2083` | `0x96CBE509` | RequestAuth |
| `GetStartupData` | `0x20C1` | `0xDAD1D206` | RequestSmallCharList — **was wrong** |
| `FinishGetStartupData` | `0x208E` | `0xA3E1FE0D` | RequestCharRealmData — **was wrong** |
| `LoadFacebookToken` | `0x20A6` | `0xE0C19C0D` | Ox20A6 |
| `VerifyLanguageSetting` | `0x20D6` | `0xDD9EC209` | Ox20D6 |
| `LoginCharacter` | `0x20B6` | `0xDD85FB0E` | EnterWorld |
| `ReceivedCharacterSettings` | `0x20EC` | `0xA4F2E303` | Ox20EC |

All seven wire ids matched the capture-derived enum exactly (independent confirmation), and `Opcodes.cs` +
`PlayerAgentMethodIds` are renamed to these proven client names. Method: `tools/frida/` pattern + a hook on
`0x0046C910`; the c2s names come from the `PlayerAgentProxy_t` constructor keys above.

**Remaining:** the c2s methods NOT sent during a plain login (CreateCharacter/RenameCharacter/Delete/Facebook-
save/hardcore/offline-level/…) — re-run the same Frida hook while performing those actions in-client — and the
**s2c PlayerInterface** login response names (hook the client's pre-auth receive dispatch). Both are more of the
same technique; the login path (what we implement) is now proven.

## PlayerAgent / PlayerInterface: the method id is DERIVED (#320, 2026-08-03)

This interface pair carries a method id in its RPC header after the 16-bit opcode. Two things about it kept the
interface from being enumerated, and both were wrong:

1. **It is a protobuf VARINT, not a fixed `u32`.** All 22 distinct header tails in the corpus are valid varints —
   including the 1-byte `0x01` and the 3-byte `0x94A760` that the grammar listed as separate "constant" cases. The
   giveaway is that the last byte of a 4-byte tail is always `0x02`–`0x0F`: those are the top bits of a 25-bit
   varint, not the low byte of a hash.
2. **The value is computed, not stored.** No id appears anywhere in `AgeOfConan.exe`, which is why searching for a
   table found nothing. The client registers each method with a 32-bit constant and the wire carries
   **`constant >> 7`**. That rule reproduces every observed id — 13 c2s and 9 s2c, 22 of 22, no exception.

⚠️ **The 16-bit opcode is a per-CONNECTION registration index on this interface, not a method key.** The corpus shows
it directly: on a second PlayerAgent connection retail sends `0x20EC` for `AuthenticateLoggedIn` and `0x20D6` for
`UpdateCharacterPreview`, where the login connection used those same opcodes for `ReceivedCharacterSettings` and
`VerifyLanguageSetting`. The client dispatches on the id. The "observed opcode" column below is therefore what
retail's login connection happened to use, not a general mapping.

Re-derive both tables from the client:

```zsh
GH=<data-repo>/Ghidra
# c2s - PlayerAgentProxy_t's constructor
/opt/ghidra/support/analyzeHeadless "$GH" aoc -process AgeOfConan.exe -noanalysis \
  -scriptPath tools/ghidra -postScript DumpPlayerAgentCtor.java
# s2c - the PlayerInterface registration block
STR=AuthenticateAck MAX=3 /opt/ghidra/support/analyzeHeadless "$GH" aoc -process AgeOfConan.exe -noanalysis \
  -scriptPath tools/ghidra -postScript StrXrefs.java
```

Both print the registration idiom `<constant>; FUN_00487900(); FUN_0040ec10("<Name>", <len>)`.

### c2s — `PlayerAgentProxy_t` (25 methods, the complete interface)

| client method | registration constant | wire id (varint) | observed opcode |
|---|---|---|---|
| `Authenticate` | `0x9CB2CB03` | `96cbe509` | 0x2083 |
| `AuthenticateLoggedIn` | `0x9D991E6C` | `bce4ec09` | 0x20EC (2nd conn) |
| `LoginFromCrash` | `0xCBDE126E` | `a4f8de0c` | — |
| `CreateCharacter` | `0xBE735486` | `a9cdf30b` | — |
| `CreateCharacterWithData` | `0xCA2C4E5E` | `9cb1d10c` | 0x20DE |
| `LoginCharacter` | `0xEF616EB6` | `dd85fb0e` | 0x20B6 |
| `RenameCharacter` | `0x1B2CF268` | `e4b3d901` | — |
| `RequestDeleteCharacter` | `0xC3C5C31D` | `86979e0c` | 0x209D |
| `VerifyLanguageSetting` | `0x9847AED6` | `dd9ec209` | 0x20D6 (login conn) |
| `AddOfflineLevel` | `0x2976E1B7` | `c3dbcb02` | — |
| `UnlockCharacter` | `0x9C370D1E` | `9adce109` | 0x209E |
| `SetHardcoreLevel` | `0x6A6B621C` | `c4add306` | — |
| `GetDimensionList` | `0x43476328` | `c69d9a04` | — |
| `RequestSuggestNickname` | `0xDBE172E5` | `e585df0d` | — |
| `GetStartupData` | `0x6A546D41` | `dad1d206` | 0x20C1 |
| `LoadFacebookToken` | `0xD3907026` | `e0c19c0d` | 0x20A6 |
| `SaveFacebookToken` | `0xD8AD362B` | `ecb4c50d` | — |
| `UpdateFacebookFriends` | `0x3A96B2B3` | `e5dad403` | — |
| `RecruitFacebookFriends` | `0x5B51044B` | `88c4da05` | — |
| `UpdateSubscriptionSettings` | `0x516C9EF5` | `bdb28b05` | 0x20F5 |
| `UpdateCharacterSettings` | `0x01035784` | `af8d08` | — |
| `UpdateCharacterPreview` | `0x4D661656` | `ac98eb04` | 0x20D6 (2nd conn) |
| `FinishGetStartupData` | `0xDFD8518E` | `a3e1fe0d` | 0x208E |
| `ReceivedCharacterSettings` | `0x3C7C926C` | `a4f2e303` | 0x20EC (login conn) |
| `ClientReplyKeepCookie` | `0x43D90C1B` | `98e49e04` | — |

### s2c — `PlayerInterface` (26 methods, the complete interface)

| client method | registration constant | wire id (varint) | observed opcode |
|---|---|---|---|
| `OnFacebookTokenResult` | `0x52454C9C` | `99959205` | 0x209C |
| `OnUpdateFacebookFriends` | `0x88750390` | `87d4c308` | — |
| `OnFacebookFriendSignedUp` | `0x7955BD80` | `fbd6ca07` | — |
| `AuthenticateAck` | `0x5DC18991` | `9386ee05` | 0x2091 |
| `LoginConnectReady` | `0xD8B66C79` | `d8d9c50d` | — |
| `CSServerConnectReady` | `0x5AED2A60` | `d4b4d705` | — |
| `ChatServerConnectReady` | `0x23A632FA` | `e5989d02` | — |
| `UpdateClientPlayerData` | `0xC414C5EF` | `8bd3a00c` | 0x20EF |
| `SetDimensionList` | `0xF899B14C` | `e2e6c40f` | 0x20CC |
| `SetStats` | `0xA7C86A29` | `d4a1be0a` | — |
| `SuggestNicknameResult` | `0xE8EAD4F8` | `a9abc70e` | — |
| `PlayerSetupComplete` | `0x4F91A58C` | `cbc6fc04` | 0x208C |
| `LoginQueuePosition` | `0x0FE09F03` | `be827f` | — |
| `OfflineLevelApplied` | `0x3E8F0623` | `8cbcf403` | — |
| `CharacterUnlocked` | `0x5CE97913` | `f2a5e705` | 0x2093 |
| `HardcoreLevelSet` | `0x1562CE84` | `9d8bab01` | — |
| `SendSubscriptionSettings` | `0x15F7AF22` | `dedeaf01` | — |
| `SendCharacterSettings` | `0x233605B9` | `8bd89902` | 0x20B9 |
| `SendCharacterPreview` | `0x206B6EE5` | `ddad8302` | 0x20E5 |
| `SendCharacterSettingsCheckSumMap` | `0x0C09CA25` | `94a760` | 0x20A5 |
| `VerifyClientStillConnected` | `0x00C46317` | `c69106` | — |
| `OverrideGameTweaks` | `0x7C47F763` | `ee9fe207` | — |
| `CharacterNeedsNewName` | `0x9625EB4A` | `d697b109` | — |
| `LoginProblem` | `0xD4063CA0` | `f998a00d` | — |
| `TeleportProblem` | `0xE33EBAE1` | `f5fa990e` | — |
| `ResetLanguage` | `0xD4C59816` | `b096a60d` | — |

### Names this corrected

The recovered constants also give the client's own METHOD NAMES, replacing nine that had been invented from observed
behaviour. Each correction is independently corroborated by request/response pairing in the captures — c2s
`UnlockCharacter` (`0x209E`) is answered by s2c `CharacterUnlocked` (`0x2093`), and c2s `LoadFacebookToken`
(`0x20A6`) by s2c `OnFacebookTokenResult` (`0x209C`).

| was | is | note |
|---|---|---|
| `InitAuth` | `AuthenticateAck` | |
| `SendCharacterRealmData` | `PlayerSetupComplete` | |
| `Ox2093` | `CharacterUnlocked` | the reply to c2s `UnlockCharacter` |
| `Ox209C` | `OnFacebookTokenResult` | the reply to c2s `LoadFacebookToken` |
| `SmallCharList` | `SendCharacterSettingsCheckSumMap` | its "constant" 3-byte tail is just its id |
| `Ox20B9` | `SendCharacterSettings` | |
| `SendRealmList` | `SetDimensionList` | a dimension IS a realm |
| `Ox20E5` | `SendCharacterPreview` | |
| `SendCharacterList` | `UpdateClientPlayerData` | |
| `CreateCharacter` (`0x20DE`) | `CreateCharacterWithData` | the client has both; plain `CreateCharacter` is never sent |
