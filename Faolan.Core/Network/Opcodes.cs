namespace Faolan.Core.Network
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
			Ox203C = 0x203c
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
			ManualRemoveBuff = 0x201D,
			ChatCommand = 0x2002,
			Ox200C = 0x200c,
			Ox205C = 0x205c,
			VanityToggle = 0x200B,

			Ox205E = 0x205E,
			Ox205F = 0x205F,
			Ox206D = 0x206D,
			Ox2085 = 0x2085,
			Ox207E = 0x207E
		}

		public enum GameServerRespondOpcodes : ushort
		{
			Pong = 0x2009,
			ReportDimensionId = 0x200b,
			ReportServerId = 0x200c,
			AckAuthentication = 0x2001,
			ReadyForPlayScreen = 0x2024,
			SpawnCheck = 0x2008,
			GcPing = 0x207D
		}

		public enum GameServerDataOpcodes : uint
		{
			Interact = 0x642CD3D6,
			CloseGame = 0x6B6FD368,
			ReadyForPlayScreen = 0x864cfef8,
			Move = 0x4517aa81,
			Ox36D3B74 = 0x36d3b74,
			Oxf98E10B3 = 0xf98e10b3,
			SelectDeselect = 0x0D155695,
			DropItem = 0x72491DE9,
			MoveItemInInventory = 0x2A8A1B49,
			DeleteQuest = 0x97E02314,
			InventoryClaimsButton = 0x06EC1255
		}

		public enum GameServerOx2000RespondsOpcodes : uint
		{
			MapChange = 0x5D85BFC7,
			Ox33A56Fb0 = 0x33A56FB0,
			SpawnObject = 0x66AEDD50,
			Spawn = 0xFA015807,
			SetDayTime = 0x4F57DC08,
			Ox642Cd3D6 = 0x642CD3D6,
			Ox96C46740 = 0x96C46740,
			ApplySpell = 0x6B87E316,
			OxF98E10B3 = 0xF98E10B3,
			SitOnMount = 0x96B8DC59, // Does not apply buff
			Ox737A6Df9 = 0x737A6DF9
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
			CreateCharacter = 0x20b9,
			InitAuth = 0x2091,
			SmallCharList = 0x20a5,
			SendCharacterList = 0x20ef,
			SendRealmList = 0x20cc,
			SendCharacterRealmData = 0x208c,
			SendCsPlayerAgent = 0x1A07,
			SendAgentServer = 0x1A07,
			SendGameServer = 0x1A07,
			SendLast = 0x1A07,
			EnterWorld = 0x20B9
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
