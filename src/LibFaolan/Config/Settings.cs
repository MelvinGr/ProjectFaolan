using System;
using LibFaolan.Other;

namespace LibFaolan.Config
{
    public sealed class Settings
    {
        private static Ini _ini;
        public static string UniverseAgentAddress;
        public static ushort UniverseAgentPort;
        public static string PlayerAgentAddress;
        public static ushort PlayerAgentPort;
        public static string CsPlayerAgentAddress;
        public static ushort CsPlayerAgentPort;
        public static string AgentServerAddress;
        public static ushort AgentServerPort;
        public static string GameServerAddress;
        public static ushort GameServerPort;
        public static uint CharacterSlots;
        public static bool UseMysql;
        public static string MySqlAddress;
        public static ushort MySqlPort;
        public static string MySqlDatabase;
        public static string MySqlUsername;
        public static string MySqlPassword;
        public static bool UseSqLite;
        public static string SqLitePath;
        public static string WelcomeString;

        public static string InGameWelcomeString => WelcomeString
            .Replace("$BuildDate$", Statics.BuildDate)
            .Replace("$BuildHash$", Statics.BuildHash 
                + (Statics.BuildDirty ? " (dirty)" : "")
                + " (" + Statics.AssemblyConfiguration + ")")
            .Replace("\r", "")
            .Replace("\n", "<br />");

        public static bool Load(string path)
        {
            try
            {
                _ini = new Ini(path);
                _ini.Load();

                UniverseAgentAddress = _ini.GetValue("UniverseAgentAddress");
                UniverseAgentPort = UInt16.Parse(_ini.GetValue("UniverseAgentPort"));

                PlayerAgentAddress = _ini.GetValue("PlayerAgentAddress");
                PlayerAgentPort = UInt16.Parse(_ini.GetValue("PlayerAgentPort"));

                CsPlayerAgentAddress = _ini.GetValue("CSPlayerAgentAddress");
                CsPlayerAgentPort = UInt16.Parse(_ini.GetValue("CSPlayerAgentPort"));

                AgentServerAddress = _ini.GetValue("AgentServerAddress");
                AgentServerPort = UInt16.Parse(_ini.GetValue("AgentServerPort"));

                GameServerAddress = _ini.GetValue("GameServerAddress");
                GameServerPort = UInt16.Parse(_ini.GetValue("GameServerPort"));

                CharacterSlots = UInt32.Parse(_ini.GetValue("CharacterSlots"));

                UseMysql = _ini.GetValue("UseMySQL") == "1";
                if (UseMysql)
                {
                    MySqlAddress = _ini.GetValue("MySqlAddress");
                    MySqlPort = UInt16.Parse(_ini.GetValue("MySqlPort"));
                    MySqlDatabase = _ini.GetValue("MySqlDatabase");
                    MySqlUsername = _ini.GetValue("MySqlUsername");
                    MySqlPassword = _ini.GetValue("MySqlPassword");
                }

                UseSqLite = _ini.GetValue("UseSQLite") == "1";
                if (UseSqLite)
                {
                    SqLitePath = _ini.GetValue("SQLitePath");
                }

                WelcomeString = _ini.GetValue("WelcomeString");

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Save()
        {
            try
            {
                _ini.Save();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}