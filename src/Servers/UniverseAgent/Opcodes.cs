namespace UniverseAgent
{
    partial class UniverseAgentListener
    {
        private enum Opcodes : ushort
        {
            InitiateAuthentication = 0x2000,
            AnswerChallenge = 0x2001
        }

        private enum RespondseOpcodes : ushort
        {
            InitiateAuthentication = 0x2000,
            SendPlayerAgentRealm = 0x2001,
            SetRegion = 0x2005
        }
    }
}