namespace GameServer
{
    partial class GameServerListener
    {
        private enum Opcodes : ushort
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
            VanityToggle = 0x200B
        }

        private enum RespondOpcodes : ushort
        {
            Pong = 0x2009,
            ReportDimesionId = 0x200b,
            ReportServerId = 0x200c,
            AckAuthentication = 0x2001,
            ReadyForPlayScreen = 0x2024
        }

        private enum DataOpcodes : uint
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

        private enum Ox2000RespondsOpcodes : uint
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

        private enum CharacterEquipSlotsIDs : uint
        {
            Neck = 1,
            Head = 2,
            Chest = 5,
            MainWeapon = 6,
            Belt = 7,
            LeftRing = 9,
            RightRing = 10,
            Cape = 11,
            Shoulder = 12,
            Boots = 14,
            Hands = 15,
            Legs = 16,
            Wrist = 18,
            Shield = 0xff, // unk
            SecondaryWeapon = 0xff, // unk
            SecondaryShield = 0xff, // unk

            // prob. all the same + 1000 still have to check
            VanityHead = 1002,
            VanityBelt = 1007,
            VanityCape = 1011,
            VanityBoots = 1014,
            VanityWrist = 1018
        }

        private enum ActionTypes : uint
        {
            CastSpell = 0x4,
            MoveEquipedToInventory = 0x9,
            SwitchWeaponsButton = 0x2e
        }

        private enum MovingTypes : ushort
        {
            MountedRun = 0x010a,
            Walk = 0x010e,
            Jump = 0x0156,
            WalkJump = 0x0152,
            MountedJump = 0x0142,
            Falling = 0x0116,
            Ox011A = 0x011a,
            Ox011E = 0x011e,
            Ox001E = 0x001e,
            Ox001A = 0x001a
        }
    }
}