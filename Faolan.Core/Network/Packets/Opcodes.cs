namespace Faolan.Core.Network.Packets
{
	namespace Opcodes
	{
		public enum AgentServerOpcodes : ushort
		{
			Hello = 0x0000,
			Whisper = 0x001E, // client sends a tell: [u32 targetCharId][string][00 01 00] (whisper capture 2026-06-12)
			ChannelChat = 0x0041, // client posts a message to a chat channel: [byte type][u32 channelId][string][0000]
			ChatReady = 0x0064 // small chat-init handshake the client sends after the channel subscriptions (000102)
		}

		public enum AgentServerRespondsOpcodes : ushort
		{
			CharacterChat = 0x0022,
			SystemMessage = 0x0023,
			Whisper = 0x001E, // incoming tell: [u32 senderCharId][string][00 01 00], preceded by 0x0014 sender info
			ChannelChat = 0x0041, // a channel message relayed to a client: [byte type][u32 channelId][u32 senderId][string][0000]
			Ox0014 = 0x0014, // sender id + name; also sent before a ChannelChat so the client can show the speaker
			Ox003C = 0x003c,
			Ox0064 = 0x0064 // echo of the client's chat-init handshake
		}

		// Client->server methods - the COMPLETE recovered CSPlayerAgent.client_to_server table. The 16-bit opcode
		// is a METHOD INDEX (0x2000 + position) into docs/aoc-client-rpc-methods.json; alignment verified against
		// the hoxxling guild capture (0x2000 Authenticate, 0x201C AcceptGuildInvitation fired exactly once on the
		// accept, 0x201E/0x202F/0x2040/0x2041/0x2077 all behave as named). See Captures/decode_guild.py.
		public enum CsPlayerAgentOpcodes : ushort
		{
			Authenticate = 0x2000,
			CharacterAuthenticated = 0x2001,
			CharacterDisconnected = 0x2002,
			TeamInvite = 0x2003,
			TeamInviteByName = 0x2004,
			TeamApply = 0x2005,
			TeamSetLootOption = 0x2006,
			TeamSetMasterLooter = 0x2007,
			AcceptTeamInvite = 0x2008,
			DeclineTeamInvite = 0x2009,
			CharacterSearch = 0x200A,
			SearchForTeam = 0x200B,
			SetNewTeamLeader = 0x200C,
			SummonCharacters = 0x200D,
			AcceptSummon = 0x200E,
			DeclineSummon = 0x200F,
			GetExtendedCharacterInfo = 0x2010,
			GetExtendedCharacterInfoByName = 0x2011,
			SetLookingForTeamStatus = 0x2012,
			SetLookingForMemberStatus = 0x2013,
			GetLookingForTeamStatus = 0x2014,
			GetLookingForMemberStatus = 0x2015,
			SetCommunityServerOptions = 0x2016,
			StartVoteKick = 0x2017,
			VoteKickAnswer = 0x2018,
			StartVoteRetreat = 0x2019, // observed body [u32 0xC350][u32 clientInstance] at login - semantics unconfirmed
			VoteRetreatAccept = 0x201A,
			VoteRetreatReject = 0x201B,
			AcceptGuildInvitation = 0x201C,
			DeclineGuildInvitation = 0x201D,
			ClientRequestGuildInfo = 0x201E,
			ClientRequestGuildName = 0x201F,
			ClientRequestCharIDByName = 0x2020,
			CreateGuild = 0x2021,
			GuildInviteByName = 0x2022,
			GuildInvite = 0x2023,
			RemoveGuildInvitationCooldown = 0x2024,
			GuildPromote = 0x2025,
			GuildDemote = 0x2026,
			GuildKick = 0x2027,
			GuildLeave = 0x2028,
			GuildTax = 0x2029,
			GuildGovernmentForm = 0x202A,
			GuildRecruitmentMode = 0x202B,
			GuildName = 0x202C,
			GuildDescription = 0x202D,
			GuildMessageOfTheDay = 0x202E,
			GuildRequestMemberList = 0x202F,
			GuildApplication = 0x2030,
			GuildRejectApplication = 0x2031,
			SetLookingForGuild = 0x2032,
			GuildMergeRequest = 0x2033,
			GuildMergeReply = 0x2034,
			GuildWithdrawMoney = 0x2035,
			GetPvpPointsInfo = 0x2036,
			AddPvpPoints = 0x2037,
			RemovePvpPoints = 0x2038,
			ClearPvpPoints = 0x2039,
			GetGuildRenownInfo = 0x203A,
			AddGuildRenown = 0x203B,
			RemoveGuildRenown = 0x203C,
			SetGuildRenown = 0x203D,
			CalculateRankings = 0x203E,
			UnlockGuildRenown = 0x203F,
			RequestRenownHistory = 0x2040,
			RequestRenownRankings = 0x2041,
			RequestBattleKeepInformation = 0x2042,
			SearchForGuilds = 0x2043,
			GMGuildName = 0x2044,
			RequestGuildCitySpot = 0x2045,
			ApplyForMassivePVPBattleQueue = 0x2046,
			WithdrawFromMassivePVPBattleQueue = 0x2047,
			MassivePvPBattleQueueGMCommand = 0x2048,
			MassivePvpBattleQueueStatusRequest = 0x2049,
			PvPMatchMakingCreateMatch = 0x204A,
			PvPMatchMakingJoinMatch = 0x204B,
			PvPMatchMakingSetSide = 0x204C,
			PvPMatchMakingMatchSay = 0x204D,
			PvPMatchMakingLeaveMatch = 0x204E,
			PvPMatchMakingSearchForMatches = 0x204F,
			PvPMatchMakingGetMatchInfo = 0x2050,
			PvPMatchMakingTeleportToMinigame = 0x2051,
			PvPMatchMakingPutIntoMatchMaking = 0x2052,
			PvPMatchMakingRemoveFromMatchMaking = 0x2053,
			PvPMatchMakingRequestInfo = 0x2054,
			PvPMatchMakingIsInMatchMaking = 0x2055,
			PvPMatchMakingKickPlayer = 0x2056,
			PvPMatchMakingBanPlayer = 0x2057,
			PvPMatchMakingMakePlayerMatchOwner = 0x2058,
			PvPMatchMakingForceSide = 0x2059,
			PvPMatchMakingStartMatch = 0x205A,
			DebugPvPMatchMakingShowDebugInfo = 0x205B,
			DebugPvPMatchMakingStopAllMatches = 0x205C,
			DebugPvPMatchMakingStopCurrentMatch = 0x205D,
			DebugPvPMatchMakingSetNewTimeLimit = 0x205E,
			DebugPvPMatchMakingRestart = 0x205F,
			DebugPvPMatchForcePlayerIntoCurrentMatch = 0x2060,
			DebugPvPMatchMakingDoMatchMaking = 0x2061,
			DebugPvPMatchForceStartMatch = 0x2062,
			DebugPvPMatchForceEnterAll = 0x2063,
			DebugCommunityServerModuleStart = 0x2064,
			DebugCommunityServerModuleStop = 0x2065,
			SetAsyncEnabled = 0x2066,
			SetNumWorkerThreads = 0x2067,
			RaidCreate = 0x2068,
			RaidLeave = 0x2069,
			RaidInviteTeam = 0x206A,
			RaidInviteTeamAccept = 0x206B,
			RaidInviteTeamDecline = 0x206C,
			RaidKickTeam = 0x206D,
			RaidKickCharacter = 0x206E,
			RaidMoveTeamMember = 0x206F,
			RaidSwapCharacters = 0x2070,
			RaidInviteCharacter = 0x2071,
			RaidInviteCharacterByName = 0x2072,
			RaidAcceptInvite = 0x2073,
			RaidDeclineInvite = 0x2074,
			RaidSetLootOption = 0x2075,
			RaidSetMasterLooter = 0x2076,
			MassivePvpInfoRequest = 0x2077,
			SendTeamInfo = 0x2078,
			GMRequestGuildInfoFromChar = 0x2079,
			QueueGroupFinder = 0x207A,
			SelectGroupFinderRoles = 0x207B,
			LeaveGroupFinder = 0x207C,
			ReadyGroupFinder = 0x207D,
			RequestRaidQueueInfo = 0x207E,
			UpdateDefaultRaid = 0x207F,
			SetFeaturedRaid = 0x2080,
			RequestFeaturedRaidNeededRole = 0x2081,
			RequestNeededRole = 0x2082,
			RequestRotatingActivityChildInfo = 0x2083,
			GetWorldStat = 0x2084,
			RequestItemSalesBalance = 0x2085,
			RequestItemSalesCoupons = 0x2086,

			// Beyond the recovered table (the live build has more methods): the client ECHOES these
			// server->client GroupFinder values back on the upload path.
			Ox20C5 = 0x20C5, // GroupFinder remoteformat echo
			Ox20D0 = 0x20D0
		}

		// Server->client. The live build's CSPlayerInterface dispatch table has unnamed extra methods (indices
		// drift vs the recovered name table), so these are named by decoded CONTENT, not by table position.
		public enum CsPlayerAgentResponseOpcodes : ushort
		{
			Authenticate = 0x2000,
			GuildInvite = 0x202A, // [guild ID32][guildName][invitee ID32][inviterName]
			GuildMemberJoined = 0x202F, // [member ID32][member record]
			GuildMemberUpdate = 0x2030, // [member record]
			RenownRankings = 0x2038,
			RenownHistory = 0x2039,
			GuildFullUpdate = 0x203F, // guild header + MOTD + renown block + self record + roster
			GuildInfoSameGuild = 0x2042,
			GuildInfoOtherGuild = 0x2043,
			Ox20A5 = 0x20A5,
			Ox20C5 = 0x20C5,
			Ox20D0 = 0x20D0
		}

		// Handler-facing opcodes for the GameServer connection's blanket switch. The connection MULTIPLEXES four
		// RPC class-pairs (GameAgent, GameCharAgent, GameCharAgentProject, TradepostAgent - see
		// docs/aoc-opcode-ids.md), so the same 16-bit value can be different methods per receiver class; the
		// correct dispatch key is (class id, opcode) and tradepost already routes by class. The small class
		// tables top out at 0x2013 (TradepostAgent) / 0x200C (project) / 0x200A (GameAgent), so every value
		// >= 0x2014 is unambiguously a GameCharAgent method and is named from that table; lower values keep their
		// behavior-derived names with the per-class candidates noted. Full per-class catalogs are below
		// (GameAgentOpcodes / GameCharAgentOpcodes / GameCharAgentProjectOpcodes).
		public enum GameServerOpcodes : ushort
		{
			Hello = 0x2003, // GameAgent.Authenticate2 (the auth packet); COLLIDES with GameCharAgent.SetTutorialStats (x80 in captures)
			Ping = 0x2005, // GameAgent.Ping; GameCharAgent.GuildInfoUpdate shares the value
			PassBlob = 0x2000, // GameAgent.PassBlob - the IIR carrier (sub-opcode in body); COLLIDES with GameCharAgentProject.RequestAggroStatus (x7621)
			ChatCommand = 0x2002, // GameAgent.ChatVicinity (typed chat, carries GM commands); COLLIDES with GameCharAgent.RequestBindLocation (x49)
			SetSprintInputState = 0x200C, // GameCharAgentProject.SetSprintInputState (x2092 - the sprint key)
			VanityToggle = 0x200B, // GameCharAgentProject.ToggleVanityArmorFilter

			// >= 0x2014: GameCharAgent methods (unambiguous).
			SplitStack = 0x201D, // GameCharAgent.SplitInventoryItem
			CancelBuff = 0x201E, // right-click-cancel a buff; body = [u32 buffId][u32 charId]
			StackInventoryItemIfPossible = 0x201F, // (handled as "SpawnCheck" historically, after the C++ Faolan docs p.160-161; only logged)
			UpdateQuestMarkerNPC = 0x2020, // ALSO the world-entry spawn-poll trigger we answer with 0x2008 (x5631)
			RespawnMe = 0x2024, // the player's "release/resurrect" click; body = [u32 token] (corpse/request id)
			CloseShop = 0x2032, // vendor window closed; body = [shop ID32]
			BuyFromShop = 0x2033, // GameCharAgent.BuyItem; body = [shop ID32][u32 pos]
			SellToShop = 0x2034, // GameCharAgent.SellItem; body = [shop ID32][u32 container][u32 charId][u32 slot]
			UndoLastSoldItem = 0x2035, // body empty (single-step undo of the last sale)
			TryLootAll = 0x2043, // loot-window take; body = [loot typetag][bag handle]
			CloseLootBag = 0x2044, // client closes the loot window; body = [loot typetag][bag handle]
			TryLootCash = 0x2045, // loot the bag's coins; body = [loot typetag][bag handle]; fires on every loot (1:1 with TryLootAll)
			PerformSpecialAction = 0x205A,
			DoSkillSellback = 0x205C,
			PlayStockphraseAsSubtitle = 0x205E,
			CinematicTransition = 0x205F, // heavy in the create flow (the post-205F follow-up gate)
			MoveTradeItemInternal = 0x206A, // (was "request change map(?)" - the old guess predates the table)
			RequestShortcutBar = 0x206D, // sent at login
			AddShortcut = 0x206E, // drag an item/ability onto the hotbar; body = [u32 type][u32 slot][u32 targetId][u32 0]
			UpdateShortcut = 0x206F, // body = [u32 slot][u32 a][u32 b] (semantics not decoded; observed a=0/1, b=0x68)
			RemoveShortcut = 0x2071, // body = [u32 slot]
			GetInstanceListForCurrentPlayfield = 0x207B, // the 'Select instance' button
			SetHideInspectOption = 0x207E,
			LatencyCheck = 0x2085 // GameCharAgent.Ping (client 0x2085 -> server 0x2082)
		}

		// Server->client opcodes on the GameServer connection. These VALUES are capture-replayed (retail
		// byte-for-byte) and in-client verified; the names below are behavior nicknames. The true s2c method
		// identities are now recovered from AgeOfConan.exe (docs/aoc-rpc-method-opcodes.md, Captures/ghidra/):
		// the live build inserted extra (mostly no-arg) dispatch methods, which is why the values don't line up
		// with the string-extracted catalog order. Ground-truth identities for the nicknames below:
		//   MoneyUpdate 0x2005   = GameCharInterface.DispatchUpdateCash          (exact match)
		//   BindLocation 0x2002  = GameCharInterface.DispatchUpdateBindLocation  (exact match)
		//   ShopData 0x2033      = GameCharInterface.DispatchShopData            (exact match)
		//   ItemSoldToShop 0x2035= GameCharInterface.DispatchItemSoldToShop      (exact match)
		//   LatencyCheckResponse 0x2082 = GameCharInterface.DispatchPong         (the REAL per-char pong)
		//   Pong 0x2009          = GameInterface.DispatchGameServerStats (reply to c2s GameAgent.UpdateGameServerStats
		//                          0x2005; the "ping/pong" pair is really the game-server-stats keepalive)
		//   SpawnCheck 0x2008    = GameCharInterface.DispatchSetQuestMarker      (per-char, not GameInterface)
		//   ReadyForPlayScreen 0x2024 = GameCharInterface.DispatchValidResurrectionSpots
		//   ReportDimensionId 0x200b = GameInterface.DispatchReportDimesionID; ReportServerId 0x200c = ...DispatchReportServerID
		//   AckAuthentication 0x2001 = GameInterface/UniverseInterface.DispatchAckAuthenticate
		// Keep the values; do NOT rename (they drive capture-replayed sends). 0x2020 s2c = DispatchAddItem (note for
		// the inventory-delivery work - there IS a per-item add packet, separate from the snapshot).
		public enum GameServerRespondOpcodes : ushort
		{
			// Reply to GameCharAgent.RequestBindLocation: [u32 playfieldId] = the character's bind/home playfield
			// (the Totem of Origin destination). Verified 1:1 request/reply across 13 captures.
			BindLocation = 0x2002,
			// Vendor window content: [shop ID32 (type 0xC75B)][vendor NPC ID32][protobuf shop content]
			// (= GameCharInterface.DispatchShopData by content; the value collides with the c2s BuyItem).
			ShopData = 0x2033,
			// Confirms a SellItem: [u32 container][u32 charId][u32 slot] - the client clears the bag slot.
			ItemSoldToShop = 0x2035,
			// Wallet update: [u64 balanceTin][u32 reason] (reason 3 = gain, 0 = spend/sync, 2 seen once).
			// Decoded from the sell/loot captures: values grow +2 per beach coin loot, jump on quest rewards,
			// DECREASE on a purchase with reason 0, and persist across sessions. The same opcode value carries
			// the TradepostInterface search results (different class/size); the money body is always 12 bytes.
			MoneyUpdate = 0x2005,
			Pong = 0x2009,
			ReportDimensionId = 0x200b,
			ReportServerId = 0x200c,
			AckAuthentication = 0x2001,
			ReadyForPlayScreen = 0x2024,
			SpawnCheck = 0x2008,
			LatencyCheckResponse = 0x2082
		}

		// --- Full client->server method catalogs for the GameServer connection's RPC classes, generated from
		// --- docs/aoc-client-rpc-methods.json (opcode = 0x2000 + table position). Most are not handled yet;
		// --- they are here so future packets can be identified and routed by (class id, opcode).

		// GameAgent (caller GameInterface) - the connection-level service: auth, ping, the PassBlob IIR carrier.
		public enum GameAgentOpcodes : ushort
		{
			PassBlob = 0x2000,
			Ping = 0x2001,
			ChatVicinity = 0x2002,
			Authenticate2 = 0x2003,
			GetStats = 0x2004,
			UpdateGameServerStats = 0x2005,
			DataSink = 0x2006,
			ClientDisconnected = 0x2007,
			SaveFacebookToken = 0x2008,
			RecruitFacebookFriends = 0x2009,
			UpdateFacebookFriends = 0x200A
		}

		// GameCharAgentProject (caller GameCharInterfaceProject) - per-character "project" extras.
		public enum GameCharAgentProjectOpcodes : ushort
		{
			RequestAggroStatus = 0x2000,
			GetProcDataForID = 0x2001,
			AcceptApprenticeTeacher = 0x2002,
			DeclineApprenticeTeacher = 0x2003,
			RequestApprenticeStudent = 0x2004,
			LeaveApprenticeMode = 0x2005,
			RequestHitAndMitigationTables = 0x2006,
			StartOfflineTrainingSystem = 0x2007,
			ResetOfflineTrainingSystem = 0x2008,
			RefreshEquippedAbilities = 0x2009,
			EnableRangerTracking = 0x200A,
			ToggleVanityArmorFilter = 0x200B,
			SetSprintInputState = 0x200C
		}

		// GameCharAgent (caller GameCharInterface) - the main per-character world service (guild sync, loot,
		// quests, trade, shop, shortcuts, instances, minigames).
		public enum GameCharAgentOpcodes : ushort
		{
			RequestAggroStatus = 0x2000,
			RequestCharName = 0x2001,
			RequestBindLocation = 0x2002,
			SetTutorialStats = 0x2003,
			UpdateCharGuildMemberInfo = 0x2004,
			GuildInfoUpdate = 0x2005,
			JoinedGuild = 0x2006,
			LeftGuild = 0x2007,
			LeaveTeam = 0x2008,
			KickFromTeam = 0x2009,
			CreatedGuild = 0x200A,
			AddGuildFeat = 0x200B,
			RemoveGuildFeat = 0x200C,
			GuildGMPromote = 0x200D,
			GuildGMDemote = 0x200E,
			GuildTaxChanged = 0x200F,
			GuildGoverningFormChanged = 0x2010,
			GuildTransferMoney = 0x2011,
			GuildWithdrawMoney = 0x2012,
			GuildDisbanded = 0x2013,
			GuildBookedCity = 0x2014,
			GuildCityOwnershipChange = 0x2015,
			GuildChangeRank = 0x2016,
			GuildUpdateGuildBank = 0x2017,
			SendGuildInfoToObservers = 0x2018,
			GuildRankingsUpdate = 0x2019,
			ValidateGuildBadges = 0x201A,
			OpenGuildBank = 0x201B,
			CloseBank = 0x201C,
			SplitInventoryItem = 0x201D,
			CancelBuff = 0x201E,
			StackInventoryItemIfPossible = 0x201F,
			UpdateQuestMarkerNPC = 0x2020,
			MagicForgetSpell = 0x2021,
			ResurrectMe = 0x2022,
			CancelResurrectionRequest = 0x2023,
			RespawnMe = 0x2024,
			SignUpForWaveRespawn = 0x2025,
			UpdateWaveRespawn = 0x2026,
			CancelWaveRespawn = 0x2027,
			RequestWaveRespawnTimerUpdate = 0x2028,
			RespawnMeHere = 0x2029,
			GMRequestSpellData = 0x202A,
			RMGetQuestFromQuestgiver = 0x202B,
			GMRequestFeatData = 0x202C,
			GMRequestFeatClassData = 0x202D,
			AdjustSkill = 0x202E,
			RequestPCDebugData = 0x202F,
			GetLootDataForID = 0x2030,
			SimulateLootForID = 0x2031,
			CloseShop = 0x2032,
			BuyItem = 0x2033,
			SellItem = 0x2034,
			UndoLastSoldItem = 0x2035,
			FormationSetData = 0x2036,
			FormationClaimPosition = 0x2037,
			FormationClear = 0x2038,
			FormationLeave = 0x2039,
			FormationUpdatePosRot = 0x203A,
			ScryMessageBoxAnswer = 0x203B,
			ScryOptionsBoxAnswer = 0x203C,
			ExitScenarioResults = 0x203D,
			FeatUntrainAnswer = 0x203E,
			CraftUntrain = 0x203F,
			PlantCTFFlags = 0x2040,
			UntrainFeat = 0x2041,
			TryOpenLootBag = 0x2042,
			TryLootAll = 0x2043,
			CloseLootBag = 0x2044,
			TryLootCash = 0x2045,
			TryLootItem = 0x2046,
			PassNeedGreedForItem = 0x2047,
			DoNeedGreedRollForItem = 0x2048,
			LootLockedItem = 0x2049,
			SlaveLooterAnswer = 0x204A,
			MasterLooterOffer = 0x204B,
			MasterLooterCancelOffer = 0x204C,
			SetPassOnLootLevel = 0x204D,
			UnbindItem = 0x204E,
			ShareQuest = 0x204F,
			ShareQuestDeclined = 0x2050,
			ShareQuestAccepted = 0x2051,
			ShareQuestBusy = 0x2052,
			RequestNextQuestPendingReward = 0x2053,
			RequestTeamQuestList = 0x2054,
			AddQuestList = 0x2055,
			AcceptNextAwaitingQuest = 0x2056,
			QuestManagerMsg = 0x2057,
			StartQuickTeleport = 0x2058,
			DoPaintballQuery = 0x2059,
			PerformSpecialAction = 0x205A,
			QueuePetCommand = 0x205B,
			DoSkillSellback = 0x205C,
			PetSetGroundTargetPos = 0x205D,
			PlayStockphraseAsSubtitle = 0x205E,
			CinematicTransition = 0x205F,
			RequestACGTeleport = 0x2060,
			RequestClearOwnACG = 0x2061,
			RequestDropTeamBind = 0x2062,
			DebugSetOceanData = 0x2063,
			RequestTrade = 0x2064,
			StartTrade = 0x2065,
			DeclineTrade = 0x2066,
			SetTradeCash = 0x2067,
			AddTradeItem = 0x2068,
			RemoveTradeItem = 0x2069,
			MoveTradeItemInternal = 0x206A,
			AcceptTrade = 0x206B,
			AbortTrade = 0x206C,
			RequestShortcutBar = 0x206D,
			AddShortcut = 0x206E,
			UpdateShortcut = 0x206F,
			MoveShortcut = 0x2070,
			RemoveShortcut = 0x2071,
			StartMinigame = 0x2072,
			NewMinigamePlayer = 0x2073,
			DropMinigamePlayer = 0x2074,
			ResendMinigameInfoToPlayer = 0x2075,
			FlagAsCoward = 0x2076,
			ForceStopMinigame = 0x2077,
			ForceSetMinigameTime = 0x2078,
			PrepareMinigameTime = 0x2079,
			PrepareToMinigameTime = 0x207A,
			GetInstanceListForCurrentPlayfield = 0x207B,
			ShiftInstanceForCurrentPlayfield = 0x207C,
			EnableHeroicMode = 0x207D,
			SetHideInspectOption = 0x207E,
			SetPlayfieldInstanceList = 0x207F,
			InstanceWillResetOnShutdown = 0x2080,
			InstanceHasBeenLocked = 0x2081,
			InstanceHasBeenUnLocked = 0x2082,
			RequestTeamMemberPosList = 0x2083,
			TradepostSearchResult = 0x2084,
			Ping = 0x2085,
			ShiftTeamInstance = 0x2086,
			SubscriptionFlagsUpdated = 0x2087,
			PlayerServicesUpdated = 0x2088,
			PassiveAbilityEquip = 0x2089,
			PassiveAbilityUnequip = 0x208A,
			ExitPassiveMode = 0x208B,
			EnterPassiveMode = 0x208C,
			RequestMassivePvPScoreData = 0x208D,
			RequestScorePlayerNames = 0x208E,
			SetVulnerabilityWindowStartTime = 0x208F,
			RequestRecipeInfo = 0x2090,
			RequestTeamInvite = 0x2091,
			RequestNewJoinHasHostileInFriends = 0x2092,
			RequestTeamInviteWithInviterFactionPoints = 0x2093,
			RequestItemSalesData = 0x2094,
			RequestItemSaleLimitedItemList = 0x2095,
			RequestItemSaleFavoriteData = 0x2096,
			SetFakeBillingConnection = 0x2097,
			AddFavoriteItem = 0x2098,
			RemoveFavoriteItem = 0x2099,
			BuyItemSalesItem = 0x209A,
			CSBuyItemReply = 0x209B,
			CSRollbackResponse = 0x209C,
			ExecuteChatCommand = 0x209D,
			ReplyUpdatedDimensionDB = 0x209E,
			RequestUseTeleportSpell = 0x209F,
			UnlockFromPlayfield = 0x20A0,
			UpdateMailBank = 0x20A1,
			SetSelectedTag = 0x20A2,
			SetSelectedBadgeTag = 0x20A3,
			KickPlayerFromGroupPlayfield = 0x20A4,
			WorldStatChanged = 0x20A5,
			UpdateNoXPGainStat = 0x20A6,
			SendReport = 0x20A7,
			RunLua = 0x20A8
		}

		// The 0x2000 sub-opcodes the GameServer HANDLES (client->server). These are the same IIR ids as
		// GameServer0X2000RespondsOpcodes (the full catalog); kept as a separate handler-facing enum with the
		// descriptive names the handler switch reads by, with the real client IIR type noted per line. The three
		// "LegacyCreateEcho*" values are NOT IIR ids - they are client instance ids the create-editor client echoes
		// back as the sub-opcode (the normal path matches the live instance dynamically; these are static fallbacks).
		public enum GameServer0X2000Opcodes : uint
		{
			Interact = 0x642CD3D6, // = BehaviorControlCenterIIR_c: generic action channel (world-object interaction, spells, right-click, inventory/equipment)
			CloseGame = 0x6B6FD368, // = CharacterActionIIR_t
			ReadyForPlayScreen = 0x864cfef8, // = CharInPlayIIR_t
			Move = 0x4517aa81, // = CharDCMoveIIR_t
			ScryScriptIIR = 0xa36d3b74, // = ScryScriptIIR_c: cutscene/scry scripts + char-editor callbacks (IntroDone, ChangeSex, TheNameIs, ClientLoadingDone, ...)
			StatIIR = 0xf98e10b3, // = StatIIR_t
			SelectDeselect = 0x0D155695, // = LookAtIIR_t
			KnubotAnswer = 0x4D7D9123, // = KnubotAnswerIIR_c: the player picked a dialogue option (NPC chat); advances the conversation one stage
			DropItem = 0x72491DE9, // = DeleteItemIIR_t: client-side inventory action; no matching bag-content response packet is decoded yet
			MoveItemInInventory = 0x2A8A1B49, // = MoveInventoryItemIIR_t: client-side slot move action
			DeleteQuest = 0x97E02314, // = QuestIIR_c
			InventoryClaimsButton = 0x06EC1255, // = CoreClaimIIR: tiny post-ready/request-side packet, not actual bag contents
			LegacyCreateEchoInstanceStrie = 0x02165EFF, // create-flow client instance echo (Strie, older retail create capture) - NOT an IIR id
			LegacyCreateEchoInstanceRetail = 0x021662C8, // create-flow client instance echo (account-stricken998 create-no-worldenter) - NOT an IIR id
			LegacyConanHimselfInstance = 0x1101B4F6 // ConanHimself manual char instance - NOT an IIR id
		}

		// Every 32-bit sub-opcode carried inside a 0x2000 packet is an "IIR" (Incremental Information Replication)
		// message id = the BKDR-31 hash of the client's IIR C++ type name (h = h*31 + c; == Java String.hashCode),
		// recovered by RE'ing AgeOfConan.exe (docs/archive/opcode-re-plan.md + docs/aoc-opcode-ids.md). Members are named
		// after the real client type (with the _t/_c template suffix dropped); the comment gives the exact type and
		// the occurrence count across all captures. This is the full catalog of every IIR id observed on the wire
		// (server->client AND client->server - the id is the message type, direction-independent), even ones the
		// server doesn't emit yet. The few descriptive aliases the server code uses are at the bottom.
		public enum GameServer0X2000RespondsOpcodes : uint
		{
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
			DeleteItemIIR = 0x72491DE9, // DeleteItemIIR_t - "DropItem"
			DestructibleFullUpdateIIR = 0x6BD56736, // DestructibleFullUpdateIIR_c  (x189)
			DoorFullUpdateIIR = 0x33A56FB0, // DoorFullUpdateIIR_c  (x89) - world door/object full update
			EnergyIIR = 0x40749B8A, // EnergyIIR  (x1039)
			FeedbackIIR = 0xBA1FD222, // FeedbackIIR_t  (x5)
			FormatFeedbackIIR = 0x8D5A8B6B, // FormatFeedbackIIR_t  (x68)
			FullPlayerCharacterIIR = 0x737A6DF9, // FullPlayerCharacterIIR  (x33) - the whole player character (inc. inventory); see carve-out below
			GameActionListIIR = 0xF508F4C1, // GameActionListIIR_t  (x366)
			GameTimeIIR = 0x4F57DC08, // GameTimeIIR_t  (x33) - "SetDayTime"
			GuildUpdateIIR = 0x2550C99A, // GuildUpdateIIR_c  (x3)
			HateListUpdateIIR = 0x8A01E5A1, // HateListUpdateIIR  (x61)
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
			PetIIR = 0x1F4CB0F7, // PetIIR_c  (x6)
			PlayfieldFIIR = 0x5D85BFC7, // PlayfieldFIIR_t  (x33) - "MapChange"
			QuestFullUpdateIIR = 0x63268258, // QuestFullUpdateIIR  (x28)
			QuestGoalIIR = 0x0774C8C1, // QuestGoalIIR_c  (x27)
			QuestIIR = 0x97E02314, // QuestIIR_c  (x34) - "DeleteQuest"
			QuestJournalIIR = 0x79F8C7A1, // QuestJournalIIR_c  (x17)
			RegenIIR = 0x28D0AC99, // RegenIIR_c  (x554)
			ScryScriptIIR = 0xA36D3B74, // ScryScriptIIR_c  (x390) - cutscene/scry scripts + char-editor callbacks (IntroDone, TheNameIs, ...)
			SetMorphValueIIR = 0xBADF5A4B, // SetMorphValueIIR_c  (x101) - character-creator morph ack
			SetNameIIR = 0xADCE0CDA, // SetNameIIR_t  (x4) - name commit in the create flow
			SetPosIIR = 0x04B80B24, // SetPosIIR_c  (x29)
			SimpleCharFullUpdateIIR = 0xFA015807, // SimpleCharFullUpdateIIR_t  (x3407) - entity spawn ("Spawn")
			SimpleCharKilledByUpdateIIR = 0xB066AFC2, // SimpleCharKilledByUpdateIIR_t  (x2) - kill/death
			SimpleDynelFullUpdateIIR = 0x66AEDD50, // SimpleDynelFullUpdateIIR_c  (x1575) - world object spawn ("SpawnObject")
			SimpleDynelVisualChangeIIR = 0xE7F4F8F8, // SimpleDynelVisualChangeIIR_c  (x548)
			SpellBookIIR = 0x6BA10936, // SpellBookIIR_t  (x1)
			StatDamageIIR = 0xE9F24184, // StatDamageIIR_t  (x450)
			StatIIR = 0xF98E10B3, // StatIIR_t  (x8167)
			StatUpdateIIR = 0x96B8DC59, // StatUpdateIIR_c  (x1595) - entity vitals/stat update (combat HP/Stamina/Mana); see docs/archive/quests-and-combat.md
			StopIIR = 0xD751EBB4, // StopIIR_c  (x2097) - stop moving
			TemplateActionIIR = 0x6C300937, // TemplateActionIIR_t  (x7)
			ThreatLevelIIR = 0x1745EF6C, // ThreatLevelIIR_c  (x162)
			TombStoneFullUpdateIIR = 0x0D80E7C9, // TombStoneFullUpdateIIR_c  (x6)
			UpdateInventoryIIR = 0xA74D2814, // UpdateInventoryIIR_t  (x30)
			UpdateSimpleDynelIIR = 0x30536411, // UpdateSimpleDynelIIR_c  (x30)
			N3TeleportIIR = 0x5A32F0D7, // n3TeleportIIR_t  (x27) - teleport
			N3ToClientFadeIIR = 0x66180B20, // n3ToClientFadeIIR_t  (x87) - screen fade
			N3ToClientQuitIIR = 0x04B82B2D, // n3ToClientQuitIIR_t  (x3249)

			// --- descriptive aliases used by the server code (same value as the IIR member above) ---
			MapChange = PlayfieldFIIR,
			Spawn = SimpleCharFullUpdateIIR,
			SpawnObject = SimpleDynelFullUpdateIIR,
			SetDayTime = GameTimeIIR,
			ApplySpell = MagicBuffIIR

			// FullPlayerCharacterIIR (0x737A6DF9) carve-out - the player inventory/bag serialization:
			// - after a 34-byte preamble, the body contains nested container-style blocks linked by field 6:
			//   101 -> f6 -> 104 -> f6 -> 112 -> f6 -> 113 -> f7=0
			// - each block currently looks like { f1=containerRef, f2=?, f3=?, repeated f4=itemEntry, f6=nextBlock? }
			// - confirmed blocks currently seen with the same shared owner/root handle: 101, 104, 112, 113
			// - block 101 carries 21 indices that exactly match the known Spawn appearance/customization ids
			//   (1,2,3,5,6,7,9,10,11,12,14,15,16,18,20,21,1002,1007,1011,1016,1018)
			// - block 104 is the strongest bag section: indices 0..29 line up with observed bag-slot moves,
			//   and a nested tiny message shaped like { f1=26, f2=count } reproduces stack counts such as
			//   20,78,100,34,96,83,82,56,4,1,5,3 from the earlier bag notes
			// - by count profile alone, 112 now looks resource-like (many non-1 stacks) and 113 looks quest-like
			//   (mostly count=1); this tentatively matches the CharacterEquipment model's Bag/Resources/Quest split
			// - the tiny message { f1=23, f2=value } now looks like a local inventory-record id rather than an
			//   item template id: across 104/112/113 it forms one perfect contiguous range 168534..168658
			//   and is the only tiny submessage that is present on every parsed entry in this sample
			// - { f1=26, f2=value } behaves like an optional stack-count field; when omitted, the entry still
			//   behaves like count=1, which matches the mostly-singleton quest-style block 113
			// - { f1=0, f2=value } behaves like optional instance state/flags rather than item type metadata
			//   because the same 4-character tail code can reappear with different count and even different
			//   presence/absence of this field (e.g. repeated codes in 104/112)
			// - each item entry ends with a stable 83-byte tail shaped like:
			//   { f1=len20, f2=len20, f3=len20, f4=fixed32, f5=fixed32, f7=varint, f8=fixed32 }
			//   where f1/f2/f3 are byte-identical, f4 always equals f5 in current samples, and f8 behaves like
			//   a short 4-character archetype/type code because it uniquely determines the whole tail signature
			// - rare optional tiny submessages also exist (currently seen with leading kinds 265, 8, and 1290),
			//   which likely carry special-case item properties rather than core bag-slot state
			// - no server->client inventory packet follows later MoveItemInInventory requests, so the client
			//   appears to learn the initial bag state here rather than via a small incremental sync packet
		}

		// PlayerAgent opcodes do NOT map onto the recovered PlayerAgent/PlayerInterface tables (25 methods, max
		// 0x2018) - the observed wire values run 0x2083..0x20F5, so the live build's table is much larger than
		// the recovered one in both directions. These stay behavior-derived names.
		public enum PlayerAgentOpcodes : ushort
		{
			RequestAuth = 0x2083,
			RequestSmallCharList = 0x20c1, //0xDAD1D206,
			RequestCharRealmData = 0x208e, //0xA3E1FE0D
			Ox20A6 = 0x20a6,
			EnterWorld = 0x20b6, //0xDD85FB0E
			Ox20D6 = 0x20d6, //0xDD9EC209
			CreateCharacter = 0x20de,
			Ox20EC = 0x20EC,
			DeleteCharacter = 0x209d // 0x86979E0C:
		}

		public enum PlayerAgentResponseOpcodes : ushort
		{
			Ox20B9 = 0x20b9,
			InitAuth = 0x2091,
			SmallCharList = 0x20a5,
			SendCharacterList = 0x20ef,
			SendRealmList = 0x20cc,
			SendCharacterRealmData = 0x208c,

			//SendCsPlayerAgent = 0x00001A07,
			//SendAgentServer = 0x00001A07,
			//SendGameServer = 0x00001A07,
			//SendLast = 0x00001A07,
			Ox1A07 = 0x1A07
		}

		// UniverseAgent client->server: matches the recovered table exactly (3 methods).
		public enum UniverseAgentOpcodes : ushort
		{
			InitiateAuthentication = 0x2000,
			AnswerChallenge = 0x2001,
			ClientDisconnected = 0x2002
		}

		// UniverseInterface server->client: the recovered table aligns here (0x2000 is the DH challenge,
		// 0x2005 sets the region state - both match observed behavior), unlike the bigger drifting interfaces.
		public enum UniverseAgentResponseOpcodes : ushort
		{
			DispatchChallenge = 0x2000, // (was "InitiateAuthentication" - it carries the DH challenge reply)
			DispatchAckAuthenticate = 0x2001, // carries the PlayerAgent realm handoff (was "SendPlayerAgentRealm")
			DispatchLoginProblem = 0x2002,
			DispatchSetUpdateManager = 0x2003,
			DispatchSetUniverseManagerError = 0x2004,
			DispatchSetRegionState = 0x2005 // (was "SetRegion")
		}
	}
}
