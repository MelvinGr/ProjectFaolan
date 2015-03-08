using AgentServer;
using CSPlayerAgent;
using GameServer;
using LibFaolan.Database;
using PlayerAgent;
using UniverseAgent;

namespace ProjectFaolan
{
    public static class Statics
    {
        public static UniverseAgentListener UniverseAgent;
        public static PlayerAgentListener PlayerAgent;
        public static GameServerListener GameServer;
        public static CsPlayerAgentListener CsplayerAgent;
        public static AgentServerListener AgentServer;
        public static IDatabase Database;
    }
}