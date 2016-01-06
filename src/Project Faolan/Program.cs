using System;
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
using Console = LibFaolan.DllImport.Console;

namespace ProjectFaolan
{
    internal class Program
    {
#if DEBUG
        private const string ConfigPath = "../../other/faolan.ini";
#else
        private const string ConfigPath="faolan.ini";
#endif

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
        private static void Main()
        {
            Console.EnableQuickEditMode();
            //Console.SetConsoleCtrlHandler(ConsoleCtrlHandler, true); // Causes crash??
            Console.SetDefaultCulture(CultureInfo.InvariantCulture);

            Logger.Setup("ProjectFolan_" + Other.time() + ".log");

            new Logger().Info(Statics.Banner);

            if (Settings.Load(ConfigPath))
                new Logger("Settings").Info("Loaded from: '" + ConfigPath + "'");
            else
            {
                new Logger("Settings").Info("Failed to load from: '" + ConfigPath + "'");
                return;
            }

            if (Settings.UseMysql)
            {
                _database = new MySqlDatabase(Settings.MySqlAddress, Settings.MySqlPort, Settings.MySqlDatabase,
                    Settings.MySqlUsername, Settings.MySqlPassword, new Logger("MySQL"));
            }
            else if (Settings.UseSqLite)
                _database = new SqLiteDatabase(Settings.SqLitePath, new Logger("SQLite"));

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