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
            Ox205A = 0x205a
        };

        private enum SendOpcodes : ushort
        {
            Pong = 0x2009
        };
    }
}