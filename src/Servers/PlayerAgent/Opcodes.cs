namespace PlayerAgent
{
    partial class PlayerAgentListener
    {
        private enum Opcodes : ushort
        {
            RequestAuth = 0x2083,
            RequestSmallCharList = 0x20c1, //0xDAD1D206,
            RequestCharRealmData = 0x208e, //0xA3E1FE0D
            Ox20A6 = 0x20a6,
            EnterWorld = 0x20b6, //0xDD85FB0E
            Ox20D6 = 0x20d6, //0xDD9EC209
            CreateCharacter = 0x20de,
            Ox20Ec = 0x20ec,
            DeleteCharacter = 0x209d // 0x86979E0C:
        };
    }
}