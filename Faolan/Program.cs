using System;
using System.Globalization;
using Faolan.AgentServer;
using Faolan.Core;
using Faolan.Core.Config;
using Faolan.Core.Database;
using Faolan.CSPlayerAgent;
using Faolan.GameServer;
using Faolan.PlayerAgent;
using Faolan.UniverseAgent;

namespace Faolan
{
    internal class Program
    {
        private static UniverseAgentListener _universeAgent;
        private static PlayerAgentListener _playerAgent;
        private static GameServerListener _gameServer;
        private static CsPlayerAgentListener _csplayerAgent;
        private static AgentServerListener _agentServer;
        private static IDatabase _database;

        /*private bool ConsoleCtrlHandler(Console.CtrlType eventType)
        {
            _gameServer?.SaveAllCharacterData();
            return false;
        }*/

        [STAThread]
        private static void Main(string[] args)
        {
            Arguments.Parse(args);

            ConsoleTools.EnableQuickEditMode();
            //ConsoleTools.SetConsoleCtrlHandler(ConsoleCtrlHandler, true); // Causes crash??
            ConsoleTools.SetDefaultCulture(CultureInfo.InvariantCulture);

            if (Arguments.WriteLogToFile)
                Logger.SetupFile($"logs/ProjectFolan_{Functions.Time()}.log");

            Logger.Get().InfoNoFormat(Statics.Banner);

            if (Settings.Load(Arguments.ConfigPath))
            {
                Logger.Get("Settings").Info($"Loaded from: {Arguments.ConfigPath}");
            }
            else
            {
                Logger.Get("Settings").Info($"Failed to load from: {Arguments.ConfigPath}");
                return;
            }

            if (Settings.UseMysql)
                _database = new MySqlDatabase(Settings.MySqlAddress, Settings.MySqlPort, Settings.MySqlDatabase,
                    Settings.MySqlUsername, Settings.MySqlPassword, Logger.Get("MySQL"));
            else if (Settings.UseSqLite)
                _database = new SqLiteDatabase(Settings.SqLitePath, Logger.Get("SQLite"));

            if (!_database.Connect())
                return;

            _universeAgent = new UniverseAgentListener(Settings.UniverseAgentPort, Logger.Get("UniverseAgent"),
                _database);
            _universeAgent.Start();

            _playerAgent = new PlayerAgentListener(Settings.PlayerAgentPort, Logger.Get("PlayerAgent"), _database);
            _playerAgent.Start();

            _csplayerAgent = new CsPlayerAgentListener(Settings.CsPlayerAgentPort, Logger.Get("CsPlayerAgent"),
                _database);
            _csplayerAgent.Start();

            _agentServer = new AgentServerListener(Settings.AgentServerPort, Logger.Get("AgentServer"), _database);
            _agentServer.Start();

            _gameServer = new GameServerListener(Settings.GameServerPort, Logger.Get("GameServer"), _database,
                _agentServer);
            _gameServer.Start();

            Functions.ExitLoop();
        }
    }
}