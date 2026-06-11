namespace Faolan.Core.Network.Packets
{
	namespace Opcodes
	{
		public enum AgentServerOpcodes : ushort
		{
			Hello = 0x0000,
			ChannelChat = 0x0041, // client posts a message to a chat channel: [byte type][u32 channelId][string][0000]
			ChatReady = 0x0064 // small chat-init handshake the client sends after the channel subscriptions (000102)
		}

		public enum AgentServerRespondsOpcodes : ushort
		{
			CharacterChat = 0x0022,
			SystemMessage = 0x0023,
			ChannelChat = 0x0041, // a channel message relayed to a client: [byte type][u32 channelId][u32 senderId][string][0000]
			Ox0014 = 0x0014, // sender id + name; also sent before a ChannelChat so the client can show the speaker
			Ox003C = 0x003c,
			Ox0064 = 0x0064 // echo of the client's chat-init handshake
		}

		public enum CsPlayerAgentOpcodes : ushort
		{
			Authenticate = 0x2000,
			Ox2019 = 0x2019,
			Ox2072 = 0x2072,
			Ox202A = 0x202a,
			Ox203B = 0x203b,
			Ox203C = 0x203c,
			Ox2040 = 0x2040,
			Ox2041 = 0x2041,
			Ox201E = 0x201E,
			Ox2077 = 0x2077,
			Ox202F = 0x202F,
			Ox2038 = 0x2038,
			Ox20C5 = 0x20C5,
			Ox20D0 = 0x20D0
		}

		public enum CsPlayerAgentResponseOpcodes : ushort
		{
			Authenticate = 0x2000,
			Ox2038 = 0x2038,
			Ox20A5 = 0x20A5,
			Ox20C5 = 0x20C5,
			Ox20D0 = 0x20D0
		}

		public enum GameServerOpcodes : ushort
		{
			Hello = 0x2003,
			Ping = 0x2005,
			SpawnCheck = 0x201F,
			Ox2000 = 0x2000,
			Ox206A = 0x206a,
			Ox207B = 0x207b,
			Ox205A = 0x205a,
			SplitStack = 0x201D,
			ChatCommand = 0x2002,
			Ox200C = 0x200c,
			Ox205C = 0x205c,
			VanityToggle = 0x200B,

			Ox205E = 0x205E,
			Ox205F = 0x205F,
			Ox206D = 0x206D,
			LatencyCheck = 0x2085,
			Ox207E = 0x207E,
			Ox2071 = 0x2071,
			Ox2020 = 0x2020,
			MoveItemToBar = 0x206E
		}

		public enum GameServerRespondOpcodes : ushort
		{
			Pong = 0x2009,
			ReportDimensionId = 0x200b,
			ReportServerId = 0x200c,
			AckAuthentication = 0x2001,
			ReadyForPlayScreen = 0x2024,
			SpawnCheck = 0x2008,
			LatencyCheckResponse = 0x2082
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
		// recovered by RE'ing AgeOfConan.exe (docs/opcode-re-plan.md + docs/aoc-opcode-ids.md). Members are named
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
			StatUpdateIIR = 0x96B8DC59, // StatUpdateIIR_c  (x1595) - entity vitals/stat update (combat HP/Stamina/Mana); see docs/quests-and-combat.md
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

		public enum UniverseAgentOpcodes : ushort
		{
			InitiateAuthentication = 0x2000,
			AnswerChallenge = 0x2001
		}

		public enum UniverseAgentResponseOpcodes : ushort
		{
			InitiateAuthentication = 0x2000,
			SendPlayerAgentRealm = 0x2001,
			SetRegion = 0x2005
		}
	}
}
