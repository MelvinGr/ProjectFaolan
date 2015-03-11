using System;
using System.Globalization;
using System.Windows.Forms;
using AgentServer;
using GameServer;
using LibFaolan.Config;
using LibFaolan.Database;
using LibFaolan.Other;
using PlayerAgent;
using UniverseAgent;

namespace ProjectFaolan
{
    public partial class Form1 : Form
    {
#if DEBUG
        private const string ConfigPath = "../../other/faolan.ini";
#else
        private const string ConfigPath="faolan.ini";
#endif

        private readonly Logger _logger;
        private readonly ConsoleStream _consoleStream;

        public Form1()
        {
            InitializeComponent();

            Functions.SetDefaultCulture(CultureInfo.InvariantCulture);

            _consoleStream = new ConsoleStream(richTextBox);
            _logger = GetLogger(null);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Statics.GameServer != null)
                Statics.GameServer.SaveAllCharacterData();

            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            _logger.WriteLine(LibFaolan.Other.Statics.BuildInfo);

            if (Settings.Load(ConfigPath))
                _logger.WriteLine("Loaded settings from: '" + ConfigPath + "'");
            else
            {
                _logger.WriteLine("Failed to load settings from: '" + ConfigPath + "'");
            }
        }

        private Logger GetLogger(string tag) => new Logger(tag) {TextWriter = _consoleStream};

        private void databaseConnectButton_Click(object sender, EventArgs e)
        {
            var logger = GetLogger("[Database] ");

            if (Settings.UseMysql)
            {
                Statics.Database = new MySqlDatabase(Settings.MySqlAddress, Settings.MySqlPort, Settings.MySqlDatabase,
                    Settings.MySqlUsername, Settings.MySqlPassword, logger);
            }
            else if (Settings.UseSqLite)
                Statics.Database = new SqLiteDatabase(Settings.SqLitePath, logger);

            if (!Statics.Database.Connect())
                return;

            databaseConnectButton.Enabled = false;

            logger = GetLogger("[UniverseAgent] ");
            Statics.UniverseAgent = new UniverseAgentListener(Settings.UniverseAgentPort, logger, Statics.Database);
            Statics.UniverseAgent.Start();

            logger = GetLogger("[PlayerAgent] ");
            Statics.PlayerAgent = new PlayerAgentListener(Settings.PlayerAgentPort, logger, Statics.Database);
            Statics.PlayerAgent.Start();

            //logger = GetLogger("[CsPlayerAgent] ");
            //Statics.CsplayerAgent = new CsPlayerAgentListener(Settings.CsPlayerAgentPort, logger, Statics.Database);
            //Statics.CsplayerAgent.Start();

            logger = GetLogger("[AgentServer] ");
            Statics.AgentServer = new AgentServerListener(Settings.AgentServerPort, logger, Statics.Database);
            Statics.AgentServer.Start();

            logger = GetLogger("[GameServer] ");
            Statics.GameServer = new GameServerListener(Settings.GameServerPort, logger, Statics.Database,
                Statics.AgentServer);
            Statics.GameServer.Start();
        }
    }
}