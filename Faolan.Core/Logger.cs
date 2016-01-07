using System;
using System.Collections.Generic;
using System.IO;

namespace Faolan.Core
{
    public sealed class Logger
    {
        [Flags]
        public enum MessageType
        {
            Info = 2,
            Warning = 4,
            Error = 6
        }

        private static StreamWriter _logWriter;
        private static FileStream _logStream;

        public static readonly Dictionary<string, Logger> Loggers = new Dictionary<string, Logger>();
        private readonly string _tag;

        public Logger(string tag)
        {
            _tag = tag;
        }

        ~Logger()
        {
            _logWriter = null;
            _logStream = null;
        }

        /* public Logger this[string tag]
        {
            get
            {
                if (Loggers.ContainsKey(tag))
                    return Loggers[tag];

                return Loggers[tag] = new Logger(tag); //LogManager.GetLogger(tag != "" ? $"[{tag}] " : ""));
            }
        } */

        public static Logger Get(string tag = "")
        {
            if (Loggers.ContainsKey(tag))
                return Loggers[tag];

            return Loggers[tag] = new Logger(tag);
        }

        public static void SetupFile(string path)
        {
            if (path == null)
                return;

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                _logStream = File.OpenWrite(path);
                _logWriter = new StreamWriter(_logStream) {AutoFlush = true};
            }
            catch (Exception e)
            {
                _logWriter = null;
                Get("Settings").Error(e.Message);
            }
        }

        public void Print(string msg, MessageType type)
        {
            var line = $"[{_tag}, {type}]: {msg}";

            Console.WriteLine(line);
            _logWriter?.WriteLine(line);
        }

        public void InfoNoFormat(string msg)
        {
            Console.WriteLine(msg);
            _logWriter?.WriteLine(msg);
        }

        public void Info(string msg, params object[] args)
        {
            Print(args.Length > 0 ? string.Format(msg, args) : msg, MessageType.Info);
        }

        public void Warning(string msg, params object[] args)
        {
            Print(args.Length > 0 ? string.Format(msg, args) : msg, MessageType.Warning);
        }

        public void Error(string msg, params object[] args)
        {
            Print(args.Length > 0 ? string.Format(msg, args) : msg, MessageType.Error);
        }
    }
}