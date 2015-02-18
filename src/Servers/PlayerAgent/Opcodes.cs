namespace PlayerAgent
{
    partial class PlayerAgentListener
    {
        private enum Opcodes
        {
            Request_Auth = 0x2083,
            Request_SmallCharList = 0x20c1, //0xDAD1D206,
            Request_Char_RealmData = 0x208e, //0xA3E1FE0D
            Ox20a6 = 0x20a6,
            EnterWorld = 0x20b6, //0xDD85FB0E
            Ox20d6 = 0x20d6, //0xDD9EC209
            CreateCharacter = 0x20de,
            Ox20EC = 0x20ec,
            DeleteCharacter = 0x209d // 0x86979E0C:
        };
    }
}