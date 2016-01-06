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
            ChatCommand = 0x2002
        }

        private enum SendOpcodes : ushort
        {
            Pong = 0x2009
        }

        private enum DataOpcodes : uint
        {
            InteractWithObject = 0x642CD3D6,
            CloseGame = 0x6B6FD368,
            ReadyForPlayScreen = 0x864cfef8,
            Move = 0x4517aa81,
            Ox36D3B74 = 0x36d3b74,
            Oxf98E10B3 = 0xf98e10b3,
            SelectDeselect = 0x0D155695
        }

        private enum Ox2000RespondsOpcodes : uint
        {
            MapChange = 0x5D85BFC7,
            Ox33A56FB0 = 0x33A56FB0,
            SpawnObject = 0x66AEDD50,
            Spawn = 0xFA015807,
            SetDayTime = 0x4F57DC08,
            Ox642CD3D6 = 0x642CD3D6,
            Ox96C46740 = 0x96C46740,
            ApplySpell = 0x6B87E316,
            OxF98E10B3 = 0xF98E10B3,
            SitOnMount = 0x96B8DC59, // Does not apply buff
            Ox737A6DF9 = 0x737A6DF9
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