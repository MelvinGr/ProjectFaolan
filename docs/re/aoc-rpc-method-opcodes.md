# AoC RPC method opcodes — recovered from AgeOfConan.exe (Ghidra, 2026-06-13)

Ground-truth 16-bit RPC method ids, recovered by RE'ing the client. **`wire opcode = 0x2000 + method
registration index`**, and the registration index is per-interface and contiguous from 0. The base is
`0x2000` for every interface EXCEPT PlayerAgent/PlayerInterface (see the note at the end).

> **Live on the wire (2026-07-08):** the Wireshark plugin resolves these names per packet
> (`faolan.pkt.method`, COL_INFO `GameCharAgent TryLootItem`) via tables generated at build time from
> the curated C# catalogs in `Faolan.Core.Network/Constants/Opcodes.cs` — that file, not this doc's
> tables, is the canonical machine-readable form (`Faolan.Wireshark/scripts/gen_opcode_names.py`).
> Regenerated `*.tshark.json` dumps carry the name as an additive `Method` key.

Recovery method (reproducible — scripts in `Captures/ghidra/`):
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
0x2001  <no-arg>
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

### PlayerInterface / PlayerAgent — DIFFERENT dispatch scheme (hash, not index)
`PlayerInterfaceDispatch_t::DispatchMethod` is **not** a `switch(0x2000+index)` like the others — it is a
binary-search tree comparing the method id against **32-bit constants** (e.g. `0x5dc18991` →
`DispatchAuthenticateAck`, `0x233605b9` → `DispatchSendCharacterSettings`, `0x1562ce84` →
`DispatchHardcoreLevelSet`; handler→name confirmed from the handlers' own strings). This is why
PlayerAgent/PlayerInterface is the lone interface whose wire ids don't fit the `0x2000 + index` model
(observed c2s wire `0x2083..0x20F5`) — it uses hashed method ids. The hash is **not** plain BKDR-31 or
CRC32 of the (Dispatch-)name (checked), so the exact function is still open. Until it's cracked the login
opcodes stay behavior-derived in `Opcodes.cs` (and they already work). Follow-up: identify the hash fn —
the handler→name pairs above are known plaintext↔hash anchors to solve it.
