using System.Globalization;
using AgentServer;
using CSPlayerAgent;
using GameServer;
using LibFaolan.Config;
using LibFaolan.Database;
using LibFaolan.DllImport;
using LibFaolan.Other;
using PlayerAgent;
using UniverseAgent;

namespace ProjectFaolan
{
    internal class ProjectFaolan
    {
#if DEBUG
        private const string ConfigPath = "../../other/faolan.ini";
#else
        private const string ConfigPath="faolan.ini";
#endif

        private UniverseAgentListener _universeAgent;
        private PlayerAgentListener _playerAgent;
        private GameServerListener _gameServer;
        private CsPlayerAgentListener _csplayerAgent;
        private AgentServerListener _agentServer;
        private IDatabase _database;

        public ProjectFaolan()
        {
            Console.EnableQuickEditMode();
            //Console.SetConsoleCtrlHandler(ConsoleCtrlHandler, true); // Causes crash??
            Functions.SetDefaultCulture(CultureInfo.InvariantCulture);

            var epoch = 0;
            Other.time(ref epoch);
            Logger.Setup("ProjectFolan_" + epoch + ".log");

            new Logger().Info(Statics.Banner);
        }

        /*private bool ConsoleCtrlHandler(Console.CtrlType eventType)
        {
            _gameServer?.SaveAllCharacterData();
            return false;
        }*/

        public void Start()
        {
            if (Settings.Load(ConfigPath))
                new Logger("Database").Info("Loaded from: '" + ConfigPath + "'");
            else
            {
                new Logger("Database").Info("Failed to load from: '" + ConfigPath + "'");
                return;
            }

            if (Settings.UseMysql)
            {
                _database = new MySqlDatabase(Settings.MySqlAddress, Settings.MySqlPort, Settings.MySqlDatabase,
                    Settings.MySqlUsername, Settings.MySqlPassword, new Logger("Database"));
            }
            else if (Settings.UseSqLite)
                _database = new SqLiteDatabase(Settings.SqLitePath, new Logger("Database"));

            if (!_database.Connect())
                return;

            _universeAgent = new UniverseAgentListener(Settings.UniverseAgentPort, new Logger("UniverseAgent"),
                _database);
            _universeAgent.Start();

            _playerAgent = new PlayerAgentListener(Settings.PlayerAgentPort, new Logger("PlayerAgent"), _database);
            _playerAgent.Start();

            _csplayerAgent = new CsPlayerAgentListener(Settings.CsPlayerAgentPort, new Logger("CsPlayerAgent"),
                _database);
            _csplayerAgent.Start();

            _agentServer = new AgentServerListener(Settings.AgentServerPort, new Logger("AgentServer"), _database);
            _agentServer.Start();

            _gameServer = new GameServerListener(Settings.GameServerPort, new Logger("GameServer"), _database,
                _agentServer);
            _gameServer.Start();
        }
    }
}