using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Faolan.Core
{
	// builder.Logging.AddProvider(new FileLoggerProvider("logs/app.log"));
	public sealed class FileLoggerProvider : ILoggerProvider
	{
		private readonly StreamWriter _streamWriter;

		public FileLoggerProvider(string path)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);

			_streamWriter = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
		}

		public void Dispose()
		{
			_streamWriter.Dispose();
		}

		public ILogger CreateLogger(string categoryName)
		{
			return new FileLogger(categoryName, _streamWriter);
		}
	}

	public sealed class FileLogger : ILogger
	{
		private static readonly Lock Lock = new Lock();
		private readonly string _categoryName;
		private readonly TextWriter _writer;

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
