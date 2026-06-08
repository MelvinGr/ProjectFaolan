namespace Faolan.Core.Network.Packets
{
	namespace Opcodes
	{
		public enum AgentServerOpcodes : ushort
		{
			Hello = 0x0000
		}

		public enum AgentServerRespondsOpcodes : ushort
		{
			CharacterChat = 0x0022,
			SystemMessage = 0x0023,
			Ox0014 = 0x0014,
			Ox003C = 0x003c
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
			GcPing = 0x2082,
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
			GcPing = 0x207D,
			LatencyCheckResponse = 0x2082
		}

		public enum GameServer0X2000Opcodes : uint
		{
			Interact = 0x642CD3D6, // generic action channel: world-object interaction, spells, right-click item, inventory/equipment actions
			CloseGame = 0x6B6FD368,
			ReadyForPlayScreen = 0x864cfef8,
			Move = 0x4517aa81,
			OxA36D3B74 = 0xa36d3b74, // character-editor script callbacks (IntroDone, ChangeSex, TheNameIs, ClientLoadingDone, ...)
			Oxf98E10B3 = 0xf98e10b3,
			Ox02165Eff = 0x02165EFF, // observed repeated client-side create-flow request in the older retail create capture
			Ox021662C8 = 0x021662C8, // observed repeated client-side create-flow request in account-stricken998-character-create-no-worldenter
			SelectDeselect = 0x0D155695,
			DropItem = 0x72491DE9, // client-side inventory action; no matching bag-content response packet is decoded yet
			MoveItemInInventory = 0x2A8A1B49, // client-side slot move action; likely pairs with a separate inventory/container sync family
			DeleteQuest = 0x97E02314,
			InventoryClaimsButton = 0x06EC1255, // observed as a tiny post-ready/request-side packet, not as actual bag contents
			Ox1101B4F6 = 0x1101B4F6
		}

		public enum GameServer0X2000RespondsOpcodes : uint
		{
			MapChange = 0x5D85BFC7,
			Ox33A56Fb0 = 0x33A56FB0, // capture examples look like location/object label style payloads (e.g. City of Tortage)
			SpawnObject = 0x66AEDD50,
			Spawn = 0xFA015807,
			SetDayTime = 0x4F57DC08,
			OxF096251E = 0xF096251E, // container/chest-style packet family in captures

			// stable observations so far:
			// - embeds names like "Chest [N7FU]", "Acheronian Chest", and "object_closed"
			// - payload sizes vary widely (e.g. 540/543/659/778/781), suggesting repeated item/container entries
			// - begins with the same world-object style position block, then a 125-byte metadata block
			//   carrying container name/remoteformat metadata, then a variable nested entry block
			// - the chest samples currently decode as 2, 3, or 4 repeated nested entries (indices 0..n-1)
			// - still looks like remote world-container/chest data, not the player bag snapshot
			Ox642Cd3D6 = 0x642CD3D6,
			Ox96C46740 = 0x96C46740, // float-heavy state block in captures; does not resemble bag or slot inventory data
			ApplySpell = 0x6B87E316,
			OxF98E10B3 = 0xF98E10B3,
			Ox96B8Dc59 = 0x96B8DC59, // entity vitals / stat update: 0000C350 + entityHandle + 00000000 + count(1) + N×(u32 statId,u32 value); carries combat HP/Stamina/Mana (docs/quests-and-combat.md)

			Ox737A6Df9 = 0x737A6DF9 // very large initial state dump in captures
			// strongest current candidate for player inventory/bag serialization
			// stable carve-out so far from the random-actions capture:
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
