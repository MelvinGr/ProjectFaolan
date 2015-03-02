namespace GameServer
{
    partial class GameServerListener
    {
        private enum Opcodes : ushort
        {
            Hello = 0x2003,
            Ping = 0x2005,
            SpawnCheck = 0x201F,
            GCPing = 0x2082,
            Ox2000 = 0x2000,
            Ox206A = 0x206a,
            Ox207B = 0x207b,
            Ox205A = 0x205a,
            ManualRemoveBuff = 0x201D
        };

        private enum SendOpcodes : ushort
        {
            Pong = 0x2009
        };

        private enum DataOpcodes : uint
        {
            InteractWithObject = 0x642CD3D6,
            CloseGame = 0x6B6FD368,
            ReadyForPlayScreen = 0x864cfef8,
            Move = 0x4517aa81,
            Ox36d3b74 = 0x36d3b74,
            Oxf98e10b3 = 0xf98e10b3,
            AchievementNPC = 0x0D155695
        }

        private enum MovingTypes : ushort
        {
            Run = 0x010a, // not shure
            Walk = 0x010e, // not shure
            Ox011a = 0x011a,
            Ox011e = 0x011e,
            Ox001e = 0x001e,
            Ox001a = 0x001a
        }
    }
}