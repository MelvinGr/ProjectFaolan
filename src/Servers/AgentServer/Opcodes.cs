namespace AgentServer
{
    partial class AgentServerListener
    {
        private enum Opcodes : ushort
        {
            Hello = 0x0000
        }

        private enum RespondsOpcodes : ushort
        {
            CharacterChat = 0x0022,
            SystemMessage = 0x0023,
            Ox0014 = 0x0014,
            Ox003c = 0x003c
        }
    }
}