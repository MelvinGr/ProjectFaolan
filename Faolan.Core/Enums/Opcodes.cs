// ReSharper disable InconsistentNaming

namespace Faolan.Core.Enums
{
	public enum ChatServerOpcodes : ushort
	{
		Hello = 0x0000,
		Whisper = 0x001E, // client sends a tell: [u32 targetCharId][string][00 01 00] (whisper capture 2026-06-12)
		AddFriend = 0x0028, // [u32 characterId] - the friend list is chat-tier and ONE-SIDED (no accept step)
		RemoveFriend = 0x0029, // [u32 characterId]
		ChannelChat = 0x0041, // client posts a message to a chat channel: [byte type][u32 channelId][string][0000]
		ChatReady = 0x0064 // small chat-init handshake the client sends after the channel subscriptions (000102)
	}

	public enum ChatServerInterfaceOpcodes : ushort
	{
		// The server's answer to the client's Hello (0x0000), carrying NO body. Named from POSITION, which is all an
		// empty message can be named from - but the position is invariant across the corpus (#532): it is the 4th chat
		// packet of every one of the 41 captures, always `Hello -> the opening ChannelSubscription burst -> THIS ->
		// the session's own ChatSenderInfo -> the welcome_message SystemMessage`. Two-client captures carry it twice,
		// once per client, so it is per-session rather than per-event. Not verified against the client; a future RE
		// pass over the chat dispatcher should confirm the name rather than assume it.
		HelloAck = 0x0005,
		CharacterChat = 0x0022,
		SystemMessage = 0x0023,
		Whisper = 0x001E, // incoming tell: [u32 senderCharId][string][00 01 00], preceded by 0x0014 sender info
		ChannelChat = 0x0041, // a channel message relayed to a client: [byte type][u32 channelId][u32 senderId][string][0000]
		ChatSenderInfo = 0x0014, // sender id + name; also sent before a ChannelChat so the client can show the speaker
		FriendPresence = 0x0025, // [u64 0][u32 stamp][u16][u8][string name] - pushed BEFORE the add reply, by NAME
		FriendAdded = 0x0028, // [u32 characterId][u8 online][u8 5][u32 stamp][u8 class] - the added friend's state
		FriendRemoved = 0x0029, // [u32 characterId] - the removal echoed back
		ChannelSubscription = 0x003C,

		// The subscription's twin: the channel you are no longer on. Body is [u8 type][u32 channelId] - byte-identical
		// to the (type, id) pair 0x003C opens with, minus the name string and trailing flags, which is what identifies
		// it (#532). The ids match channels subscribed earlier in the same capture: a `~Team` subscription
		// (82 000f08db) is followed by 0x003D on 82 000f08db, and in the two-client group capture the "leave goup now"
		// message on channel 82 0051dd9a is followed by 0x003D on that same channel. `~Playfield` likewise flips as the
		// player changes playfield - the old channel is dropped, then the new one subscribed.
		ChannelUnsubscribe = 0x003D,
		ChatReadyAck = 0x0064 // echo of the client's chat-init handshake
	}

	// Client->server methods - the COMPLETE recovered CSPlayerAgent.client_to_server table. The method IS the
	// position in docs/re/aoc-client-rpc-methods.json; alignment verified against the hoxxling guild capture
	// (0x00 Authenticate, 0x1C AcceptGuildInvitation fired exactly once on the accept, 0x1E/0x2F/0x40/0x41/0x77
	// all behave as named). See tools/capture/decode_guild.py.
	public enum CSPlayerAgentMethods : uint
	{
		Authenticate = 0x00,
		CharacterAuthenticated = 0x01,
		CharacterDisconnected = 0x02,
		TeamInvite = 0x03,
		TeamInviteByName = 0x04,
		TeamApply = 0x05,
		TeamSetLootOption = 0x06,
		TeamSetMasterLooter = 0x07,
		AcceptTeamInvite = 0x08,
		DeclineTeamInvite = 0x09,
		CharacterSearch = 0x0A,
		SearchForTeam = 0x0B,
		SetNewTeamLeader = 0x0C,
		SummonCharacters = 0x0D,
		AcceptSummon = 0x0E,
		DeclineSummon = 0x0F,
		GetExtendedCharacterInfo = 0x10,
		GetExtendedCharacterInfoByName = 0x11,
		SetLookingForTeamStatus = 0x12,
		SetLookingForMemberStatus = 0x13,
		GetLookingForTeamStatus = 0x14,
		GetLookingForMemberStatus = 0x15,
		SetCommunityServerOptions = 0x16,
		StartVoteKick = 0x17,
		VoteKickAnswer = 0x18,
		StartVoteRetreat = 0x19, // StartVoteRetreat(), NO params. The "observed [u32 0xC350][u32 clientInstance] at login" was 0x201E ClientRequestGuildInfo mis-attributed (#458)
		VoteRetreatAccept = 0x1A,
		VoteRetreatReject = 0x1B,
		AcceptGuildInvitation = 0x1C,
		DeclineGuildInvitation = 0x1D,
		ClientRequestGuildInfo = 0x1E,
		ClientRequestGuildName = 0x1F,
		ClientRequestCharIDByName = 0x20,
		CreateGuild = 0x21,
		GuildInviteByName = 0x22,
		GuildInvite = 0x23,
		RemoveGuildInvitationCooldown = 0x24,
		GuildPromote = 0x25,
		GuildDemote = 0x26,
		GuildKick = 0x27,
		GuildLeave = 0x28,
		GuildTax = 0x29,
		GuildGovernmentForm = 0x2A,
		GuildRecruitmentMode = 0x2B,
		GuildName = 0x2C,
		GuildDescription = 0x2D,
		GuildMessageOfTheDay = 0x2E,
		GuildRequestMemberList = 0x2F,
		GuildApplication = 0x30,
		GuildRejectApplication = 0x31,
		SetLookingForGuild = 0x32,
		GuildMergeRequest = 0x33,
		GuildMergeReply = 0x34,
		GuildWithdrawMoney = 0x35,
		GetPvpPointsInfo = 0x36,
		AddPvpPoints = 0x37,
		RemovePvpPoints = 0x38,
		ClearPvpPoints = 0x39,
		GetGuildRenownInfo = 0x3A,
		AddGuildRenown = 0x3B,
		RemoveGuildRenown = 0x3C,
		SetGuildRenown = 0x3D,
		CalculateRankings = 0x3E,
		UnlockGuildRenown = 0x3F,
		RequestRenownHistory = 0x40,
		RequestRenownRankings = 0x41,
		RequestBattleKeepInformation = 0x42,
		SearchForGuilds = 0x43,
		GMGuildName = 0x44,
		RequestGuildCitySpot = 0x45,
		ApplyForMassivePVPBattleQueue = 0x46,
		WithdrawFromMassivePVPBattleQueue = 0x47,
		MassivePvPBattleQueueGMCommand = 0x48,
		MassivePvpBattleQueueStatusRequest = 0x49,
		PvPMatchMakingCreateMatch = 0x4A,
		PvPMatchMakingJoinMatch = 0x4B,
		PvPMatchMakingSetSide = 0x4C,
		PvPMatchMakingMatchSay = 0x4D,
		PvPMatchMakingLeaveMatch = 0x4E,
		PvPMatchMakingSearchForMatches = 0x4F,
		PvPMatchMakingGetMatchInfo = 0x50,
		PvPMatchMakingTeleportToMinigame = 0x51,
		PvPMatchMakingPutIntoMatchMaking = 0x52,
		PvPMatchMakingRemoveFromMatchMaking = 0x53,
		PvPMatchMakingRequestInfo = 0x54,
		PvPMatchMakingIsInMatchMaking = 0x55,
		PvPMatchMakingKickPlayer = 0x56,
		PvPMatchMakingBanPlayer = 0x57,
		PvPMatchMakingMakePlayerMatchOwner = 0x58,
		PvPMatchMakingForceSide = 0x59,
		PvPMatchMakingStartMatch = 0x5A,
		DebugPvPMatchMakingShowDebugInfo = 0x5B,
		DebugPvPMatchMakingStopAllMatches = 0x5C,
		DebugPvPMatchMakingStopCurrentMatch = 0x5D,
		DebugPvPMatchMakingSetNewTimeLimit = 0x5E,
		DebugPvPMatchMakingRestart = 0x5F,
		DebugPvPMatchForcePlayerIntoCurrentMatch = 0x60,
		DebugPvPMatchMakingDoMatchMaking = 0x61,
		DebugPvPMatchForceStartMatch = 0x62,
		DebugPvPMatchForceEnterAll = 0x63,
		DebugCommunityServerModuleStart = 0x64,
		DebugCommunityServerModuleStop = 0x65,
		SetAsyncEnabled = 0x66,
		SetNumWorkerThreads = 0x67,
		RaidCreate = 0x68,
		RaidLeave = 0x69,
		RaidInviteTeam = 0x6A,
		RaidInviteTeamAccept = 0x6B,
		RaidInviteTeamDecline = 0x6C,
		RaidKickTeam = 0x6D,
		RaidKickCharacter = 0x6E,
		RaidMoveTeamMember = 0x6F,
		RaidSwapCharacters = 0x70,
		RaidInviteCharacter = 0x71,
		RaidInviteCharacterByName = 0x72,
		RaidAcceptInvite = 0x73,
		RaidDeclineInvite = 0x74,
		RaidSetLootOption = 0x75,
		RaidSetMasterLooter = 0x76,
		MassivePvpInfoRequest = 0x77,
		SendTeamInfo = 0x78,
		GMRequestGuildInfoFromChar = 0x79,
		QueueGroupFinder = 0x7A,
		SelectGroupFinderRoles = 0x7B,
		LeaveGroupFinder = 0x7C,
		ReadyGroupFinder = 0x7D,
		RequestRaidQueueInfo = 0x7E,
		UpdateDefaultRaid = 0x7F,
		SetFeaturedRaid = 0x80,
		RequestFeaturedRaidNeededRole = 0x81,
		RequestNeededRole = 0x82,
		RequestRotatingActivityChildInfo = 0x83,
		GetWorldStat = 0x84,
		RequestItemSalesBalance = 0x85,
		RequestItemSalesCoupons = 0x86,

		// Beyond the recovered c2s table: these are the CSPlayerInterface server->client GroupFinder DISPATCH
		// opcodes that the client ECHOES back on the upload path (names recovered from the client dispatch table +
		// verbatim binary strings, #365). Consume-only; the real s2c pushes are the CSPlayerInterface entries.
		EchoGroupFinderRemoved = 0xC5, // s2c DispatchGroupFinderRemoved (args: reason, silent)
		EchoGroupFinderUpdateFeaturedActivities = 0xD0 // s2c DispatchGroupFinderUpdateFeaturedActivities (args: activities list)
	}

	// CSPlayerInterface server->client dispatch table (211 methods; +33 inserted no-arg). Guild / team / groupfinder on the CsPlayerAgent connection.
	// Generated from the Ghidra s2c dispatch tables (docs/re/aoc-rpc-method-opcodes.md) (the method IS the dispatch switch index);
	// <no-arg> insertion slots have a known index but no recoverable name -> Reserved0xNN.
	public enum CSPlayerInterfaceMethods : uint
	{
		DispatchAuthenticateAck = 0x00,
		DispatchTeamInvite = 0x01,
		DispatchTargetIsBusy = 0x02,
		DispatchTargetIsCriminal = 0x03,
		DispatchTeamCharacterApplied = 0x04,
		DispatchYouAlreadySentRequest = 0x05,
		DispatchTargetIsAlreadyInTeam = 0x06,
		DispatchTeamInvitationTimedOut = 0x07,
		DispatchTargetDeclinedTeamInvite = 0x08,
		DispatchYouAreNoLongerInvited = 0x09,
		DispatchYouInvitedToTeam = 0x0A,
		DispatchInviteWithInviterFactionPoints = 0x0B,
		DispatchExtendedCharacterInfo = 0x0C,
		DispatchExtendedCharacterInfoNotFound = 0x0D,
		DispatchYouJoinedTeam = 0x0E,
		DispatchTeamSearchResult = 0x0F,
		DispatchCharacterSearchResult = 0x10,
		DispatchCharacterNameSearchResult = 0x11,
		DispatchTeamLeave = 0x12,
		DispatchTeamJoin = 0x13,
		DispatchTeamMemberUpdate = 0x14,
		DispatchTeamDisband = 0x15,
		DispatchSetNewTeamLeader = 0x16,
		DispatchTeamLootOptionChanged = 0x17,
		DispatchTeamMasterLooterChanged = 0x18,
		DispatchTeamSummonRequest = 0x19,
		DispatchTeamSummonTimedOut = 0x1A,
		DispatchYouAreNoLongerSummoned = 0x1B,
		DispatchTeamSummonRequestDeclined = 0x1C,
		DispatchTeamSummonRequestAccepted = 0x1D,
		DispatchTeamSummonRequestFailedInvalidPF = 0x1E,
		DispatchTeamSummonRequestFailedAlreadyInSameInstance = 0x1F,
		DispatchWarningTeamSizeTooLarge = 0x20,
		Reserved0x21 = 0x21,
		DispatchYouGotKickedFromTeam = 0x22,
		DispatchVoteKickStarted = 0x23,
		DispatchVoteKickFailed = 0x24,
		DispatchVoteKickEnded = 0x25,
		DispatchCharacterLookingForTeamStatus = 0x26,
		DispatchTeamLookingForMemberStatus = 0x27,
		DispatchDeleteCanceledMail = 0x28,
		DispatchDeleteCanceledMailItem = 0x29,
		DispatchGuildInvite = 0x2A,
		Reserved0x2B = 0x2B,
		Reserved0x2C = 0x2C,
		Reserved0x2D = 0x2D,
		DispatchGuildMemberLeft = 0x2E,
		DispatchGuildMemberJoined = 0x2F,
		DispatchGuildMemberUpdate = 0x30,
		DispatchGuildInviteReplyToInviter = 0x31,
		DispatchGuildGMPromoteFeedback = 0x32,
		DispatchGuildGMDemoteFeedback = 0x33,
		DispatchGuildInviteNoSuchName = 0x34,
		Reserved0x35 = 0x35,
		DispatchGuildPvPPointInfo = 0x36,
		DispatchGuildRenownInfo = 0x37,
		DispatchGuildRankingsUpdate = 0x38,
		DispatchGuildRenownHistoryUpdate = 0x39,
		DispatchClientAddGuildRenown = 0x3A,
		DispatchInformAboutRankings = 0x3B,
		DispatchInformAboutRankingWinners = 0x3C,
		DispatchRankingCaluculationCountdown = 0x3D,
		DispatchPersonalRenownCapReached = 0x3E,
		DispatchGuildUpdate = 0x3F,
		DispatchGuildRenownUpdate = 0x40,
		DispatchGuildName = 0x41,
		DispatchGuildUpdateMember = 0x42,
		DispatchGuildUpdateOther = 0x43,
		DispatchGuildCreated = 0x44,
		DispatchGuildNonAlphanumericName = 0x45,

		// ⭐ The two NO-ARG members of the guild-name refusal block, recovered from the client rather than named
		// (#737). A no-arg dispatch has no argument signature for the string catalog to have recorded, which is why
		// they sat as `Reserved` - but the dispatcher hands each one to an interface VTABLE SLOT, and
		// `CSClientIncoming_c`'s override of that slot fetches the LDB category-100 token it shows:
		//
		//   0x45 -> slot +0x118 -> "NonAlphanumericGuildName"     (already named - the control)
		//   0x46 -> slot +0x11C -> "WrongLengthGuildName"
		//   0x47 -> slot +0x120 -> "TakenGuildName"
		//   0x48 -> slot +0x124 -> "BadGuildName"                 (already named - the second control)
		//
		// Both ends of the block land on their known names, which is what makes the two in the middle trustworthy.
		DispatchGuildNameWrongLength = 0x46,
		DispatchGuildNameTaken = 0x47,
		DispatchGuildBadName = 0x48,
		DispatchGuildNameWaitTime = 0x49,
		Reserved0x4A = 0x4A,
		Reserved0x4B = 0x4B,
		Reserved0x4C = 0x4C,
		Reserved0x4D = 0x4D,
		DispatchGuildOnInviteCooldown = 0x4E,
		Reserved0x4F = 0x4F,
		DispatchGuildTaxError = 0x50,
		DispatchGuildInvalidGoverningForm = 0x51,
		DispatchGuildUpdateCash = 0x52,
		Reserved0x53 = 0x53,
		DispatchGuildTaxChanged = 0x54,
		DispatchGuildGoverningFormChanged = 0x55,
		DispatchGuildRecruitmentModeChanged = 0x56,
		DispatchGuildMessageOfTheDayChanged = 0x57,
		Reserved0x58 = 0x58,
		DispatchGuildNameChanged = 0x59,
		DispatchGMGuildNameChanged = 0x5A,
		DispatchGuildMemberList = 0x5B,
		DispatchGuildSearchResult = 0x5C,
		DispatchGuildSetPvPPoints = 0x5D,
		DispatchGuildSetPvPKillPoints = 0x5E,
		DispatchGuildSetPvPTotalKills = 0x5F,
		DispatchGuildSetPvPSeasonalKills = 0x60,
		DispatchGuildSetGuildWar = 0x61,
		DispatchGuildSetGuildAsDefenderOfWar = 0x62,
		DispatchAddGuildFeat = 0x63,
		DispatchRemoveGuildFeat = 0x64,
		DispatchGuildRankChanged = 0x65,
		DispatchGuildApplicationRequest = 0x66,
		Reserved0x67 = 0x67,
		DispatchGuildApplicationRejected = 0x68,
		DispatchGuildMergeRequest = 0x69,
		DispatchGuildMergeRequestFailed = 0x6A,
		DispatchGuildCityBooked = 0x6B,
		DispatchGuildCityBookingFailed = 0x6C,
		DispatchNewVulnerabilityWindowData = 0x6D,
		Reserved0x6E = 0x6E,
		DispatchGuildIsMassivePvPCityOwner = 0x6F,
		DispatchGuildIsMassivePvPCityAttacker = 0x70,
		DispatchFeedbackMessage = 0x71,
		DispatchRequestBattleKeepInformation = 0x72,
		DispatchGMReplyGuildInfoFromChar = 0x73,
		Reserved0x74 = 0x74,
		DispatchPvPMatchMakingWantsToStart = 0x75,
		DispatchPvPMatchMakingPlayerJoined = 0x76,
		DispatchPvPMatchMakingPlayerChangedSide = 0x77,
		DispatchPvPMatchMakingPlayerLeft = 0x78,
		DispatchPvPMatchMakingPlayerKicked = 0x79,
		DispatchPvPMatchMakingErrorCode = 0x7A,
		DispatchPvPMatchMakingYouJoinedMatch = 0x7B,
		Reserved0x7C = 0x7C,
		DispatchPvPMatchMakingMatchInfo = 0x7D,
		DispatchPvPMatchMakingSearchResult = 0x7E,
		DispatchPvPMatchMakingStartClicked = 0x7F,
		DispatchPvPMatchMakingYouAreInMatchMaking = 0x80,
		Reserved0x81 = 0x81,
		Reserved0x82 = 0x82,
		DispatchPvPMatchMakingMatchSay = 0x83,
		DispatchPvPMatchMakingCollectingMorePlayers = 0x84,
		DispatchPvPMatchMakingNewMatchLeader = 0x85,
		DispatchPvPMatchMakingTeamLeaderSignedUpForMatchMaking = 0x86,
		Reserved0x87 = 0x87,
		Reserved0x88 = 0x88,
		DispatchPvPMatchMakingCantStartMatchNotAllPlayersHaveSide = 0x89,
		DispatchPvPMatchMakingCantLockToSideMustPickASideFirst = 0x8A,
		DispatchPvPMatchMakingCantChangeSideYouHaveBeenLockedToSide = 0x8B,
		DispatchPvPMatchMakingCharacterNowLockedToSide = 0x8C,
		DispatchPvPMatchMakingCharacterNoLongerLockedToSide = 0x8D,
		Reserved0x8E = 0x8E,
		Reserved0x8F = 0x8F,
		Reserved0x90 = 0x90,
		Reserved0x91 = 0x91,
		DispatchPvPMatchMakingInfoRequestReturn = 0x92,
		DispatchYouJoinedRaid = 0x93,
		DispatchRaidTeamJoined = 0x94,
		DispatchRaidTeamLeft = 0x95,
		DispatchRaidTeamUpdate = 0x96,
		DispatchRaidCharacterUpdate = 0x97,
		DispatchRaidNewRaidLeader = 0x98,
		DispatchRaidLootOptionChanged = 0x99,
		DispatchRaidNewMasterLooter = 0x9A,
		DispatchRaidInvite = 0x9B,
		DispatchRaidTargetIsBusy = 0x9C,
		DispatchRaidIsAlreadyInRaid = 0x9D,
		DispatchRaidInvitationTimedOut = 0x9E,
		DispatchRaidTargetDeclinedRaidInvite = 0x9F,
		DispatchRaidYouAreNoLongerInvited = 0xA0,
		Reserved0xA1 = 0xA1,
		DispatchRaidCantInviteInvalidSubscriptionFlags = 0xA2,
		Reserved0xA3 = 0xA3,
		DispatchRaidCantCreateInvalidMemberSubscriptionFlags = 0xA4,
		DispatchMassivePvpInfoRequestReturn = 0xA5,
		DispatchMassivePvPBattleQueueFeedback = 0xA6,
		DispatchMassivePvpStartCountDown = 0xA7,
		DispatchMassivePvpBattleQueueStatusUpdate = 0xA8,
		DispatchBattleKeepInfoRequestReturnPhase1And2 = 0xA9,
		DispatchBattleKeepInfoRequestReturnPhase3And4 = 0xAA,
		DispatchYouGotMailNotification = 0xAB,
		Reserved0xAC = 0xAC,
		DispatchDebugFullGameServerList = 0xAD,
		DispatchDebugGameServerConnected = 0xAE,
		DispatchDebugGameServerDisconnected = 0xAF,
		DispatchDebugClientConnected = 0xB0,
		DispatchDebugClientChanged = 0xB1,
		DispatchDebugClientDisconnected = 0xB2,
		DispatchDebugFullClientList = 0xB3,
		DispatchDebugFullTeamListBurst = 0xB4,
		DispatchDebugAddTeam = 0xB5,
		DispatchDebugRemoveTeam = 0xB6,
		DispatchDebugUpdateTeam = 0xB7,
		DispatchDebugFullMatchListBurst = 0xB8,
		DispatchDebugAddMatch = 0xB9,
		DispatchDebugMatchChanged = 0xBA,
		DispatchDebugRemoveMatch = 0xBB,
		DispatchDebugAddIDToMatchMaking = 0xBC,
		DispatchDebugRemoveIDFromMatchMaking = 0xBD,
		DispatchItemSalesUpdateBalance = 0xBE,
		DispatchItemSalesCoupons = 0xBF,
		Reserved0xC0 = 0xC0,
		DispatchCSFeedback = 0xC1,
		DispatchGroupFinderSignupFailed = 0xC2,
		// #792: no-arg, so the string catalog has no name for it - but it is not opaque. Its handler is
		// `CSClientIncoming_c`'s override of vtable +0x310, which fires the signal at `CSIncoming+0x7c`; ⚠️ the
		// DECOMPILATION of that override is identical to 0xC8's and the two were written off as
		// indistinguishable on that basis - the `LEA ECX` that picks the signal is dropped, and on a body this
		// short it is the whole method. In the capture it fires immediately after a SUCCESSFUL
		// `QueueGroupFinder`, in the slot where the refused attempt got `DispatchGroupFinderSignupFailed`
		// instead, and before `SelectGroupFinderRoles` / `DispatchGroupFinderSignupSuccess`. Naming it needs the
		// signal's SUBSCRIBER, which is not reachable from the singleton (Gitea #801 records the exhausted routes).
		Reserved0xC3 = 0xC3,
		DispatchGroupFinderSignupSuccess = 0xC4,
		DispatchGroupFinderRemoved = 0xC5,
		DispatchGroupFinderReadyCheck = 0xC6,
		Reserved0xC7 = 0xC7,
		// #792: the twin of 0xC3 - vtable +0x324, the signal at `CSIncoming+0x84`, and in the capture it fires
		// immediately after `DispatchYouJoinedTeam`, i.e. once the group finder's work is done. Gitea #801.
		Reserved0xC8 = 0xC8,
		DispatchGroupFinderReadyCheckStatus = 0xC9,
		DispatchGroupFinderReadyCheckRejected = 0xCA,
		DispatchGroupFinderPlayfieldInfo = 0xCB,
		DispatchGroupFinderFeaturedPlayfieldInfo = 0xCC,
		DispatchGroupFinderFeaturedRaidNeededRoleUpdated = 0xCD,
		DispatchGroupFinderReceivedNeededRole = 0xCE,
		DispatchGroupFinderReceivedRotatingActivityChildInfo = 0xCF,
		DispatchGroupFinderUpdateFeaturedActivities = 0xD0,
		DispatchReturnWorldStat = 0xD1,
		DispatchInvalidWorldStat = 0xD2
	}

	// --- Full client->server method catalogs for the GameServer connection's RPC classes, generated from
	// --- docs/re/aoc-client-rpc-methods.json (the method IS the table position). Most are not handled yet;
	// --- they are here so future packets can be identified and routed by (class id, method).

	// GameAgent (caller GameInterface) - the connection-level service: auth, ping, the PassBlob IIR carrier.
	public enum GameAgentMethods : uint
	{
		PassBlob = 0x00,
		Ping = 0x01,
		ChatVicinity = 0x02,
		Authenticate2 = 0x03,
		GetStats = 0x04,
		UpdateGameServerStats = 0x05,
		DataSink = 0x06,
		ClientDisconnected = 0x07,
		SaveFacebookToken = 0x08,
		RecruitFacebookFriends = 0x09,
		UpdateFacebookFriends = 0x0A
	}

	// GameCharAgentProject (caller GameCharInterfaceProject) - per-character "project" extras.
	public enum GameCharAgentProjectMethods : uint
	{
		RequestAggroStatus = 0x00,
		GetProcDataForID = 0x01,
		AcceptApprenticeTeacher = 0x02,
		DeclineApprenticeTeacher = 0x03,
		RequestApprenticeStudent = 0x04,
		LeaveApprenticeMode = 0x05,
		RequestHitAndMitigationTables = 0x06,
		StartOfflineTrainingSystem = 0x07,
		ResetOfflineTrainingSystem = 0x08,
		RefreshEquippedAbilities = 0x09,
		EnableRangerTracking = 0x0A,
		ToggleVanityArmorFilter = 0x0B,
		SetSprintInputState = 0x0C
	}

	// GameCharAgent (caller GameCharInterface) - the main per-character world service (guild sync, loot,
	// quests, trade, shop, shortcuts, instances, minigames).
	public enum GameCharAgentMethods : uint
	{
		RequestAggroStatus = 0x00,
		RequestCharName = 0x01,
		RequestBindLocation = 0x02,
		SetTutorialStats = 0x03,
		UpdateCharGuildMemberInfo = 0x04,
		GuildInfoUpdate = 0x05,
		JoinedGuild = 0x06,
		LeftGuild = 0x07,
		LeaveTeam = 0x08,
		KickFromTeam = 0x09,
		CreatedGuild = 0x0A,
		AddGuildFeat = 0x0B,
		RemoveGuildFeat = 0x0C,
		GuildGMPromote = 0x0D,
		GuildGMDemote = 0x0E,
		GuildTaxChanged = 0x0F,
		GuildGoverningFormChanged = 0x10,
		GuildTransferMoney = 0x11,
		GuildWithdrawMoney = 0x12,
		GuildDisbanded = 0x13,
		GuildBookedCity = 0x14,
		GuildCityOwnershipChange = 0x15,
		GuildChangeRank = 0x16,
		GuildUpdateGuildBank = 0x17,
		SendGuildInfoToObservers = 0x18,
		GuildRankingsUpdate = 0x19,
		ValidateGuildBadges = 0x1A,
		OpenGuildBank = 0x1B,
		CloseBank = 0x1C,
		SplitInventoryItem = 0x1D,
		CancelBuff = 0x1E,
		StackInventoryItemIfPossible = 0x1F,
		UpdateQuestMarkerNPC = 0x20,
		MagicForgetSpell = 0x21,
		ResurrectMe = 0x22,
		CancelResurrectionRequest = 0x23,
		RespawnMe = 0x24,
		SignUpForWaveRespawn = 0x25,
		UpdateWaveRespawn = 0x26,
		CancelWaveRespawn = 0x27,
		RequestWaveRespawnTimerUpdate = 0x28,
		RespawnMeHere = 0x29,
		GMRequestSpellData = 0x2A,
		RM_GetQuestFromQuestgiver = 0x2B,
		GMRequestFeatData = 0x2C,
		GMRequestFeatClassData = 0x2D,
		AdjustSkill = 0x2E,
		RequestPCDebugData = 0x2F,
		GetLootDataForID = 0x30,
		SimulateLootForID = 0x31,
		CloseShop = 0x32,
		BuyItem = 0x33,
		SellItem = 0x34,
		UndoLastSoldItem = 0x35,
		FormationSetData = 0x36,
		FormationClaimPosition = 0x37,
		FormationClear = 0x38,
		FormationLeave = 0x39,
		FormationUpdatePosRot = 0x3A,
		ScryMessageBoxAnswer = 0x3B,
		ScryOptionsBoxAnswer = 0x3C,
		ExitScenarioResults = 0x3D,
		FeatUntrainAnswer = 0x3E,
		CraftUntrain = 0x3F,
		PlantCTFFlags = 0x40,
		UntrainFeat = 0x41,
		TryOpenLootBag = 0x42,
		TryLootAll = 0x43,
		CloseLootBag = 0x44,
		TryLootCash = 0x45,
		TryLootItem = 0x46,
		PassNeedGreedForItem = 0x47,
		DoNeedGreedRollForItem = 0x48,
		LootLockedItem = 0x49,
		SlaveLooterAnswer = 0x4A,
		MasterLooterOffer = 0x4B,
		MasterLooterCancelOffer = 0x4C,
		SetPassOnLootLevel = 0x4D,
		UnbindItem = 0x4E,
		ShareQuest = 0x4F,
		ShareQuestDeclined = 0x50,
		ShareQuestAccepted = 0x51,
		ShareQuestBusy = 0x52,
		RequestNextQuestPendingReward = 0x53,
		RequestTeamQuestList = 0x54,
		AddQuestList = 0x55,
		AcceptNextAwaitingQuest = 0x56,
		QuestManagerMsg = 0x57,
		StartQuickTeleport = 0x58,
		DoPaintballQuery = 0x59,
		PerformSpecialAction = 0x5A,
		QueuePetCommand = 0x5B,
		DoSkillSellback = 0x5C,
		PetSetGroundTargetPos = 0x5D,
		PlayStockphraseAsSubtitle = 0x5E,
		CinematicTransition = 0x5F,
		RequestACGTeleport = 0x60,
		RequestClearOwnACG = 0x61,
		RequestDropTeamBind = 0x62,
		DebugSetOceanData = 0x63,
		RequestTrade = 0x64,
		StartTrade = 0x65,
		DeclineTrade = 0x66,
		SetTradeCash = 0x67,
		AddTradeItem = 0x68,
		RemoveTradeItem = 0x69,
		MoveTradeItemInternal = 0x6A,
		AcceptTrade = 0x6B,
		AbortTrade = 0x6C,
		RequestShortcutBar = 0x6D,
		AddShortcut = 0x6E,
		UpdateShortcut = 0x6F,
		MoveShortcut = 0x70,
		RemoveShortcut = 0x71,
		StartMinigame = 0x72,
		NewMinigamePlayer = 0x73,
		DropMinigamePlayer = 0x74,
		ResendMinigameInfoToPlayer = 0x75,
		FlagAsCoward = 0x76,
		ForceStopMinigame = 0x77,
		ForceSetMinigameTime = 0x78,
		PrepareMinigameTime = 0x79,
		PrepareToMinigameTime = 0x7A,
		GetInstanceListForCurrentPlayfield = 0x7B,
		ShiftInstanceForCurrentPlayfield = 0x7C,
		EnableHeroicMode = 0x7D,
		SetHideInspectOption = 0x7E,
		SetPlayfieldInstanceList = 0x7F,
		InstanceWillResetOnShutdown = 0x80,
		InstanceHasBeenLocked = 0x81,
		InstanceHasBeenUnLocked = 0x82,
		RequestTeamMemberPosList = 0x83,
		TradepostSearchResult = 0x84,
		Ping = 0x85,
		ShiftTeamInstance = 0x86,
		SubscriptionFlagsUpdated = 0x87,
		PlayerServicesUpdated = 0x88,
		PassiveAbilityEquip = 0x89,
		PassiveAbilityUnequip = 0x8A,
		ExitPassiveMode = 0x8B,
		EnterPassiveMode = 0x8C,
		RequestMassivePvPScoreData = 0x8D,
		RequestScorePlayerNames = 0x8E,
		SetVulnerabilityWindowStartTime = 0x8F,
		RequestRecipeInfo = 0x90,
		RequestTeamInvite = 0x91,
		RequestNewJoinHasHostileInFriends = 0x92,
		RequestTeamInviteWithInviterFactionPoints = 0x93,
		RequestItemSalesData = 0x94,
		RequestItemSaleLimitedItemList = 0x95,
		RequestItemSaleFavoriteData = 0x96,
		SetFakeBillingConnection = 0x97,
		AddFavoriteItem = 0x98,
		RemoveFavoriteItem = 0x99,
		BuyItemSalesItem = 0x9A,
		CSBuyItemReply = 0x9B,
		CSRollbackResponse = 0x9C,
		ExecuteChatCommand = 0x9D,
		ReplyUpdatedDimensionDB = 0x9E,
		RequestUseTeleportSpell = 0x9F,
		UnlockFromPlayfield = 0xA0,
		UpdateMailBank = 0xA1,
		SetSelectedTag = 0xA2,
		SetSelectedBadgeTag = 0xA3,
		KickPlayerFromGroupPlayfield = 0xA4,
		WorldStatChanged = 0xA5,
		UpdateNoXPGainStat = 0xA6,
		SendReport = 0xA7,
		RunLua = 0xA8
	}

	// Every 32-bit sub-opcode carried inside a 0x2000 packet is an "IIR" (Incremental Information Replication)
	// message id = the BKDR-31 hash of the client's IIR C++ type name (h = h*31 + c; == Java String.hashCode),
	// recovered by RE'ing AgeOfConan.exe (docs/re/aoc-opcode-ids.md). Members are named
	// after the real client type (with the _t/_c template suffix dropped); the comment gives the exact type and
	// the occurrence count across all captures. This is the full catalog of every IIR id observed on the wire
	// (server->client AND client->server - the id is the message type, direction-independent), even ones the
	// server doesn't emit yet. The few descriptive aliases the server code uses are at the bottom.
	public enum IirTypes : uint
	{
		AddToGroupIIR = 0x0DF44B73, // AddToGroupIIR_c  (x16) - a member joined a team; pushed to every member's WORLD connection
		AreaFormulaIIR = 0xCE30BE4E, // AreaFormulaIIR_t  (x42)
		BankIIR = 0x0BA1C1CB, // BankIIR_t  (x3)
		BehaviorControlCenterIIR = 0x642CD3D6, // BehaviorControlCenterIIR_c  (x22474) - the generic action/behavior channel ("Interact")
		ChainKillIIR = 0x0EED7068, // ChainKillIIR_t  (x28)
		CharDCMoveIIR = 0x4517AA81, // CharDCMoveIIR_t  (x11005) - entity move ("Move"); raw-body floats are big-endian
		CharInPlayIIR = 0x864CFEF8, // CharInPlayIIR_t  (x74) - "ReadyForPlayScreen"
		CharacterActionIIR = 0x6B6FD368, // CharacterActionIIR_t  (x136) - "CloseGame"
		ChatTextIIR = 0xE0B51102, // ChatTextIIR_t  (x1)
		ClimbingDynelFullUpdateIIR = 0x1FD6412B, // ClimbingDynelFullUpdateIIR_c  (x87)
		CombatReactiveIIR = 0x54C02089, // CombatReactiveIIR_c  (x17)
		CombatShieldIIR = 0xCFFD0519, // CombatShieldIIR_c  (x79)
		ComboBookIIR = 0xA8BBEB7F, // ComboBookIIR_c  (x65)
		CoreClaimIIR = 0x06EC1255, // CoreClaimIIR  (x8) - "InventoryClaimsButton"
		CrimeIIR = 0xC7954504, // CrimeIIR_c  (x3)
		DamageResultIIR = 0xFEC4158A, // DamageResultIIR_c  (x1134) - combat damage result
		DeleteItemIIR = 0x72491DE9, // DeleteItemIIR_t - "DropItem"; body = ProtoDeleteItemIIR (NOT the sibling move message - they alias on the wire; see DeleteItemCodec)
		DestructibleFullUpdateIIR = 0x6BD56736, // DestructibleFullUpdateIIR_c  (x189)
		DoorFullUpdateIIR = 0x33A56FB0, // DoorFullUpdateIIR_c  (x89) - world door/object full update
		EnergyIIR = 0x40749B8A, // EnergyIIR  (x1039)
		FeatIIR = 0x28DA68A0, // FeatIIR (BKDR-31 of "FeatIIR") - feat train/unlock: 0x3F1-framed feat-id list; c2s state 0x04 request, s2c 0x05 ack (#251)
		FeedbackIIR = 0xBA1FD222, // FeedbackIIR_t  (x5)
		FormatFeedbackIIR = 0x8D5A8B6B, // FormatFeedbackIIR_t  (x68)
		FullPlayerCharacterIIR = 0x737A6DF9, // FullPlayerCharacterIIR  (x33) - the whole player character (inc. inventory); see carve-out below
		GameActionListIIR = 0xF508F4C1, // GameActionListIIR_t  (x366)
		TradeskillRequestIIR = 0x06B9CD94, // TradeskillRequestIIR_c - recipe-book REQUEST (client opened Crafting->Recipes); reply = TradeskillReplyIIR
		TradeskillReplyIIR = 0x9062D559, // TradeskillReplyIIR_c - the recipe-book list (reply to TradeskillRequestIIR 0x06B9CD94)
		GameTimeIIR = 0x4F57DC08, // GameTimeIIR_t  (x33) - the world day/night clock; resyncs the client's GameTime_t
		GuildUpdateIIR = 0x2550C99A, // GuildUpdateIIR_c  (x3)
		HateListUpdateIIR = 0x8A01E5A1, // HateListUpdateIIR  (x61)
		// FanaticCommandIIR_t - what the client sends on `/gm`. "Fanatic" is its own name for the GM/admin command
		// channel. Body decoded from the client's reader (FUN_00663860): [u32 command][u32 targetA][u32 targetB][string].
		// Seen on the wire from our own client, never in the corpus (the retail traces are ordinary play, not GM
		// sessions), so the server's REPLY is still undecoded - #448/#449.
		FanaticCommandIIR = 0xEE4AA85C,

		IntendedTeleportIIR = 0x10D27BC0, // IntendedTeleportIIR_c  (x15)
		KnubotAnswerIIR = 0x4D7D9123, // KnubotAnswerIIR_c  (x194) - NPC dialogue ("Knubot" = the NPC dialogue system)
		KnubotAnswerListIIR = 0xF164A4A5, // KnubotAnswerListIIR_c  (x177)
		KnubotAppendTextIIR = 0x4A3D899A, // KnubotAppendTextIIR_c  (x217)
		KnubotCloseChatWindowIIR = 0xC4AF39EB, // KnubotCloseChatWindowIIR_c  (x44)
		KnubotFinishTradeIIR = 0x7446F3FA, // KnubotFinishTradeIIR_c  (x2)
		KnubotOpenChatWindowIIR = 0x045B0B0F, // KnubotOpenChatWindowIIR_c  (x43)
		KnubotStartTradeIIR = 0x4B2A47BF, // KnubotStartTradeIIR_c  (x2)
		LockedToChangedIIR = 0x6B0AEC58, // LockedToChangedIIR_t  (x58)
		LookAtIIR = 0x0D155695, // LookAtIIR_t  (x812) - "SelectDeselect"
		LootBagFullUpdateIIR = 0xF096251E, // LootBagFullUpdateIIR_c  (x102) - loot bag (NOT the player bag)
		LootRemoveItemsIIR = 0x70010162, // LootRemoveItemsIIR_c  (x42)
		MagicBuffIIR = 0x6B87E316, // MagicBuffIIR_c  (x728) - a magic buff/effect ("ApplySpell")
		MoveInventoryItemIIR = 0x2A8A1B49, // MoveInventoryItemIIR_t  (x16) - "MoveItemInInventory"
		MoveTaskIIR = 0x96C46740, // MoveTaskIIR_c  (x5564) - movement task (the float-heavy state block)
		NewAllowedLooterIIR = 0x1E2E0139, // NewAllowedLooterIIR_c  (x59)
		NewCurrentLooterIIR = 0xD06568C8, // NewCurrentLooterIIR_c  (x60)
		NewLevelIIR = 0x6D314743, // NewLevelIIR_t - level-up notification (built by NewLevelCodec)
		PetIIR = 0x1F4CB0F7, // PetIIR_c  (x6)
		PlayfieldFIIR = 0x5D85BFC7, // PlayfieldFIIR_t  (x33) - "MapChange"
		QuestFullUpdateIIR = 0x63268258, // QuestFullUpdateIIR  (x28)
		QuestGoalIIR = 0x0774C8C1, // QuestGoalIIR_c  (x27)
		QuestIIR = 0x97E02314, // QuestIIR_c  (x34) - "DeleteQuest"
		QuestJournalIIR = 0x79F8C7A1, // QuestJournalIIR_c  (x17)
		// MainQuestUpdateIIR_c - the active-quest LIST that populates the J-panel. Named QuestLogListIIR here until
		// #545, which is a name we INVENTED (BKDR-31("QuestLogListIIR") == 0xB80D3852, not this id); the client's own
		// class name hashes exactly, and its Activate() switch is what the leading word selects.
		MainQuestUpdateIIR = 0x504B7924, // MainQuestUpdateIIR_c - the active-quest LIST that populates the J-panel
		RegenIIR = 0x28D0AC99, // RegenIIR_c  (x554)
		RemoveFromGroupIIR = 0xE4FF0C85, // RemoveFromGroupIIR_c  (x8) - a member left/was kicked; the AddToGroupIIR complement
		ScryScriptIIR = 0xA36D3B74, // ScryScriptIIR_c  (x390) - cutscene/scry scripts + char-editor callbacks (IntroDone, TheNameIs, ...)
		SetMorphValueIIR = 0xBADF5A4B, // SetMorphValueIIR_c  (x101) - character-creator morph ack
		SetNameIIR = 0xADCE0CDA, // SetNameIIR_t  (x4) - name commit in the create flow
		SetPosIIR = 0x04B80B24, // SetPosIIR_c  (x29)
		SetShadowCoverageIIR = 0xD6C1F0CC, // SetShadowCoverageIIR_c (BKDR-31 of "SetShadowCoverageIIR_c") - environment/lighting update
		SimpleCharFullUpdateIIR = 0xFA015807, // SimpleCharFullUpdateIIR_t  (x3407) - entity spawn ("Spawn")
		SimpleCharKilledByUpdateIIR = 0xB066AFC2, // SimpleCharKilledByUpdateIIR_t  (x2) - kill/death
		SimpleDynelFullUpdateIIR = 0x66AEDD50, // SimpleDynelFullUpdateIIR_c  (x1575) - world object spawn ("SpawnObject")
		SimpleDynelVisualChangeIIR = 0xE7F4F8F8, // SimpleDynelVisualChangeIIR_c  (x548)
		SkillIIR = 0xD558FF16, // SkillIIR_t  (x4) - c2s skillpoint spend: a (statId, delta) list (#739)
		SpellBookIIR = 0x6BA10936, // SpellBookIIR_t  (x1)
		StatDamageIIR = 0xE9F24184, // StatDamageIIR_t  (x450)
		StatIIR = 0xF98E10B3, // StatIIR_t  (x8167)
		StatUpdateIIR = 0x96B8DC59, // StatUpdateIIR_c  (x1595) - entity vitals/stat update (combat HP/Stamina/Mana); see docs/re/quests-and-combat.md
		StopIIR = 0xD751EBB4, // StopIIR_c  (x2097) - stop moving
		TemplateActionIIR = 0x6C300937, // TemplateActionIIR_t  (x7)
		ThreatLevelIIR = 0x1745EF6C, // ThreatLevelIIR_c  (x162)
		TombStoneFullUpdateIIR = 0x0D80E7C9, // TombStoneFullUpdateIIR_c  (x6)
		UpdateInventoryIIR = 0xA74D2814, // UpdateInventoryIIR_t  (x30)
		UpdateSimpleDynelIIR = 0x30536411, // UpdateSimpleDynelIIR_c  (x30)
		N3TeleportIIR = 0x5A32F0D7, // n3TeleportIIR_t  (x27) - teleport
		N3ToClientFadeIIR = 0x66180B20, // n3ToClientFadeIIR_t  (x87) - screen fade
		N3ToClientQuitIIR = 0x04B82B2D // n3ToClientQuitIIR_t  (x3249)

		// FullPlayerCharacterIIR (0x737A6DF9) inventory/bag serialization carve-out (nested 101/104/112/113
		// container blocks, 83-byte item tails): the RE notes live in docs/re/fullplayer-snapshot-reader.md.

		// ---------------------------------------------------------------------------------------------------------
		// BACKLOG: *IIR classes the client (AgeOfConan.exe RTTI) defines but we have NOT added above NOR seen in a
		// capture. Generated by tools/capture/find_missing_opcodes.py. NOT enum members yet. Hashes marked
		// [auth] are AUTHORITATIVE - the class's ReadSubClass vtable-reader (slot 8) recovered by Ghidra
		// (tools/ghidra/DumpVtableReaders.java -> docs/re/iir-readers-binary-only.md has each reader + grammar).
		// [unverified] = reader not dumped yet: both candidate BKDR-31 forms (bare / name+"_c") shown - extend
		// DumpVtableReaders.java to resolve. Add the authoritative one to the enum once dumped or seen on the wire.
		//
		// GAMEPLAY (40) - the real backlog of unseen record types:
		//   AutoToggleCombatIIR          0x107E921F  [auth: AutoToggleCombatIIR_c]
		//   BccIIR                       [alias] = BehaviorControlCenterIIR 0x642CD3D6 (Bcc), already emitted
		//   ClonedFeatsIIR               0xF7BF33DC  [auth: ClonedFeatsIIR_c]  (candidate: full feat set at login, #251)
		//   ConstructionMenuIIR          0xAF084AA6  [auth: ConstructionMenuIIR_c]
		//   ConstructionOrderIIR         0x8AD083D9  [auth: ConstructionOrderIIR_c]
		//   ContainerAddItemIIR          [alias] we deliver via the DispatchAddItem METHOD 0x2020 (AddItemBody), not this IIR
		//   FanaticCommandIIR            0xEE4AA85C  [auth: FanaticCommandIIR_t]
		//   FanaticIIR                   0x1C4528A7  [auth: FanaticIIR_t]
		//   FloatStatIIR                 0x09012E82  [auth: FloatStatIIR]
		//   FlushRDBCachesIIR            0x45ECFD79  [auth: FlushRDBCachesIIR_c]
		//   GMStatIIR                    0xBF12AA7C  [auth: GMStatIIR_c]
		//   GroundEffectIIR              0x45675F8F  [auth: GroundEffectIIR_t]
		//   HealthDamageIIR              [alias] combat damage; we emit DamageResultIIR 0xFEC4158A / StatDamageIIR 0xE9F24184
		//   Int32StatIIR                 0xA2F53610  [auth: Int32StatIIR]
		//   KnubotBaseIIR                0x9ECCA4B0  [auth: KnubotBaseIIR_c]
		//   LootBagIIR                   [alias] base of LootBagFullUpdateIIR 0xF096251E, already emitted
		//   LootCharNeedGreedUpdateIIR   0x8ACD1AC8  [auth: LootCharNeedGreedUpdateIIR_c]
		//   LootDeleteItemsIIR           0x6AC7B325  [auth: LootDeleteItemsIIR]
		//   LootSpecialLootUpdateIIR     0xE9EB9694  [auth: LootSpecialLootUpdateIIR_c]
		//   LootUpdateAllowedLootersIIR  0x51BEF2CB  [auth: LootUpdateAllowedLootersIIR]
		//   MassivePvPDamageLocationIIR  0x156687CC  [auth: MassivePvPDamageLocationIIR_c]
		//   MassivePvpScoreIIR           0x3C5DDB2A  [auth: MassivePvpScoreIIR_c]
		//   MasterLooterOpenIIR          0x26D0EDD5  [auth: MasterLooterOpenIIR_c]
		//   MoveToPosIIR                 [alias] movement; we emit MoveTaskIIR 0x96C46740
		//   PVPDropItemIIR               0x7057225B  [auth: PVPDropItemIIR_t]
		//   PatrolIIR                    0x34DA37AA  [auth: PatrolIIR_c]  (a REAL class, not an alias - see below)
		//   PlayfieldAnarchyFIIR         [alias] playfield/map change; we emit PlayfieldFIIR 0x5D85BFC7
		//   QuestCooldownIIR             0x9CC129C5  [auth: QuestCooldownIIR]
		//   QuestRewardNotifyIIR         0x8BB2799C  [auth: QuestRewardNotifyIIR_c]
		//   QuestRewardSelectionIIR      0x80138D7B  [auth: QuestRewardSelectionIIR_c]
		//   ScriptTextIIR                0xE6C3CCEF  [auth: ScriptTextIIR_t]
		//   ScryLuaIIR                   0x55ACD7D1  [auth: ScryLuaIIR]
		//   SprintRegenIIR               [alias] stamina/sprint regen; we emit RegenIIR 0x28D0AC99
		//   StringStatIIR                0xC415E6ED  [auth: StringStatIIR]
		//   TeleportFailedIIR            0x78BB8C2E  [auth: TeleportFailedIIR_c]
		//   ThreatMeterDataIIR           0x8753247D  [auth: ThreatMeterDataIIR_c]
		//   TimeScaleIIR                 0xDD9BF66A  [auth: TimeScaleIIR_t]
		//   ToolMessageIIR               0x444A6CD8  [auth: ToolMessageIIR_t]
		//   ToolMessageInternalIIR       0x3188C31B  [auth: ToolMessageInternalIIR_t]
		//   UpdateClientVisualIIR        0x356184B3  [auth: UpdateClientVisualIIR_t]
		//
		// ⚠️ PatrolIIR's id is COMPUTED, not observed - the one entry here that is. `PatrolIIR_c` is attested by exactly
		// one string in the whole image, the assert `PatrolIIR_c::PollStatus[%d:%u], invalid id!` at 01fdf970 (used by
		// FUN_00919cc0), and unlike all 141 IIR classes below it has NO RTTI type descriptor - so it is compiled in but
		// never instantiated polymorphically. 0x34DA37AA is what BKDR-31 of the `_c` name gives, which is the rule every
		// other id follows, but nothing here confirms the client ever puts it on the wire: 0 of 120 captures carry it
		// (control: MainQuestUpdateIIR appears in 31). It was previously listed as "[alias] ... (no distinct wire
		// record)" with no id at all, which stated the inference as fact in the other direction.
		//
		// Do not try to confirm an id by scanning the image for it as a constant: these ids are NEVER immediates. The
		// client registers each class by NAME and hashes it at runtime, so even 0x504B7924 - live, captured and
		// implemented - appears in no instruction. Absence of the constant is not evidence about a type.
		//
		// BASE / PROTO / ABSTRACT (19) - mostly C++ base classes + Proto* wrappers, likely not concrete wire subops:
		//   CoreCharacterFullUpdateIIR   bare 0x828E35F0  _c 0x17D885D4  [unverified]
		//   CoreCharacterIIR             bare 0x618AF4C8  _c 0x2AA0EEAC  [unverified]
		//   CoreDoorFullUpdateIIR        bare 0x43A5D8ED  _c 0xF1935D91  [unverified]
		//   CoreFullPlayerCharacterIIR   bare 0xBE8FA878  _c 0x5947765C  [unverified]
		//   FullCharacterIIR             bare 0x5BC77FB8  _c 0x87E67D9C  [unverified]
		//   GameCodeDynelFullUpdateIIR   0x16294E79  [auth: GameCodeDynelFullUpdateIIR]
		//   GameCodeDynelIIR             bare 0xB9287491  _c 0x10DDA035  [unverified]
		//   ProtoCoreCharacterIIR        bare 0xB00B3E90  _c 0xDA35E674  [unverified]
		//   ProtoDeleteItemIIR           bare 0x6F9E4A2C  _c 0x01347B10  [unverified]  (the MESSAGE is confirmed as
		//       DeleteItemIIR_t's payload via the client ctor's vftable - it is these NAME HASHES that stay unverified)
		//   ProtoFullCharacterIIR        bare 0xAA47C980  _c 0x377B7564  [unverified]
		//   ProtoGameCodeDynelIIR        bare 0x07A8BE59  _c 0xC07297FD  [unverified]
		//   ProtoLootBagIIR              bare 0x6862F2DA  _c 0xDB71B03E  [unverified]
		//   ProtoMoveInventoryItemIIR    bare 0x3CE3FA1C  _c 0x93CDEF00  [unverified]
		//   ProtoScryScriptIIR           bare 0x67251EC8  _c 0x325898AC  [unverified]
		//   ProtoSimpleDynelIIR          bare 0x5583C20C  _c 0x039B7AF0  [unverified]
		//   SimpleDynelIIR               bare 0x616BA644  _c 0xB51B3128  [unverified]
		//   VGameActionListIIR           bare 0xCC86A236  _c 0xC566F89A  [unverified]
		//   VToolMessageInternalIIR      bare 0xDB21C47C  _c 0x99C2A160  [unverified]
		//   Vn3PlayfieldFullUpdateIIR    bare 0xF53E418F  _c 0x9EB425B3  [unverified]
		//
		// DEBUG (5) - dev-only records, not expected on the retail wire:
		//   HiveDebugIIR                 0xAC1383D3  [auth: HiveDebugIIR_c]
		//   NPCDebugManagerIIR           0x9F2C283B  [auth: NPCDebugManagerIIR_c]
		//   PlayfieldDebugManagerIIR     0xEE7F5776  [auth: PlayfieldDebugManagerIIR_c]
		//   RequestDebugInfoIIR          0x09F159E4  [auth: RequestDebugInfoIIR_c]
		//   ServerPosDebugInfoIIR        0xAFE243E6  [auth: ServerPosDebugInfoIIR_c]
		//
		// RTTI SWEEP (2026-08-01) - every `.?AV*IIR*@@` type descriptor in the image, hashed. It recovered 141 IIR
		// classes and is the reason to trust the ids above: 80 of the 80 IirTypes members below hash EXACTLY to one of
		// those class names, so neither the names nor the hash rule are guesswork. The 13 ids the sweep found that this
		// file did not mention at all, grouped by what they are:
		//
		//   nested PROTOBUF sub-messages (a field of another IIR's payload, never a wire sub-opcode of their own):
		//     ProtoCoreCharacterIIR_CharacterBcc        0xF3CFB5C8
		//     ProtoCoreCharacterIIR_CharacterStats      0x3FB0B4A5
		//     ProtoCoreCharacterIIR_InventoryEntry      0x27A68225
		//     ProtoCoreCharacterIIR_InventoryEntry_GemSlot 0x3AD379B3
		//     ProtoCoreCharacterIIR_NpcStats            0xDD7C872D
		//     ProtoCoreCharacterIIR_PlayerStats         0x3BB3CAEF
		//     ProtoGameCodeDynelIIR_RunningEffects      0x06B5B509
		//     ProtoLootBagIIR_SpecialLootOwnerEntry     0xF810A79B
		//     ProtoScryScriptIIR_Variable               0x05E16A33
		//   CLIENT-SIDE view classes (UI, not wire):
		//     IIRDebugView                              0x52076086
		//     IIRStatListViewItem                       0x9D5F1DDC
		//   genuine IIR classes we have never seen on the wire:
		//     n3PlayfieldFullUpdateIIR_t                0x5D7884EE
		//     n3ToServerUnBlockIIR_t                    0x87A3B396
		//
		// ⚠️ The client spells the n3* classes with a LOWERCASE n3 - `n3TeleportIIR_t`, `n3ToClientFadeIIR_t`,
		// `n3ToClientQuitIIR_t`. The members below are `N3…` for C# convention; the ids are hashes of the CLIENT
		// spelling, so do not "fix" a member name into the hash input.
		// ---------------------------------------------------------------------------------------------------------
	}

	// The PlayerAgent (c2s) / PlayerInterface (s2c) METHOD CONSTANTS - the 32-bit value the client registers each method
	// with, and the thing that actually names a method on this interface pair. Read off the client's own registration
	// table (`DumpPlayerAgentCtor.java` / `StrXrefs.java`), so every name is the client's; 25 c2s + 26 s2c is the
	// complete interface.
	//
	// These are the SOURCE the wire encoding is derived from, not a parallel catalog: the RPC header's method field is
	// `varint(constant)`, which `Rpc` writes whole. `PlayerAgentMethodIds` reads these enums for both directions of that.
	//
	// ⚠️ **This pair has no opcodes, and there is no opcode enum for it** (Gitea #613). Every other class registers its
	// methods by INDEX, small enough that the whole constant is the varint's first group - which is why those enums
	// name the index directly (#615) and a hex dump shows it as the method tag `0x20` followed by that byte. Here the
	// constants are 32-bit HASHES, so the same two bytes keep only 7 of 32 and name two methods at once: `0x20EC` is
	// BOTH `ReceivedCharacterSettings` and `AuthenticateLoggedIn`, `0x20D6` BOTH `VerifyLanguageSetting` and
	// `UpdateCharacterPreview` - all four in the corpus. There used to be `PlayerAgentOpcodes`/`PlayerInterfaceOpcodes`
	// enums listing those observed two-byte values; they were deleted because a value that names two methods cannot be
	// a catalog key. Dispatch, naming and the docs all read these constants (#608/#610). To find one of these in a hex
	// dump, `PlayerAgentMethodIds.ObservedOpcode` computes the two bytes you would see.
	public enum PlayerAgentMethods : uint
	{
		Authenticate = 0x9CB2CB03,
		AuthenticateLoggedIn = 0x9D991E6C,
		LoginFromCrash = 0xCBDE126E,
		CreateCharacter = 0xBE735486,

		// Retail sends THIS one, not plain `CreateCharacter` - the client has both, and `CreateCharacter` has never
		// been seen on the wire.
		CreateCharacterWithData = 0xCA2C4E5E,
		LoginCharacter = 0xEF616EB6,
		RenameCharacter = 0x1B2CF268,
		RequestDeleteCharacter = 0xC3C5C31D,
		VerifyLanguageSetting = 0x9847AED6,
		AddOfflineLevel = 0x2976E1B7,
		UnlockCharacter = 0x9C370D1E,
		SetHardcoreLevel = 0x6A6B621C,
		GetDimensionList = 0x43476328,
		RequestSuggestNickname = 0xDBE172E5,
		GetStartupData = 0x6A546D41,
		LoadFacebookToken = 0xD3907026,
		SaveFacebookToken = 0xD8AD362B,
		UpdateFacebookFriends = 0x3A96B2B3,
		RecruitFacebookFriends = 0x5B51044B,
		UpdateSubscriptionSettings = 0x516C9EF5,
		UpdateCharacterSettings = 0x01035784,
		UpdateCharacterPreview = 0x4D661656,
		FinishGetStartupData = 0xDFD8518E,
		ReceivedCharacterSettings = 0x3C7C926C,
		ClientReplyKeepCookie = 0x43D90C1B
	}

	// Server->client. Nine of these names were INVENTED from observed behaviour until #320 recovered the client's own
	// registration table; each one that changed carries the old name, because that is what the rest of the tree called
	// it and what older notes and captures refer to.
	public enum PlayerInterfaceMethods : uint
	{
		OnFacebookTokenResult = 0x52454C9C,   // was "Ox209C" - the reply to c2s LoadFacebookToken
		OnUpdateFacebookFriends = 0x88750390,
		OnFacebookFriendSignedUp = 0x7955BD80,
		AuthenticateAck = 0x5DC18991,         // was "InitAuth"

		// The four login handoffs. They additionally carry a `full_routing_context` (RpcHeader field 3), which is what
		// the codebase used to read as one invented opcode `0x1A07` plus an opaque 13-byte "session blob" (#610).
		LoginConnectReady = 0xD8B66C79,
		CSServerConnectReady = 0x5AED2A60,
		ChatServerConnectReady = 0x23A632FA,

		UpdateClientPlayerData = 0xC414C5EF,  // was "SendCharacterList"
		SetDimensionList = 0xF899B14C,        // was "SendRealmList" (a dimension IS a realm)
		SetStats = 0xA7C86A29,
		SuggestNicknameResult = 0xE8EAD4F8,
		PlayerSetupComplete = 0x4F91A58C,     // was "SendCharacterRealmData"
		LoginQueuePosition = 0x0FE09F03,
		OfflineLevelApplied = 0x3E8F0623,
		CharacterUnlocked = 0x5CE97913,       // was "Ox2093" - the reply to c2s UnlockCharacter
		HardcoreLevelSet = 0x1562CE84,
		SendSubscriptionSettings = 0x15F7AF22,
		SendCharacterSettings = 0x233605B9,   // was "Ox20B9"
		SendCharacterPreview = 0x206B6EE5,    // was "Ox20E5"
		SendCharacterSettingsCheckSumMap = 0x0C09CA25, // was "SmallCharList"
		VerifyClientStillConnected = 0x00C46317,
		OverrideGameTweaks = 0x7C47F763,
		CharacterNeedsNewName = 0x9625EB4A,
		LoginProblem = 0xD4063CA0,
		TeleportProblem = 0xE33EBAE1,
		ResetLanguage = 0xD4C59816
	}
	// UniverseAgent client->server: matches the recovered table exactly (3 methods).
	public enum UniverseAgentMethods : uint
	{
		InitiateAuthentication = 0x00,
		AnswerChallenge = 0x01,
		ClientDisconnected = 0x02
	}

	// UniverseInterface server->client: the recovered table aligns here (0x2000 is the DH challenge,
	// 0x2005 sets the region state - both match observed behavior), unlike the bigger drifting interfaces.
	public enum UniverseInterfaceMethods : uint
	{
		DispatchChallenge = 0x00, // (was "InitiateAuthentication" - it carries the DH challenge reply)
		DispatchAckAuthenticate = 0x01, // carries the PlayerAgent realm handoff (was "SendPlayerAgentRealm")
		DispatchLoginProblem = 0x02,
		DispatchSetUpdateManager = 0x03,
		DispatchSetUniverseManagerError = 0x04,
		DispatchSetRegionState = 0x05 // (was "SetRegion")
	}

	// GameInterface server->client dispatch table (16 methods; +3 inserted no-arg).
	// Generated from the Ghidra s2c dispatch tables (docs/re/aoc-rpc-method-opcodes.md) (the method IS the dispatch switch index);
	// <no-arg> insertion slots have a known index but no recoverable name -> Reserved0xNN.
	//
	// ⭐ #792: a no-arg slot is nameable through the INTERFACE VTABLE, because its dispatch branch is a bare stub -
	// `slot index = method id + 1` on this interface family, so 0x04 is vtable +0x14, and `GameInterface_t`'s
	// override of it logs `GameInterface_t::OnQuitGame` in the client's own words. Retail sends it 4 times in 4
	// captures; the handler runs the disconnect/redirect path.
	public enum GameInterfaceMethods : uint
	{
		DispatchPassBlob = 0x00,
		DispatchAckAuthenticate = 0x01,
		DispatchRedirectToServer = 0x02,
		Reserved0x03 = 0x03,
		DispatchQuitGame = 0x04, // GameInterface_t::OnQuitGame (#792)
		DispatchPong = 0x05,
		DispatchPongWithPacketTiming = 0x06,
		DispatchInfoToUser = 0x07,
		DispatchGameStats = 0x08,
		DispatchGameServerStats = 0x09,
		DispatchGameServerDataset = 0x0A,
		DispatchReportDimesionID = 0x0B,
		DispatchReportServerID = 0x0C,
		DispatchDataSink = 0x0D,
		Reserved0x0E = 0x0E,
		DispatchDisplayPlayfieldAccessPopup = 0x0F
	}

	// GameCharInterfaceProject server->client dispatch table (16 methods; +1 inserted).
	// Generated from the Ghidra s2c dispatch tables (docs/re/aoc-rpc-method-opcodes.md) (the method IS the dispatch switch index);
	// <no-arg> insertion slots have a known index but no recoverable name -> Reserved0xNN.
	public enum GameCharInterfaceProjectMethods : uint
	{
		DispatchUpdateAggroStatus = 0x00,
		DispatchGuildBankData = 0x01,
		Reserved0x02 = 0x02,
		DispatchRequestApprentice = 0x03,
		DispatchAddApprenticeEntry = 0x04,
		DispatchRemoveApprenticeEntry = 0x05,
		DispatchDoYouWantToShiftTeamInstance = 0x06,
		DispatchInDungeonPlayfield = 0x07,
		DispatchReplyDebugGetProcData = 0x08,
		DispatchReplyDebugGetSpellProcData = 0x09,
		DispatchSendHitAndMitigationTable = 0x0A,
		DispatchSetIsSprinting = 0x0B,
		DispatchSyncCanSprint = 0x0C,
		DispatchSyncRegenDelayed = 0x0D,
		DispatchUpdateSprintBar = 0x0E,
		DispatchOpenSiteInBrowserOnClient = 0x0F
	}

	// TradepostAgent client->server methods (the AH + mail selector after the endpoints) - the complete
	// recovered TradepostAgent.client_to_server table (the method IS the table position; alignment verified on
	// ItemSearch/BuyItem/Close). The method ids COLLIDE with ordinary GameServer opcodes, so tradepost packets
	// route by RPC class id (RpcInterfaces.TradepostAgentType), never by opcode alone. Behaviour that differs
	// from the name is noted per line (docs/subsystems/tradepost-protocol.md).
	public enum TradepostAgentMethods : uint
	{
		GetAllMails = 0x00, // the mailbox poll
		GetMailByID = 0x01, // body [mailId:u64]
		SendMail = 0x02,
		MarkMailRead = 0x03, // also observed for the delete-side flow
		CheckNewMail = 0x04,
		GetMailItems = 0x05,
		GetMailItem = 0x06, // body [mailId][itemId]
		GetItemData = 0x07, // observed with a [mailId]-style body (was decoded as "GetMailItems")
		DeleteMail = 0x08,
		CancelMail = 0x09,
		TradepostSellItem = 0x0A,
		TradepostChangeItemPrice = 0x0B,
		TradepostCancelSellItem = 0x0C,
		ItemSearch = 0x0D, // TradepostItemSearch (query body)
		BuyItem = 0x0E, // TradepostBuyItem [saleId:u64][searchResultId:u64]
		TradepostOpen = 0x0F, // not observed in captures - the client opens with UpdateItemTimeouts instead
		Close = 0x10, // TradepostClose (empty body)
		UpdateItemTimeouts = 0x11, // TradepostUpdateItemTimeouts - sent (empty body) when the AH window opens
		SetAsyncEnabled = 0x12,
		SetNumWorkerThreads = 0x13
	}

	// TradepostInterface server->client dispatch table (8 methods; mail + tradepost search).
	// Generated from the Ghidra s2c dispatch tables (docs/re/aoc-rpc-method-opcodes.md) (the method IS the dispatch switch index);
	// <no-arg> insertion slots have a known index but no recoverable name -> Reserved0xNN.
	public enum TradepostInterfaceMethods : uint
	{
		DispatchOpenTradepost = 0x00,
		DispatchClose = 0x01, // no-arg; the string catalog missed it (recovered from the TradepostInterface RE)
		DispatchMailMessage = 0x02,
		DispatchSendMailResult = 0x03,
		DispatchRetrieveMailResult = 0x04,
		DispatchTradepostSearchResults = 0x05,
		DispatchDeleteCanceledMail = 0x06,
		DispatchDeleteCanceledMailItem = 0x07
	}

	// GameCharInterface server->client dispatch table (181 methods; +24 inserted no-arg). The main world s2c surface: inventory (DispatchAddItem 0x20), shop, loot, quests, trade, spells/feats, formations.
	// Generated from the Ghidra s2c dispatch tables (docs/re/aoc-rpc-method-opcodes.md) (the method IS the dispatch switch index);
	// <no-arg> insertion slots have a known index but no recoverable name -> Reserved0xNN.
	public enum GameCharInterfaceMethods : uint
	{
		DispatchUpdateAggroStatus = 0x00,
		DispatchUpdateName = 0x01,
		DispatchUpdateBindLocation = 0x02,
		DispatchGuildNotEnoughCash = 0x03,
		Reserved0x04 = 0x04,
		DispatchUpdateCash = 0x05,
		DispatchUpdateCashRepository = 0x06,
		DispatchMoveInventoryItemToOrFromGuildBank = 0x07,
		DispatchSetQuestMarker = 0x08,
		DispatchChangeItemStackSize = 0x09,
		DispatchLearnSpell = 0x0A,
		DispatchForgetSpell = 0x0B,
		Reserved0x0C = 0x0C,
		DispatchTradeskillComplete = 0x0D,
		Reserved0x0E = 0x0E,
		DispatchClearRecipe = 0x0F,
		DispatchRemoveRecipesByGroupClient = 0x10,
		DispatchUntrainSingleFeat = 0x11,
		DispatchDisplayItemLootChatMessage = 0x12,
		Reserved0x13 = 0x13,
		DispatchSendNPCAutoTradeCashAmount = 0x14,
		DispatchSetDialogueCamera = 0x15,
		DispatchAddTag = 0x16,
		DispatchRemoveTag = 0x17,
		DispatchResurrectRequest = 0x18,
		DispatchSetDbDebugCategory = 0x19,
		DispatchSetDbDebugLevel = 0x1A,
		// ⚠️ #792: NOT nameless for want of looking - `GamecodeClient_t`'s override of its vtable slot (+0x70) is an
		// EMPTY function, so the client does nothing with it. Retail sends it 102 times across 59 of 132 captures,
		// immediately before every IntendedTeleportIIR, and the client ignores every one. So the zone-change
		// handshake is not missing a step we fail to perform - this one is a no-op on the receiving end.
		//
		// ⭐ The "immediately before every" is now MEASURED and exact: of the 102 frames, the next s2c frame is an
		// `IntendedTeleportIIR` (`0x10D27BC0`) **102 times, with no exceptions** - cross-map handoffs and same-map
		// revives alike. That is what makes it safe to send unconditionally from the teleport senders, which we now
		// do (`GameServerListener.SendTeleportPending`) for byte-stream fidelity and nothing else.
		Reserved0x1B = 0x1B,
		DispatchSetItemStat = 0x1C,
		DispatchAddToItemSlot = 0x1D,
		DispatchRemoveFromItemSlot = 0x1E,
		DispatchRemoveItem = 0x1F,
		DispatchAddItem = 0x20,
		DispatchMoveInventoryItemToGuildBank = 0x21,
		DispatchMoveInventoryItemFromGuildBank = 0x22,
		DispatchCharacterNameReply = 0x23,
		DispatchValidResurrectionSpots = 0x24,
		Reserved0x25 = 0x25,
		DispatchReplyPCDebugData = 0x26,
		DispatchReplyLootForCharacter = 0x27,
		DispatchReplyLootForCharacterAddHash = 0x28,
		DispatchReplyLootForCharacterTemplateClusterOk = 0x29,
		DispatchReplyLootForCharacterTemplateClusterError = 0x2A,
		DispatchReplyLootForCharacterAddTreasureTemplate = 0x2B,
		DispatchReplySimulateLootForID = 0x2C,
		DispatchFormationSetData = 0x2D,
		DispatchFormationClaimPosition = 0x2E,
		DispatchFormationUpdatePosition = 0x2F,
		DispatchFormationClearPosition = 0x30,
		Reserved0x31 = 0x31,
		DispatchFormationUpdatePosRot = 0x32,
		DispatchShopData = 0x33,
		DispatchCloseShop = 0x34,
		DispatchItemSoldToShop = 0x35,
		DispatchItemBoughtBack = 0x36,
		DispatchOpenLootBag = 0x37,
		DispatchCloseLootBag = 0x38,
		DispatchDoNeedGreedRollForItemsInLootBag = 0x39,
		DispatchCancelNeedGreedRollForItemsInLootBag = 0x3A,
		DispatchOpenDialogForLockedItemsInLootBag = 0x3B,
		DispatchCloseDialogForLockedItemsInLootBag = 0x3C,
		DispatchMasterLooterOfferItemTo = 0x3D,
		DispatchSlaveLooterAnswer = 0x3E,
		DispatchMasterLooterCancelOffer = 0x3F,
		DispatchOverrideGameTweaks = 0x40,
		DispatchShowClientFIFO = 0x41,
		DispatchAddFormation = 0x42,
		DispatchRemoveFormation = 0x43,
		DispatchUpgradeCombo = 0x44,
		DispatchUpgradeComboStep = 0x45,
		DispatchXPSelectClass = 0x46,
		DispatchNextQuestAvailable = 0x47,
		DispatchQuestShareGlobalFeedback = 0x48,
		DispatchQuestShareIndividualFeedback = 0x49,
		DispatchQuestShareRequest = 0x4A,
		DispatchUpdateTeamQuestList = 0x4B,
		DispatchCancelTeamQuestList = 0x4C,
		DispatchUpdateTokenAmount = 0x4D,
		DispatchLockTemplate = 0x4E,
		DispatchApplyFactionRank = 0x4F,
		DispatchApplyFactionPoints = 0x50,
		Reserved0x51 = 0x51,
		DispatchAddSpecialAbility = 0x52,
		DispatchRemoveSpecialAbility = 0x53,
		DispatchPerformSpecialAction = 0x54,
		DispatchQueuePetCommand = 0x55,
		DispatchACGTeleportChoice = 0x56,
		DispatchACGSelfInstanceCleared = 0x57,
		DispatchACGTeamBindChanged = 0x58,
		Reserved0x59 = 0x59,
		DispatchOverrideJumpHeight = 0x5A,
		DispatchOverrideJumpSpeed = 0x5B,
		DispatchVisualSimulateNpc = 0x5C,
		DispatchReceivedStockPhraseText = 0x5D,
		DispatchScryShowMessageBox = 0x5E,
		DispatchScryCloseMessageBox = 0x5F,
		DispatchScryShowOptionsBox = 0x60,
		DispatchScryCloseOptionsBox = 0x61,
		DispatchOpenScenarioResults = 0x62,
		DispatchScrySendCustomGUIData = 0x63,
		DispatchFeatUntrainMessageBox = 0x64,
		DispatchCraftUntrainMessageBox = 0x65,
		DispatchUnbindItemReply = 0x66,
		DispatchBroadcastMessage = 0x67,
		// ⭐ #792: the TRADE block's four no-arg slots, named through the interface VTABLE. On this family the
		// dispatch branch of a no-arg method is a bare stub calling one virtual slot, and `slot = (method + 1) * 4`
		// holds on all eight measured (0x1B, 0x6A, 0x6B, 0x6C, 0x6D, 0x71, 0x72, 0x75) - the four that DO have a name
		// are the controls. `GamecodeClient_t` is the implementing class; `GameCharInterface_t`'s overrides are empty.
		//
		//   0x6A -> +0x1AC -> `CoreTradeHolder::TradeAbort()`, the client's own assert string ("is closing all
		//                     trades"). ⭐ Its TRIGGER is measured: 152 of the 153 frames in the corpus are the
		//                     immediate reply to `CloseShop` (the odd one follows an `UpdateQuestMarkerNPC` - a shop
		//                     closed by walking away). A vendor session is a trade to the client, which is why one
		//                     message serves both. We answer `CloseShop` with it (`SendTradeAbort`).
		//   0x6B -> +0x1B0 -> the LOCAL holder's `accepted` byte set + the trade window refreshed. Byte for byte the
		//                     same body as 0x6C's, which is named DispatchPartnerAcceptedTrade and differs only in
		//                     taking the ID32 of whose holder to stamp - so 0x6B is the argument-less YOU half.
		//   0x6D -> +0x1B8 -> the holder's own vtable slot 2: clear the character's in-trade flag, zero the accepted
		//                     byte and both cash slots, free both item lists, then the trade-ended signal with 1
		//                     where TradeAbort sends 0. ⭐ The CAPTURES say the same thing independently - it lands on
		//                     BOTH connections, empty, as the trade commits (ITradeOutbound.SendTradeCompleted).
		//   0x71 -> +0x1C8 -> the exact inverse of 0x6B: the accepted byte set back to 0. Never sent in the corpus.
		DispatchRequestTrade = 0x68,
		DispatchStartTrade = 0x69,
		DispatchTradeAbort = 0x6A,
		DispatchAcceptedTrade = 0x6B,
		DispatchPartnerAcceptedTrade = 0x6C,
		DispatchTradeCompleted = 0x6D,
		DispatchPartnerUpdatedCash = 0x6E,
		DispatchTradePartnerAddedItem = 0x6F,
		DispatchTradeAddedItem = 0x70,
		DispatchUnacceptedTrade = 0x71,
		Reserved0x72 = 0x72, // #792: fires the trade-window refresh signal and nothing else. Never sent in the corpus.

		DispatchTradePartnerRemovedItem = 0x73,
		DispatchTradeRemovedItem = 0x74,
		// #792: the client logs "DeleteCachedLDBData remote method was called." under its `LDB` category, then builds
		// the LDB cache object and resets it. Never sent in the corpus.
		DispatchDeleteCachedLDBData = 0x75,
		DispatchShortcutBarReply = 0x76,
		Reserved0x77 = 0x77,
		Reserved0x78 = 0x78,
		Reserved0x79 = 0x79,
		DispatchAddWayPoint = 0x7A,
		DispatchRemoveWayPoint = 0x7B,
		DispatchReplyWaveRespawnTimerUpdate = 0x7C,
		DispatchGMReplySpellData = 0x7D,
		DispatchGMReplyFeatData = 0x7E,
		DispatchGMReplyFeatClassData = 0x7F,
		DispatchInstanceSelectionForCurrentPlayfield = 0x80,
		DispatchApprenticeModeOnOff = 0x81,
		DispatchPong = 0x82,
		DispatchUpdateTeamMemberPosList = 0x83,
		DispatchDynamicSpaceDebugInfo = 0x84,
		DispatchSetGameTweakValue = 0x85,
		Reserved0x86 = 0x86,
		DispatchMinigameScoreChanged = 0x87,
		DispatchMinigameFullScore = 0x88,
		Reserved0x89 = 0x89,
		DispatchMinigameMatchStarted = 0x8A,
		DispatchMinigameMatchEnded = 0x8B,
		DispatchMinigameCaptureZoneUpdate = 0x8C,
		DispatchMinigameCaptureZoneList = 0x8D,
		DispatchMinigameCaptureZoneLeaveEnter = 0x8E,
		DispatchForceStopMinigame = 0x8F,
		DispatchSetMinigameTime = 0x90,
		DispatchMinigameStartsInXSeconds = 0x91,
		Reserved0x92 = 0x92,
		DispatchMassivePvPFullScoreUpdate = 0x93,
		DispatchMassivePvPGuildScoreUpdate = 0x94,
		DispatchMassivePvPPlayerScoreUpdate = 0x95,
		DispatchMassivePvPScoreNames = 0x96,
		Reserved0x97 = 0x97,
		Reserved0x98 = 0x98,
		DispatchShowVulnerabilityWindowGui = 0x99,
		DispatchPassiveAbilityEquip = 0x9A,
		DispatchPassiveAbilityUnequip = 0x9B,
		DispatchReplyRecipeInfo = 0x9C,
		DispatchAddEffectPackage = 0x9D,
		DispatchEnterExitGamingRoom = 0x9E,
		DispatchReplyTeamInvite = 0x9F,
		DispatchReplyNewJoinHasHostileInFriends = 0xA0,
		Reserved0xA1 = 0xA1,
		DispatchReplyItemForCashSaleSetData = 0xA2,
		DispatchReplyItemForCashSaleUpdateData = 0xA3,
		DispatchItemSaleResult = 0xA4,
		DispatchReplyItemSaleFavoriteData = 0xA5,
		DispatchItemSalesUpdateBalance = 0xA6,
		DispatchPlayfieldAccessDenied = 0xA7,
		DispatchPlayfieldAccessGranted = 0xA8,
		DispatchSendPlayfieldAccessData = 0xA9,
		DispatchSendPlayfieldLockoutData = 0xAA,
		DispatchLockAllSpells = 0xAB,
		DispatchAddSpellResistance = 0xAC,
		DispatchRemoveSpellResistance = 0xAD,
		DispatchSetStackinglineResists = 0xAE,
		DispatchPlayerServicesUpdated = 0xAF,
		DispatchOnFacebookTokenResult = 0xB0,
		DispatchOnUpdateFacebookFriends = 0xB1,
		DispatchOnFacebookFriendSignedUp = 0xB2,
		DispatchRequestGetEffect = 0xB3,
		DispatchSignalWorldStatChanged = 0xB4
	}
}
