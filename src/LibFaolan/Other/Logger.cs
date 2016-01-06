using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace LibFaolan.Other
{
    public class Logger
    {
        private readonly ILog _logger;

        public Logger(string tag = null)
        {
            _logger = LogManager.GetLogger(tag != null ? "[" + tag + "] " : "");
        }

        public static void Setup(string path)
        {
            // "%d[%2%t] %-5p[%-10c] %m %n"
            var pl = new PatternLayout {ConversionPattern = "%c%m%n"};
            pl.ActivateOptions();

            var fileAppender = new FileAppender
            {
                AppendToFile = true,
                LockingModel = new FileAppender.MinimalLock(),
                File = path,
                Layout = pl
            };
            fileAppender.ActivateOptions();

            var bufferingForwardingAppender = new BufferingForwardingAppender
            {
                Lossy = false,
                Fix = FixFlags.None
            };
            bufferingForwardingAppender.AddAppender(fileAppender);
            bufferingForwardingAppender.ActivateOptions();

            var consoleAppender = new ConsoleAppender {Layout = pl};
            consoleAppender.ActivateOptions();

            var hierarchy = (Hierarchy) LogManager.GetRepository();
            hierarchy.Root.RemoveAllAppenders();
            BasicConfigurator.Configure(hierarchy, bufferingForwardingAppender, consoleAppender);
        }

        public void Info(string msg, params object[] args)
        {
            if (args.Length > 0)
                _logger.Info(string.Format(msg, args));
            else
                _logger.Info(msg);
        }

        public void Error(string errorMsg, params object[] args)
        {
            if (args.Length > 0)
                _logger.Error(string.Format(errorMsg, args));
            else
                _logger.Error(errorMsg);
        }

        public void Warning(string warningMsg, params object[] args)
        {
            if (args.Length > 0)
                _logger.Warn(string.Format(warningMsg, args));
            else
                _logger.Warn(warningMsg);
        }
    }
}