using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;

namespace Faolan
{
    internal static class Arguments
    {
        private static readonly Dictionary<string, Action<string>> Actions = new Dictionary<string, Action<string>>
        {
            {"configPath", value => ConfigPath = value},
            {"writeLogFile", value => WriteLogToFile = bool.Parse(value)}
        };

        public static bool WriteLogToFile { get; private set; } = true;
        public static string ConfigPath { get; private set; } = "faolan.ini";

        public static string[] Parameters => Actions.Keys.ToArray();

        public static void Parse(string[] args)
        {
            foreach (var i in args.Where(a => a.StartsWith("-")))
            {
                var paramName = i.Substring(1).Split(':')[0];
                var paramValue = i.Substring(2 + paramName.Length);

                if (!Actions.ContainsKey(paramName))
                {
                    Logger.Get("Config").Warning($"Unknown parameter: {i}");
                    continue;
                }

                Actions[paramName].Invoke(paramValue);
            }
        }
    }
}