using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Helpers
{
	// builder.Logging.AddProvider(new FileLoggerProvider("logs/app.log"));
	public sealed class FileLoggerProvider : ILoggerProvider
	{
		private readonly StreamWriter _streamWriter;
		private readonly Timer _flushTimer;

		public FileLoggerProvider(string path)
		{
			// Only create a directory when the path actually names one - a bare filename ("faolan.log") writes to the
			// CWD, and Path.GetDirectoryName returns "" for it, which Directory.CreateDirectory rejects (empty path).
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			// AutoFlush is OFF: flushing on every line turns each log call into a synchronous disk write under a
			// lock, which causes severe lag spikes under the in-world packet stream (hundreds of log lines/sec).
			// Buffer the writes and flush on a timer instead - logs still appear within ~1s, without the per-line
			// disk I/O on the hot path.
			_streamWriter = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
			{
				AutoFlush = false
			};
			_flushTimer = new Timer(_ =>
			{
				lock (FileLogger.Lock)
				{
					_streamWriter.Flush();
				}
			}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
		}

		public void Dispose()
		{
			_flushTimer.Dispose();
			lock (FileLogger.Lock)
			{
				_streamWriter.Flush();
			}

			_streamWriter.Dispose();
		}

		public ILogger CreateLogger(string categoryName)
		{
			return new FileLogger(categoryName, _streamWriter);
		}
	}

	public sealed class FileLogger : ILogger
	{
		internal static readonly Lock Lock = new Lock();
		private readonly TextWriter _writer;
		private readonly string _categoryName;

		public FileLogger(string categoryName, TextWriter writer)
		{
			_categoryName = categoryName;
			_writer = writer;
		}

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return logLevel >= LogLevel.Information;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			//if (!IsEnabled(logLevel))
			//	return;

			var message = formatter(state, exception);

			lock (Lock)
			{
				_writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_categoryName}: {message}");

				if (exception != null)
					_writer.WriteLine(exception);
			}
		}
	}
}
