using System;
using System.Windows.Forms;
using LibFaolan.Config;
using LibFaolan.Database;
using LibFaolan.Network;
using LibFaolan.Other;

namespace ProjectFaolan
{
    public partial class Form1 : Form
    {
#if DEBUG
        private const string ConfigPath = "../../other/faolan.ini";
#else
        private const string ConfigPath = "faolan.ini";
#endif

        private readonly Logger _logger;
        private readonly ConsoleStream _consoleStream;

        public Form1()
        {
            InitializeComponent();

            _consoleStream = new ConsoleStream(richTextBox);
            _logger = GetLogger(null);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (Settings.Load(ConfigPath))
                _logger.WriteLine("Loaded settings from: '" + ConfigPath + "'");
            else
            {
                _logger.WriteLine("Failed to load settings from: '" + ConfigPath + "'");
            }
        }

        private void databaseConnectButton_Click(object sender, EventArgs e)
        {
            Statics.MySqlDatabase = new MySqlDatabase(Settings.MySqlAddress, Settings.MySqlPort, Settings.MySqlDatabase,
                Settings.MySqlUsername, Settings.MySqlPassword);
            if (Statics.MySqlDatabase.Connect())
                _logger.WriteLine("Connected to database");
            else
            {
                _logger.WriteLine("Failed to connect to database");
            }
        }

        private Logger GetLogger(string tag) => new Logger(tag) {TextWriter = _consoleStream};

        private void universeAgentButton_Click(object sender, EventArgs e)
            => StartServer(Settings.UniverseAgentPort, out Statics.UniverseAgent);

        private void playerAgentButton_Click(object sender, EventArgs e)
            => StartServer(Settings.PlayerAgentPort, out Statics.PlayerAgent);

        private void csPlayerAgentButton_Click(object sender, EventArgs e)
            => StartServer(Settings.CSPlayerAgentPort, out Statics.CsplayerAgent);

        private void agentServerButton_Click(object sender, EventArgs e)
            => StartServer(Settings.AgentServerPort, out Statics.AgentServer);

        private void gameServerButton_Click(object sender, EventArgs e)
            => StartServer(Settings.GameServerPort, out Statics.GameServer);

        private bool StartServer<T>(ushort port, out T ret) where T : Server
        {
            var logger = GetLogger("[" + typeof (T).Name + "] ");
            var server = (T) Activator.CreateInstance(typeof (T), port, logger, Statics.MySqlDatabase);
            if (server.Start())
            {
                logger.WriteLine("Started on port: " + server.Port);
                ret = server;
                return true;
            }

            logger.WriteLine("Failed to start on port: " + server.Port);
            ret = null;
            return false;
        }
    }
}